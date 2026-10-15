using PortfolioOptimizer.Application.MarketData;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.MachineLearning;
using PortfolioOptimizer.Infrastructure.Identity;
using PortfolioOptimizer.Infrastructure.ModelArtifacts;
using PortfolioOptimizer.Infrastructure.Jobs;
using PortfolioOptimizer.Infrastructure.MarketData;
using PortfolioOptimizer.Infrastructure.News;
using PortfolioOptimizer.Infrastructure.Llm;
using PortfolioOptimizer.Application.News;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MarketDataOptions>(configuration.GetSection(MarketDataOptions.Section));
        services.Configure<QuoteRefreshOptions>(configuration.GetSection(QuoteRefreshOptions.Section));
        services.Configure<YahooFinanceOptions>(configuration.GetSection(YahooFinanceOptions.Section));
        services.Configure<AlphaVantageOptions>(configuration.GetSection(AlphaVantageOptions.Section));
        services.Configure<MockProviderOptions>(configuration.GetSection(MockProviderOptions.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.Configure<MachineLearningOptions>(configuration.GetSection(MachineLearningOptions.Section));
        services.Configure<ModelArtifactOptions>(configuration.GetSection(ModelArtifactOptions.Section));
        services.AddSingleton<IModelArtifactStore, LocalModelArtifactStore>();

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.Section));
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured. Set ConnectionStrings__{DatabaseOptions.ConnectionStringName} " +
                "(e.g. Host=localhost;Port=5432;Database=portfolio;Username=portfolio_app;Password=...).");
        var commandTimeout = configuration.GetValue($"{DatabaseOptions.Section}:CommandTimeoutSeconds", 60);
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString, sql => sql.CommandTimeout(commandTimeout)));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<UniverseSeeder>();
        services.Configure<UniverseOptions>(configuration.GetSection(UniverseOptions.Section));
        services.AddScoped<TestDataPurger>();
        AddIdentity(services, configuration);

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

        services.AddSingleton<AlphaVantageQuota>();
        services.AddSingleton<MockMarketDataProvider>();
        services.AddSingleton<IMarketDataProvider>(sp => sp.GetRequiredService<MockMarketDataProvider>());
        services.AddTransient<IMarketDataProvider>(sp => sp.GetRequiredService<YahooFinanceProvider>());
        services.AddTransient<IMarketDataProvider>(sp => sp.GetRequiredService<AlphaVantageProvider>());
        services.AddScoped<IMarketDataProviderRegistry, MarketDataProviderRegistry>();

        // Language model for the research assistant (optional; deterministic rules are used when none is configured).
        services.Configure<LanguageModelOptions>(configuration.GetSection(LanguageModelOptions.Section));
        services.AddSingleton<MockLanguageModel>();
        services.AddSingleton<ILanguageModel>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<LanguageModelOptions>>().Value;
            return o.Provider switch
            {
                "Anthropic" => ActivatorUtilities.CreateInstance<AnthropicLanguageModel>(sp),
                "Mock" => sp.GetRequiredService<MockLanguageModel>(),
                _ => new DisabledLanguageModel(),
            };
        });

        // Company descriptions: Wikipedia summary API (article from the catalogue), simulated provider for tests only.
        // The simulated provider is registered first so a test configuration that enables it takes precedence.
        services.Configure<WikipediaProfileOptions>(configuration.GetSection(WikipediaProfileOptions.Section));
        services.Configure<MockProfileOptions>(configuration.GetSection(MockProfileOptions.Section));
        AddProviderClient<WikipediaProfileProvider, WikipediaProfileOptions>(services, (o, c) => c.Timeout = Timeout.InfiniteTimeSpan,
            o => o.MinRequestIntervalMs, o => o.TimeoutSeconds);
        services.AddSingleton<MockProfileProvider>();
        services.AddTransient<ICompanyProfileProvider>(sp => sp.GetRequiredService<MockProfileProvider>());
        services.AddTransient<ICompanyProfileProvider>(sp => sp.GetRequiredService<WikipediaProfileProvider>());

        // News: Yahoo company news (unofficial), official central-bank RSS feeds, and a simulated provider for tests only.
        services.Configure<NewsOptions>(configuration.GetSection(NewsOptions.Section));
        services.Configure<YahooNewsOptions>(configuration.GetSection(YahooNewsOptions.Section));
        services.Configure<OfficialFeedOptions>(configuration.GetSection(OfficialFeedOptions.Section));
        services.Configure<MockNewsOptions>(configuration.GetSection(MockNewsOptions.Section));
        AddProviderClient<YahooNewsProvider, YahooNewsOptions>(services, (o, c) =>
        {
            c.BaseAddress = new Uri(o.BaseUrl);
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PortfolioOptimizer/1.0; personal research)");
        }, o => o.MinRequestIntervalMs, o => o.TimeoutSeconds);
        AddProviderClient<OfficialFeedProvider, OfficialFeedOptions>(services, (o, c) =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PortfolioOptimizer/1.0 (educational research; RSS reader)");
        }, o => o.MinRequestIntervalMs, o => o.TimeoutSeconds);
        services.AddSingleton<MockNewsProvider>();
        services.AddTransient<ICompanyNewsProvider>(sp => sp.GetRequiredService<YahooNewsProvider>());
        services.AddTransient<INewsFeedProvider>(sp => sp.GetRequiredService<OfficialFeedProvider>());
        services.AddTransient<ICompanyNewsProvider>(sp => sp.GetRequiredService<MockNewsProvider>());
        services.AddTransient<INewsFeedProvider>(sp => sp.GetRequiredService<MockNewsProvider>());

        services.AddSingleton<IBackgroundJobQueue, ChannelBackgroundJobQueue>();
        services.AddHostedService<BackgroundJobWorker>();
        services.AddHostedService<ScheduledMarketDataRefresh>();

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// ASP.NET Core Identity with cookie authentication for the same-origin dashboard. API semantics: unauthenticated →
    /// 401, forbidden → 403 (no login redirects). Cookies are HttpOnly and SameSite=Strict; state-changing requests also
    /// require an antiforgery header (configured in the API).
    /// </summary>
    private static void AddIdentity(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AdminBootstrapOptions>(configuration.GetSection(AdminBootstrapOptions.Section));
        services.AddDataProtection().SetApplicationName("PortfolioOptimizer").PersistKeysToDbContext<AppDbContext>();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireDigit = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.SignIn.RequireConfirmedAccount = false; // access is granted by administrator approval instead
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "po.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // HTTPS deployments get Secure cookies
            o.ExpireTimeSpan = TimeSpan.FromHours(configuration.GetValue("Authentication:SessionHours", 8));
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        // Password changes and role/status changes rotate the security stamp; other sessions re-validate within a minute.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();
        services.AddScoped<IAdminOverviewService, AdminOverviewService>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<AdminBootstrapper>();
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
