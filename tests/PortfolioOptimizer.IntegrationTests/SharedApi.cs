namespace PortfolioOptimizer.IntegrationTests;

/// <summary>
/// One database and API host for all tests of a class (an xUnit class fixture): created before the first test, with the
/// shared data of <see cref="SeedAsync"/> (usually a simulated price download), dropped after the last. Tests still sign
/// in their own sessions and create their own users, so user-owned data (portfolios, runs, watchlists) never leaks from
/// one test into the next; global data (prices, catalogue, FX rates, settings, news) is shared within the class.
/// Classes whose tests change or count global state keep a database per test.
/// </summary>
public abstract class SharedApi : IAsyncLifetime
{
    public ApiFactory Factory { get; private set; } = null!;

    /// <summary>Creates the factory (override to register test services such as a scripted provider).</summary>
    protected virtual Task<ApiFactory> CreateFactoryAsync() => ApiFactory.CreateAsync();

    /// <summary>Data every test of the class reads, created once with an administrator session.</summary>
    protected virtual Task SeedAsync(HttpClient admin) => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        Factory = await CreateFactoryAsync();
        using var admin = await Factory.CreateAdminSessionAsync();
        await SeedAsync(admin);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Factory.DeleteDatabaseAsync();
    }

    /// <summary>Downloads simulated history for <paramref name="symbols"/> (all tracked instruments when null).</summary>
    protected static Task SyncAsync(HttpClient admin, int years, params string[]? symbols) =>
        admin.SyncAndWaitAsync(symbols is { Length: > 0 }
            ? new Application.Dtos.SyncRequest { Symbols = [.. symbols], StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-years) }
            : new Application.Dtos.SyncRequest { StartDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-years) });
}
