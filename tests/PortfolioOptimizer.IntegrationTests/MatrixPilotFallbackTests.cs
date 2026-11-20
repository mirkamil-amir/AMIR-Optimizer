using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Pilot;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Llm;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Matrix Pilot's fallback models (docs/matrix-pilot.md › Models): a cycle moves to the next model only while no model has
/// answered, so exactly one model makes each decision; the owner confirms the list; every switch is journaled and the model
/// that decided is recorded on the cycle. Scripted models (test scripts, not AIs) against the real API and PostgreSQL.
/// </summary>
public sealed class MatrixPilotFallbackTests(MatrixPilotFallbackTests.Shared shared) : IClassFixture<MatrixPilotFallbackTests.Shared>, IAsyncLifetime
{
    public sealed class Shared : SharedApi
    {
        public TestChain Chain { get; } = new();

        protected override Task<ApiFactory> CreateFactoryAsync() =>
            ApiFactory.CreateAsync(testServices: s => s.AddSingleton<IPilotLanguageModel>(Chain));

        protected override Task SeedAsync(HttpClient admin) => SyncAsync(admin, 3, [.. Stocks, "SPY"]);
    }

    /// <summary>The configured list, changeable per test: a scripted primary and scripted (or unusable) fallbacks.</summary>
    public sealed class TestChain : IPilotLanguageModel
    {
        public MockLanguageModel Primary { get; set; } = new();
        public List<ILanguageModel> List { get; } = [];
        public string Provider => Primary.Provider;
        public string Model => Primary.Model;
        public bool IsAvailable => Primary.IsAvailable;
        public IReadOnlyList<ILanguageModel> Fallbacks => List;
        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct) => Primary.GenerateAsync(request, ct);
    }

    private static readonly string[] Stocks = ["AAPL", "MSFT", "NVDA", "JPM", "BAC", "PG", "KO", "JNJ", "XOM", "CVX"];
    private readonly ApiFactory _factory = shared.Factory;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;
    private MockLanguageModel _primary = null!;
    private MockLanguageModel _fallback = null!;

    public async Task InitializeAsync()
    {
        _primary = shared.Chain.Primary = new MockLanguageModel();
        _fallback = new MockLanguageModel("mock-fallback");
        shared.Chain.List.Clear();
        shared.Chain.List.Add(_fallback);
        _admin = await _factory.CreateAdminSessionAsync();
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        return Task.CompletedTask;
    }

    private static PilotMandate Mandate() => new()
    {
        Universe = [.. Stocks], MaxPositionWeight = 0.10, MinPositionWeight = 0.02, MinHoldings = 5, MaxHoldings = 10,
        MinCashWeight = 0.01, MaxCashWeight = 0.30, MaxTurnover = 1.0, MinTradeValue = 100m,
    };

    private async Task<PortfolioDto> ActivePilotAsync()
    {
        var p = await (await _user.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest
        {
            Name = "Fallback test", BaseCurrency = "USD", InitialInvestment = 100_000m, Universe = [.. Stocks],
        })).ReadAsync<PortfolioDto>();
        (await _user.PostJsonAsync($"/api/v1/portfolios/{p.Id}/pilot", new CreatePilotRequest { Mandate = Mandate(), Frequency = PilotDecisionFrequency.Weekly }))
            .EnsureStatus(HttpStatusCode.Created);
        (await _user.PostJsonAsync($"/api/v1/portfolios/{p.Id}/pilot/enable", new EnablePilotRequest
        {
            Confirm = true, Provider = "Mock", Model = "mock-model", Fallbacks = PilotModelChain.Key(shared.Chain.Fallbacks),
        })).EnsureStatus(HttpStatusCode.OK);
        return p;
    }

    private async Task<MatrixPilotCycle> RunCycleAsync(Guid portfolioId, string? key = null)
    {
        Guid cycleId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var pilot = await db.MatrixPilots.AsNoTracking().SingleAsync(x => x.PortfolioId == portfolioId);
            var cycle = await PilotCycles.TryCreateAsync(db, pilot, "Scheduled", DateTime.UtcNow, default, key);
            Assert.NotNull(cycle);
            cycleId = cycle.Id;
        }
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PilotEngine>().ExecuteAsync(cycleId, default);
        using (var scope = _factory.Services.CreateScope())
            return await scope.ServiceProvider.GetRequiredService<IApplicationDbContext>().MatrixPilotCycles.AsNoTracking().Include(c => c.Steps)
                .SingleAsync(c => c.Id == cycleId);
    }

    private static int DecisionCalls(MockLanguageModel m) => m.Requests.Count(r => r.Purpose != "pilot.check");

    [Fact]
    public async Task PrimaryFailingBeforeAnyAnswer_HandsTheWholeDecisionToTheNextModel()
    {
        var p = await ActivePilotAsync();
        _primary.EnqueueFailure(LlmFailure.ProviderError); // e.g. HTTP 503 after its retries

        var cycle = await RunCycleAsync(p.Id);

        Assert.Equal(PilotCycleOutcome.Rebalanced, cycle.Outcome);
        // The model that decided is the one on record; the primary made exactly the one failed call and nothing else.
        Assert.Equal(("Mock", "mock-fallback"), (cycle.Provider, cycle.Model));
        Assert.Equal(1, DecisionCalls(_primary));
        Assert.True(DecisionCalls(_fallback) > 1);
        var switched = Assert.Single(cycle.Steps, s => s.Name == "Model fallback");
        Assert.Contains("Mock mock-model failed before answering (ProviderError)", switched.Summary);
        Assert.Contains("Mock mock-fallback, which makes the whole decision", switched.Summary);
        Assert.Contains(cycle.Steps, s => s.Kind == PilotStepKind.AiDecision && s.Status == "error" && s.Summary.StartsWith("Mock mock-model"));
    }

    [Fact]
    public async Task OnceAModelHasAnswered_ItIsNeverReplacedMidCycle()
    {
        var p = await ActivePilotAsync();
        // The first call ("Rules first") is answered; the research call then fails.
        _primary.Enqueue("pilot.research", _ => throw new LanguageModelException(LlmFailure.ProviderError, "Simulated 503."));

        var cycle = await RunCycleAsync(p.Id);

        Assert.Equal(PilotCycleOutcome.Failed, cycle.Outcome);
        Assert.Equal("mock-model", cycle.Model);
        Assert.Equal(0, DecisionCalls(_fallback));
        Assert.DoesNotContain(cycle.Steps, s => s.Name == "Model fallback");
        Assert.Equal(0, cycle.TradesBooked);
    }

    [Fact]
    public async Task WhenEveryModelFails_TheCycleFailsClosed_NamingTheLastOneTried()
    {
        var p = await ActivePilotAsync();
        _primary.EnqueueFailure(LlmFailure.ProviderError);
        _fallback.EnqueueFailure(LlmFailure.RateLimited);

        var cycle = await RunCycleAsync(p.Id);

        Assert.Equal(PilotCycleOutcome.Failed, cycle.Outcome);
        Assert.Equal(0, cycle.TradesBooked);
        Assert.StartsWith("No model of the list answered; the last one tried (Mock mock-fallback) failed (RateLimited)", cycle.OutcomeReason);
    }

    [Fact]
    public async Task AFallbackWithoutKey_IsSkipped_AndShownAsUnusableInTheCheck()
    {
        shared.Chain.List.Insert(0, new UnconfiguredLanguageModel("Groq", "some-model", "No Groq API key is configured."));
        var check = await (await _user.PostJsonAsync("/api/v1/pilots/model-check", new { })).ReadAsync<PilotModelCheckDto>();
        Assert.True(check.Usable);
        Assert.Equal(["not_configured", "ok"], check.Fallbacks.Select(f => f.Status));
        Assert.Contains("GROQ_API_KEY", check.Fallbacks[0].Message);

        var p = await ActivePilotAsync();
        _primary.EnqueueFailure(LlmFailure.Timeout);
        var cycle = await RunCycleAsync(p.Id);

        Assert.Equal(PilotCycleOutcome.Rebalanced, cycle.Outcome);
        Assert.Equal("mock-fallback", cycle.Model);
        Assert.Contains(cycle.Steps, s => s.Name == "Model fallback" && s.Status == "skipped" && s.Summary.Contains("Groq some-model"));
    }

    [Fact]
    public async Task AChangedList_StopsCycles_UntilTheOwnerConfirmsItInTheMandate()
    {
        var p = await ActivePilotAsync();
        shared.Chain.List.Add(new MockLanguageModel("mock-second"));

        var blocked = await RunCycleAsync(p.Id);
        Assert.Equal(PilotCycleOutcome.Failed, blocked.Outcome);
        Assert.Contains("set up with Mock mock-model, then Mock mock-fallback", blocked.OutcomeReason);
        Assert.Equal(0, DecisionCalls(_primary));

        var overview = await (await _user.GetAsync($"/api/v1/portfolios/{p.Id}/pilot")).ReadAsync<PilotOverviewDto>();
        Assert.False(overview.Pilot!.ModelMatchesConfiguration);
        Assert.Equal(["mock-fallback", "mock-second"], overview.ConfiguredModel.Fallbacks.Select(f => f.Model));
        Assert.Equal(["mock-fallback"], overview.Pilot.Fallbacks.Select(f => f.Model));

        // Resuming or switching on with the old list is refused; the mandate change adopts the new list (a new version).
        var stale = await _user.PostJsonAsync($"/api/v1/portfolios/{p.Id}/pilot/kill", new PilotReasonRequest { Reason = "test" });
        stale.EnsureStatus(HttpStatusCode.OK);
        (await _user.PostJsonAsync($"/api/v1/portfolios/{p.Id}/pilot/resume", new EnablePilotRequest
        {
            Confirm = true, Provider = "Mock", Model = "mock-model", Fallbacks = "Mock:mock-fallback",
        })).EnsureStatus(HttpStatusCode.Conflict);
        var adopted = await (await _user.PutJsonAsync($"/api/v1/portfolios/{p.Id}/pilot/mandate", new UpdatePilotMandateRequest
        {
            Mandate = Mandate(), Frequency = PilotDecisionFrequency.Weekly, Effort = overview.Pilot.Effort, Reason = "New fallback", Confirm = true, AdoptConfiguredModel = true,
        })).ReadAsync<PilotDto>();
        Assert.True(adopted.ModelMatchesConfiguration);
        Assert.Equal(overview.Pilot.MandateVersion + 1, adopted.MandateVersion);
        (await _user.PostJsonAsync($"/api/v1/portfolios/{p.Id}/pilot/resume", new EnablePilotRequest
        {
            Confirm = true, Provider = "Mock", Model = "mock-model", Fallbacks = "Mock:mock-fallback;Mock:mock-second",
        })).EnsureStatus(HttpStatusCode.OK);

        var decided = await RunCycleAsync(p.Id, $"{blocked.CycleKey}#2");
        Assert.Equal(PilotCycleOutcome.Rebalanced, decided.Outcome);
        Assert.Equal("mock-model", decided.Model);
    }
}
