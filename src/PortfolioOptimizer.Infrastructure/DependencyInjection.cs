using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Infrastructure.Jobs;
using PortfolioOptimizer.Infrastructure.MarketData;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MarketDataOptions>(configuration.GetSection(MarketDataOptions.Section));
        services.Configure<YahooFinanceOptions>(configuration.GetSection(YahooFinanceOptions.Section));
        services.Configure<AlphaVantageOptions>(configuration.GetSection(AlphaVantageOptions.Section));
        services.Configure<MockProviderOptions>(configuration.GetSection(MockProviderOptions.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));

        var connectionString = configuration.GetConnectionString("Default") ?? "Data Source=data/portfolio.db";
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString, sql => sql.CommandTimeout(60)));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<DatabaseInitializer>();

        AddProviderClient<YahooFinanceProvider, YahooFinanceOptions>(services, (o, c) =>
        {
            c.BaseAddress = new Uri(o.BaseUrl);
            c.Timeout = Timeout.InfiniteTimeSpan; // governed by the resilience pipeline
        }, o => o.MinRequestIntervalMs, o => o.TimeoutSeconds);

        AddProviderClient<AlphaVantageProvider, AlphaVantageOptions>(services, (o, c) =>
        {
            c.BaseAddress = new Uri(o.BaseUrl);
            c.Timeout = Timeout.InfiniteTimeSpan;
        }, o => o.MinRequestIntervalMs, o => o.TimeoutSeconds);

        services.AddSingleton<MockMarketDataProvider>();
        services.AddSingleton<IMarketDataProvider>(sp => sp.GetRequiredService<MockMarketDataProvider>());
        services.AddTransient<IMarketDataProvider>(sp => sp.GetRequiredService<YahooFinanceProvider>());
        services.AddTransient<IMarketDataProvider>(sp => sp.GetRequiredService<AlphaVantageProvider>());
        services.AddScoped<IMarketDataProviderRegistry, MarketDataProviderRegistry>();

        services.AddSingleton<IBackgroundJobQueue, ChannelBackgroundJobQueue>();
        services.AddHostedService<BackgroundJobWorker>();
        services.AddHostedService<ScheduledMarketDataRefresh>();

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Typed HttpClient with: per-provider throttling (min interval between requests), retries with exponential backoff and
    /// jitter on 408/429/5xx and network errors (honouring Retry-After), per-attempt and total timeouts, and a circuit breaker.
    /// </summary>
    private static void AddProviderClient<TClient, TOptions>(
        IServiceCollection services,
        Action<TOptions, HttpClient> configure,
        Func<TOptions, int> minIntervalMs,
        Func<TOptions, int> timeoutSeconds)
        where TClient : class
        where TOptions : class
    {
        var builder = services.AddHttpClient<TClient>((sp, client) => configure(sp.GetRequiredService<IOptions<TOptions>>().Value, client));
        builder.AddStandardResilienceHandler()
            .Configure((HttpStandardResilienceOptions o, IServiceProvider sp) =>
            {
                var opts = sp.GetRequiredService<IOptions<TOptions>>().Value;
                var attempt = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds(opts)));
                o.AttemptTimeout.Timeout = attempt;
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.BackoffType = DelayBackoffType.Exponential;
                o.Retry.UseJitter = true;
                o.Retry.Delay = TimeSpan.FromSeconds(2);
                o.TotalRequestTimeout.Timeout = attempt * 5 + TimeSpan.FromSeconds(30);
                o.CircuitBreaker.SamplingDuration = attempt * 2 + TimeSpan.FromSeconds(1);
            });
        builder.AddHttpMessageHandler(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TOptions>>().Value;
            return new ThrottlingHandler(TimeSpan.FromMilliseconds(minIntervalMs(opts)), sp.GetRequiredService<TimeProvider>());
        });
    }
}
