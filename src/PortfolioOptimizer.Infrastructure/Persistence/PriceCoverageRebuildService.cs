using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PortfolioOptimizer.Infrastructure.Persistence;

/// <summary>
/// Rebuilds the stored price coverage once after the API has started (AMIR-43), instead of before it accepts requests: the
/// rebuild took 1.6–5.2 s of a 4.0–8.4 s start on 5.3M prices. Until it commits, readers see the coverage as last stored
/// (one transaction, so never an empty table); it only corrects prices changed outside the application (SQL, restores,
/// imports). Concurrent price downloads are serialized with it by the advisory lock in
/// <see cref="AppDbContext.RebuildPriceCoverageAsync"/>. A failure is logged and never stops the API.
/// </summary>
public sealed class PriceCoverageRebuildService(IServiceScopeFactory scopes, ILogger<PriceCoverageRebuildService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().RebuildPriceCoverageAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: the next start rebuilds.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rebuilding the price coverage failed; the stored coverage stays as it was until the next start.");
        }
    }
}
