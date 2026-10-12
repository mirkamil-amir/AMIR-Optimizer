using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;
using PortfolioOptimizer.Api.Infrastructure;
using PortfolioOptimizer.Application;
using PortfolioOptimizer.Infrastructure;
using PortfolioOptimizer.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(o => o.OutputFormatters.RemoveType<Microsoft.AspNetCore.Mvc.Formatters.StringOutputFormatter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
        Description = "Equity portfolio optimization, simulated (paper) trading and backtesting. " +
                      "Local single-user application without authentication. No real orders are ever placed. " +
                      "Weights, rates and returns are fractions (0.05 = 5%).",
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
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimiting:PermitPerMinute", 600),
            Window = TimeSpan.FromMinutes(1),
        }));
    o.AddFixedWindowLimiter("expensive", l =>
    {
        l.PermitLimit = builder.Configuration.GetValue("RateLimiting:ExpensivePermitPerMinute", 30);
        l.Window = TimeSpan.FromMinutes(1);
    });
});

builder.Services.AddRequestTimeouts(o => o.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("RequestTimeoutSeconds", 120)),
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "Portfolio Optimizer API v1");
        o.DocumentTitle = "Portfolio Optimizer API";
        o.DisplayRequestDuration();
    });
}

app.UseCors();
app.UseRateLimiter();
app.UseRequestTimeouts();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapControllers();

app.Run();

/// <summary>Entry point marker for integration tests.</summary>
public partial class Program;
