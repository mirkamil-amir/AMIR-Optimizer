using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using PortfolioOptimizer.Api.Infrastructure;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Infrastructure;
using PortfolioOptimizer.Infrastructure.Persistence;

// One-shot commands (run with the privileged migration identity, then exit):
//   migrate                                   apply EF Core migrations. The API itself runs with a least-privilege
//                                             role and does not change the schema unless Database:MigrateOnStartup is on.
//   purge-test-data [--apply] [--allow-production]
//                                             remove automated-test accounts (*.test) and their data; dry run by default.
string[] commandWords = ["migrate", "purge-test-data", "--apply", "--allow-production"];
var migrateOnly = args.Contains("migrate", StringComparer.OrdinalIgnoreCase);
var purgeTestData = args.Contains("purge-test-data", StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(args.Where(a => !commandWords.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray());

builder.Services
    .AddControllers(o =>
    {
        o.OutputFormatters.RemoveType<Microsoft.AspNetCore.Mvc.Formatters.StringOutputFormatter>();
        // CSRF: every POST/PUT/PATCH/DELETE must carry the antiforgery header (see AntiforgeryTokens).
        o.Filters.Add<ValidateAntiforgeryOnUnsafeMethodsFilter>();
    })
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IAuthorizationHandler, ApprovedAccountHandler>();
builder.Services.AddAuthorization(Policies.Configure);
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = AntiforgeryTokens.HeaderName;
    o.Cookie.Name = "po.af";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Portfolio Optimizer API",
        Version = "v1",
        Description = "Equity research, classical portfolio optimization, machine-learning strategies, strategy comparison and " +
                      "simulated (paper) trading. No real orders are ever placed. Weights, rates and returns are fractions (0.05 = 5%).\n\n" +
                      "**Authentication:** cookie session. Call `GET /api/v1/auth/antiforgery`, then `POST /api/v1/auth/login`, then " +
                      "`GET /api/v1/auth/antiforgery` again; this Swagger UI copies the `XSRF-TOKEN` cookie into the `X-XSRF-TOKEN` " +
                      "header automatically. Only approved accounts can use data endpoints; `/admin` endpoints require the Administrator role.",
    });
    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml);
    var appXml = Path.Combine(AppContext.BaseDirectory, "PortfolioOptimizer.Application.xml");
    if (File.Exists(appXml)) o.IncludeXmlComments(appXml);
    o.SupportNonNullableReferenceTypes();
});

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("Location")));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
    static string UserOrIp(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? $"ip:{ClientIp(ctx)}";
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimiting:PermitPerMinute", 600),
            Window = TimeSpan.FromMinutes(1),
        }));
    o.AddPolicy(RateLimitPolicies.Expensive, ctx => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("RateLimiting:ExpensivePermitPerMinute", 30),
        Window = TimeSpan.FromMinutes(1),
    }));
    o.AddPolicy(RateLimitPolicies.Quotes, ctx => RateLimitPartition.GetFixedWindowLimiter(UserOrIp(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("RateLimiting:QuotePermitPerMinute", 60),
        Window = TimeSpan.FromMinutes(1),
    }));
    o.AddPolicy(RateLimitPolicies.Authentication, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = builder.Configuration.GetValue("RateLimiting:AuthenticationPermitPerMinute", 10),
        Window = TimeSpan.FromMinutes(1),
    }));
});

// Behind the bundled nginx (or another reverse proxy that terminates TLS), trust X-Forwarded-For/Proto so client IPs
// (rate limits, audit) and HTTPS detection (Secure cookies) are correct. Never enable when the API is directly reachable.
if (builder.Configuration.GetValue("ReverseProxy:TrustForwardedHeaders", false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services.AddRequestTimeouts(o => o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("RequestTimeoutSeconds", 120)),
});

var app = builder.Build();

if (purgeTestData)
{
    using var purgeScope = app.Services.CreateScope();
    var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
    PortfolioOptimizer.Infrastructure.Identity.TestDataPurgeReport report;
    try
    {
        report = await purgeScope.ServiceProvider.GetRequiredService<PortfolioOptimizer.Infrastructure.Identity.TestDataPurger>()
            .PurgeAsync(apply, args.Contains("--allow-production", StringComparer.OrdinalIgnoreCase), CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    Console.WriteLine($"Environment: {report.Environment} · database: {report.Database} · {(report.Applied ? "APPLIED" : "dry run (add --apply to delete)")}");
    Console.WriteLine($"Test accounts ({report.Accounts.Count}): {(report.Accounts.Count == 0 ? "none" : string.Join(", ", report.Accounts))}");
    Console.WriteLine($"Owned: {report.Portfolios} portfolio(s), {report.OptimizationRuns} optimization run(s), {report.Backtests} backtest(s), " +
                      $"{report.MlExperiments} ML experiment(s), {report.StrategyComparisons} comparison(s), {report.WatchlistEntries} watchlist entr(ies); audit events: {report.AuditEvents}");
    return 0;
}

if (migrateOnly)
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(CancellationToken.None);
    return 0;
}

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
}

if (app.Configuration.GetValue("ReverseProxy:TrustForwardedHeaders", false)) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RequestLoggingMiddleware>();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    await next();
});

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "Portfolio Optimizer API v1");
        o.DocumentTitle = "Portfolio Optimizer API";
        o.DisplayRequestDuration();
        // Send the antiforgery token (readable XSRF-TOKEN cookie) with every request made from the Swagger UI.
        o.UseRequestInterceptor("(req) => { const m = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]+)/); " +
                                "if (m) req.headers['X-XSRF-TOKEN'] = decodeURIComponent(m[1]); return req; }");
    });
}

app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseRequestTimeouts();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
// Lets end-to-end test runners verify they target a disposable test stack (ASPNETCORE_ENVIRONMENT=E2E) before they
// create accounts and data. Reveals nothing beyond that flag.
app.MapGet("/api/v1/system/test-environment", (IHostEnvironment env) => Results.Ok(new { isTestEnvironment = env.IsEnvironment("E2E") }))
    .AllowAnonymous().ExcludeFromDescription();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription().AllowAnonymous();
app.MapControllers();

app.Run();
return 0;

/// <summary>Entry point marker for integration tests.</summary>
public partial class Program;
