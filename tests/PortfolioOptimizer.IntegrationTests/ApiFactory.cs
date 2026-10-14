using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// Hosts the real API in-process against a dedicated PostgreSQL database (see <see cref="PostgresTestServer"/>) and the
/// deterministic mock data provider (no network access). Pass an existing connection string to simulate a restart.
/// An administrator is bootstrapped from configuration (<see cref="AdminEmail"/> / <see cref="AdminPassword"/>).
/// </summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin-Passw0rd-Test";
    public const string UserPassword = "User-Passw0rd-Test";

    public string ConnectionString { get; } = connectionString;

    /// <summary>Whether signing in starts the background quote refresh cycle (off by default for deterministic tests).</summary>
    public bool RefreshOnSignIn { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<ApiFactory> CreateAsync(bool refreshOnSignIn = false) =>
        new(await PostgresTestServer.CreateDatabaseAsync()) { RefreshOnSignIn = refreshOnSignIn };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Admin:Email", AdminEmail);
        builder.UseSetting("Admin:Password", AdminPassword);
        builder.UseSetting("MarketData:DefaultProvider", "Mock");
        builder.UseSetting("MarketData:ScheduledRefreshEnabled", "false");
        // Tests that need the sign-in refresh cycle enable it explicitly (keeps other tests deterministic).
        builder.UseSetting("MarketData:Quotes:RefreshOnSignIn", RefreshOnSignIn ? "true" : "false");
        builder.UseSetting("MarketData:Yahoo:Enabled", "false");
        builder.UseSetting("Seed:Assets", "true");
        builder.UseSetting("Seed:DemoPortfolio", "false");
        builder.UseSetting("Seed:SyncOnEmptyDatabase", "false");
        builder.UseSetting("RateLimiting:ExpensivePermitPerMinute", "1000");
        builder.UseSetting("RateLimiting:AuthenticationPermitPerMinute", "1000");
        builder.UseSetting("ModelArtifacts:RootPath", Path.Combine(Path.GetTempPath(), $"po-it-artifacts-{Guid.NewGuid():N}"));
    }

    public Task DeleteDatabaseAsync() => PostgresTestServer.DropDatabaseAsync(ConnectionString);

    /// <summary>A browser-like client: keeps cookies and sends the antiforgery header on unsafe requests.</summary>
    public HttpClient CreateSession() => CreateDefaultClient(new BrowserSessionHandler());

    public async Task<HttpClient> CreateAdminSessionAsync()
    {
        var client = CreateSession();
        await client.LoginAsync(AdminEmail, AdminPassword);
        return client;
    }

    /// <summary>Registers a new account, has the administrator approve it and returns a signed-in session.</summary>
    public async Task<(HttpClient Client, CurrentUserDto User)> CreateApprovedUserAsync(HttpClient admin, string? email = null)
    {
        email ??= $"user-{Guid.NewGuid():N}@test.local";
        var client = CreateSession();
        (await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest { Email = email, Password = UserPassword, DisplayName = "Test User" }))
            .EnsureStatus(HttpStatusCode.Accepted);
        var pending = await client.LoginAsync(email, UserPassword);
        Assert.Equal(UserAccountStatus.Pending, pending.Status);
        (await admin.PutJsonAsync($"/api/v1/admin/users/{pending.Id}/status", new UpdateUserStatusRequest { Status = UserAccountStatus.Approved }))
            .EnsureStatus(HttpStatusCode.OK);
        var me = await (await client.GetAsync("/api/v1/auth/me")).ReadAsync<CurrentUserDto>();
        return (client, me);
    }
}

/// <summary>
/// Cookie jar + antiforgery for tests: on POST/PUT/PATCH/DELETE it sends the XSRF-TOKEN cookie value as X-XSRF-TOKEN,
/// fetching a token first if needed. Tokens are bound to the signed-in identity, so they are discarded after login/logout.
/// </summary>
public sealed class BrowserSessionHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && !request.Headers.Contains("X-XSRF-TOKEN"))
        {
            var token = Cookie(request.RequestUri!, "XSRF-TOKEN") ?? await FetchTokenAsync(request.RequestUri!, ct);
            request.Headers.Add("X-XSRF-TOKEN", token);
        }
        var response = await SendWithCookiesAsync(request, ct);
        var path = request.RequestUri!.AbsolutePath;
        if (response.IsSuccessStatusCode && (path.EndsWith("/auth/login") || path.EndsWith("/auth/logout")))
            Expire(request.RequestUri!, "XSRF-TOKEN");
        return response;
    }

    private async Task<string> FetchTokenAsync(Uri baseUri, CancellationToken ct)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/api/v1/auth/antiforgery"));
        using var response = await SendWithCookiesAsync(get, ct);
        response.EnsureSuccessStatusCode();
        return Cookie(baseUri, "XSRF-TOKEN") ?? throw new InvalidOperationException("No XSRF-TOKEN cookie issued.");
    }

    private async Task<HttpResponseMessage> SendWithCookiesAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var header = _cookies.GetCookieHeader(request.RequestUri!);
        if (!string.IsNullOrEmpty(header)) request.Headers.Add("Cookie", header);
        var response = await base.SendAsync(request, ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var v in values) _cookies.SetCookies(request.RequestUri!, v);
        return response;
    }

    private string? Cookie(Uri uri, string name) => _cookies.GetCookies(uri)[name]?.Value is { Length: > 0 } v ? Uri.UnescapeDataString(v) : null;

    private void Expire(Uri uri, string name)
    {
        if (_cookies.GetCookies(uri)[name] is { } c) c.Expired = true;
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

    public static HttpResponseMessage EnsureStatus(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new Xunit.Sdk.XunitException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}: {response.Content.ReadAsStringAsync().Result}");
        return response;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiFactory.Json);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, ApiFactory.Json);

    public static async Task<CurrentUserDto> LoginAsync(this HttpClient client, string email, string password) =>
        await (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest { Email = email, Password = password })).ReadAsync<CurrentUserDto>();

    /// <summary>Downloads simulated history through the real background job pipeline and waits for completion.</summary>
    public static async Task SyncAndWaitAsync(this HttpClient client, SyncRequest request)
    {
        var job = await (await client.PostJsonAsync("/api/v1/market-data/sync", request)).ReadAsync<SyncJobDto>();
        for (var i = 0; i < 300; i++)
        {
            var status = await (await client.GetAsync($"/api/v1/market-data/jobs/{job.Id}")).ReadAsync<SyncJobDto>();
            if (status.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.PartiallySucceeded)
            {
                Assert.Equal(JobStatus.Succeeded, status.Status);
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Market data sync did not finish.");
    }
}
