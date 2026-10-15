using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Infrastructure.MarketData;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Company descriptions: catalogue reference → provider → cache → API, with the unavailable and error paths.</summary>
public sealed class CompanyProfileTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
        (_user, _) = await _factory.CreateApprovedUserAsync(_admin);
    }

    public async Task DisposeAsync()
    {
        _user.Dispose();
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    private Task<CompanyProfileDto> Profile(string symbol) => _user.GetAsync($"/api/v1/stocks/{symbol}/profile").ContinueWith(t => t.Result.ReadAsync<CompanyProfileDto>()).Unwrap();

    [Fact]
    public async Task SeededStock_GetsItsDescription_FromTheLinkedArticle_AndIsCached()
    {
        var mock = _factory.Services.GetRequiredService<MockProfileProvider>();
        var before = mock.Calls;
        var p = await Profile("AAPL");
        Assert.Equal("Available", p.Status);
        Assert.Contains("Apple Inc.", p.Description); // resolved from the catalogue reference "en:Apple Inc."
        Assert.True(p.IsSimulated);
        Assert.NotNull(p.RetrievedAtUtc);
        Assert.NotNull(p.SourceUrl);
        Assert.Equal(before + 1, mock.Calls);

        // Next/Previous back and forth: served from the cache, no further provider request.
        await Profile("AAPL");
        await Profile("aapl");
        Assert.Equal(before + 1, mock.Calls);
    }

    [Fact]
    public async Task NoArticle_Index_NotFound_AndErrors_AreReportedHonestly()
    {
        // Admin import of a small test universe with a missing article, a failing reference and no reference at all.
        (await _admin.PostJsonAsync("/api/v1/admin/catalogue/universes/PROFILETEST", new ImportUniverseRequest
        {
            Csv = "symbol,name,country,currency,wiki\nZZMA,Missing Co,US,USD,en:Missing Company\nZZFA,Failing Co,US,USD,en:Failing Company\nZZNO,No Ref Co,US,USD,",
            Name = "Profile test", Country = "US", Currency = "USD", Source = "integration test",
        })).EnsureStatus(HttpStatusCode.OK);

        var missing = await Profile("ZZMA");
        Assert.Equal("NotFound", missing.Status);
        Assert.Null(missing.Description);
        Assert.Contains("unavailable", missing.Message);

        var mock = _factory.Services.GetRequiredService<MockProfileProvider>();
        var failing = await Profile("ZZFA");
        Assert.Equal("Error", failing.Status);
        Assert.Null(failing.Description);
        Assert.Contains("retried", failing.Message);
        var calls = mock.Calls;
        Assert.Equal("Error", (await Profile("ZZFA")).Status); // within the retry window: no new request
        Assert.Equal(calls, mock.Calls);

        var noRef = await Profile("ZZNO");
        Assert.Equal("NoReference", noRef.Status);
        Assert.Contains("unavailable", noRef.Message);

        var etf = await Profile("SPY");
        Assert.Equal("NoReference", etf.Status);

        (await _user.GetAsync("/api/v1/stocks/NOPE123/profile")).EnsureStatus(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CachedTextFromAnotherSource_IsNotServed_ByTheConfiguredSource()
    {
        // A description cached by a different source (e.g. an earlier simulated run) for the same article.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PortfolioOptimizer.Infrastructure.Persistence.AppDbContext>();
            var msft = db.Assets.Single(a => a.Symbol == "MSFT");
            db.CompanyProfiles.Add(new PortfolioOptimizer.Domain.CompanyProfile
            {
                AssetId = msft.Id, Reference = msft.ReferenceArticle!, Status = PortfolioOptimizer.Domain.CompanyProfileStatus.Available,
                Description = "Old text from another source.", SourceName = "Another source", AttemptedAtUtc = DateTime.UtcNow, RetrievedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var mock = _factory.Services.GetRequiredService<MockProfileProvider>();
        var before = mock.Calls;
        var p = await Profile("MSFT");
        Assert.Equal(before + 1, mock.Calls);
        Assert.DoesNotContain("Old text", p.Description);
        Assert.Equal("Simulated profile (tests only)", p.SourceName);
    }

    [Fact]
    public async Task Profile_RequiresSignIn_AndNeverLeaksConfiguration()
    {
        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/stocks/AAPL/profile")).StatusCode);
        var body = await (await _user.GetAsync("/api/v1/stocks/AAPL/profile")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("apikey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User-Agent", body, StringComparison.OrdinalIgnoreCase);
    }
}
