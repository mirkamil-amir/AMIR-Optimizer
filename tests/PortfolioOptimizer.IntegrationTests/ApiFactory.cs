using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Hosts the real API in-process against a temporary SQLite file and the deterministic mock data provider
/// (no network access). Each factory instance gets its own database unless a path is supplied.
/// </summary>
public sealed class ApiFactory(string? databasePath = null) : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = databasePath ?? Path.Combine(Path.GetTempPath(), $"po-it-{Guid.NewGuid():N}.db");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");
        builder.UseSetting("MarketData:DefaultProvider", "Mock");
        builder.UseSetting("MarketData:ScheduledRefreshEnabled", "false");
        builder.UseSetting("MarketData:Yahoo:Enabled", "false");
        builder.UseSetting("Seed:Assets", "true");
        builder.UseSetting("Seed:DemoPortfolio", "false");
        builder.UseSetting("Seed:SyncOnEmptyDatabase", "false");
        builder.UseSetting("RateLimiting:ExpensivePermitPerMinute", "1000");
    }

    public void DeleteDatabase()
    {
        foreach (var f in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"HTTP {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, ApiFactory.Json)!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiFactory.Json);
}
