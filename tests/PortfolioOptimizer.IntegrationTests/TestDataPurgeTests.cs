using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Identity;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Removing automated-test artifacts (accounts under the reserved .test domain) without touching genuine data.</summary>
public sealed class TestDataPurgeTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private TestDataPurger Purger(IServiceScope scope, string environment) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IModelArtifactStore>(),
        new Env(environment), scope.ServiceProvider.GetRequiredService<IAuditLog>(), NullLogger<TestDataPurger>.Instance);

    [Fact]
    public async Task Purge_RemovesOnlyTestAccounts_AndTheirData()
    {
        var (genuine, genuineUser) = await _factory.CreateApprovedUserAsync(_admin, $"student-{Guid.NewGuid():N}@university.example");
        var (tester, testerUser) = await _factory.CreateApprovedUserAsync(_admin, $"applicant-{Guid.NewGuid():N}@e2e.test");
        // The test account owns a portfolio; the genuine account too.
        await (await tester.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest { Name = "Test", InitialInvestment = 1000m, BaseCurrency = "USD" })).ReadAsync<PortfolioDto>();
        await (await genuine.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest { Name = "Mine", InitialInvestment = 1000m, BaseCurrency = "USD" })).ReadAsync<PortfolioDto>();

        using var scope = _factory.Services.CreateScope();
        var dryRun = await Purger(scope, "Development").PurgeAsync(apply: false, allowProduction: false, CancellationToken.None);
        Assert.Equal([testerUser.Email], dryRun.Accounts);
        Assert.Equal(1, dryRun.Portfolios);
        Assert.True(dryRun.AuditEvents > 0);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Id == testerUser.Id)); // dry run changed nothing

        var applied = await Purger(scope, "Development").PurgeAsync(apply: true, allowProduction: false, CancellationToken.None);
        Assert.True(applied.Applied);
        Assert.False(await db.Users.AnyAsync(u => u.Id == testerUser.Id));
        Assert.False(await db.Portfolios.AnyAsync(p => p.OwnerId == testerUser.Id));
        Assert.False(await db.AuditEvents.AnyAsync(a => a.ActorUserId == testerUser.Id || a.TargetId == testerUser.Id.ToString()));
        // Genuine user, data and audit trail remain; the purge itself is audited.
        Assert.True(await db.Users.AnyAsync(u => u.Id == genuineUser.Id));
        Assert.True(await db.Portfolios.AnyAsync(p => p.OwnerId == genuineUser.Id));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.ActorUserId == genuineUser.Id));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == AuditActions.TestDataPurged));
        Assert.True(await db.Users.AnyAsync(u => u.Email == ApiFactory.AdminEmail));
        genuine.Dispose();
        tester.Dispose();
    }

    [Fact]
    public async Task Purge_RefusesProduction_UnlessExplicitlyAllowed()
    {
        using var scope = _factory.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Purger(scope, "Production").PurgeAsync(apply: true, allowProduction: false, CancellationToken.None));
        var allowed = await Purger(scope, "Production").PurgeAsync(apply: false, allowProduction: true, CancellationToken.None);
        Assert.False(allowed.Applied);
    }

    [Theory]
    [InlineData("applicant-1@e2e.test", true)]
    [InlineData("someone@example.test", true)]
    [InlineData("admin@example.com", false)]
    [InlineData("user@test.local", false)]
    [InlineData("student@latest.de", false)]
    [InlineData("", false)]
    public void TestEmailPolicy_UsesTheReservedTestDomainOnly(string email, bool isTest) =>
        Assert.Equal(isTest, TestDataPolicy.IsTestEmail(email));

    [Fact]
    public async Task AdminViews_ExcludeTestAccounts_AndAreAdminOnly()
    {
        var (genuine, _) = await _factory.CreateApprovedUserAsync(_admin, $"student-{Guid.NewGuid():N}@university.example");
        var (tester, testerUser) = await _factory.CreateApprovedUserAsync(_admin, $"applicant-{Guid.NewGuid():N}@e2e.test");
        await (await tester.PostJsonAsync("/api/v1/portfolios", new CreatePortfolioRequest { Name = "Test", InitialInvestment = 1000m, BaseCurrency = "USD" })).ReadAsync<PortfolioDto>();

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await genuine.GetAsync("/api/v1/admin/overview")).StatusCode);

        var overview = await (await _admin.GetAsync("/api/v1/admin/overview")).ReadAsync<AdminOverviewDto>();
        Assert.Equal(1, overview.Accounts.TestAccounts);
        Assert.Equal(0, overview.Activity.Portfolios); // the test account's portfolio is not counted
        Assert.Contains(overview.MarketData.Indices, i => i.Code == "DAX40" && i.Members == 40);
        Assert.Contains(overview.MarketData.Providers, p => p.Name == "Mock" && p.IsConfigured);
        var raw = await (await _admin.GetAsync("/api/v1/admin/overview")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);

        var list = await (await _admin.GetAsync("/api/v1/admin/users?pageSize=200")).ReadAsync<PagedResult<UserSummaryDto>>();
        Assert.DoesNotContain(list.Items, u => u.Id == testerUser.Id);
        var withTests = await (await _admin.GetAsync("/api/v1/admin/users?pageSize=200&includeTestAccounts=true")).ReadAsync<PagedResult<UserSummaryDto>>();
        Assert.True(Assert.Single(withTests.Items, u => u.Id == testerUser.Id).IsTestAccount);
        genuine.Dispose();
        tester.Dispose();
    }
}
