using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.Pilot;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Jobs;

public sealed class ChannelPilotCycleQueue : IPilotCycleQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    public ValueTask EnqueueAsync(Guid cycleId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(cycleId, cancellationToken);
    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Runs Matrix Pilot cycles one at a time in their own scope (acting as the pilot's owner) and, on a timer, creates the
/// cycle of every active pilot whose decision is due – one per pilot and period (unique key), so a repeated tick or a second
/// instance cannot decide twice. On start-up, cycles a previous process left unfinished are recorded as failed (nothing
/// was booked by them unless their booking committed, which the ledger shows) and their leases are released.
/// </summary>
public sealed class MatrixPilotWorker(
    IPilotCycleQueue queue,
    IServiceScopeFactory scopes,
    IOptions<PilotOptions> options,
    TimeProvider clock,
    ILogger<MatrixPilotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        var consumer = ConsumeAsync(stoppingToken);
        if (options.Value.SchedulerEnabled)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.SchedulerIntervalMinutes)), clock);
                do await ScheduleDueAsync(stoppingToken);
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) { }
        }
        else logger.LogInformation("Matrix Pilot scheduler is disabled (cycles start only on the owner's request).");
        await consumer;
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cycleId in queue.DequeueAllAsync(ct))
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<FxService>().UseBackgroundWait();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<PilotEngine>().ExecuteAsync(cycleId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
                {
                    logger.LogError(ex, "Matrix Pilot cycle {CycleId} crashed", cycleId);
                    await MarkFailedAsync(cycleId, "The cycle stopped unexpectedly; nothing further was traded. See the server log.", ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Creates and enqueues the cycle of every active pilot whose next decision time has come.</summary>
    public async Task ScheduleDueAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            var due = await db.MatrixPilots.AsNoTracking()
                .Where(p => p.Status == PilotStatus.Active && p.ActiveCycleId == null && p.NextDecisionAtUtc != null && p.NextDecisionAtUtc <= now)
                .ToListAsync(ct);
            foreach (var pilot in due)
            {
                var cycle = await PilotCycles.TryCreateAsync(db, pilot, "Scheduled", now, ct);
                if (cycle is not null) await queue.EnqueueAsync(cycle.Id, ct);
                else
                {
                    // This period already has its decision (e.g. a manual run): move the schedule on.
                    var next = PilotSchedule.NextDecision(pilot.Frequency, now, options.Value.DecisionHourUtc);
                    await db.MatrixPilots.Where(p => p.Id == pilot.Id && p.Status == PilotStatus.Active)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.NextDecisionAtUtc, next), ct);
                }
            }
            await ReleaseStaleLeasesAsync(db, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ExceptionFilters.IsRecoverable(ex))
        {
            logger.LogWarning(ex, "Matrix Pilot scheduling failed; it is retried at the next tick.");
        }
    }

    private async Task ReleaseStaleLeasesAsync(IApplicationDbContext db, DateTime now, CancellationToken ct)
    {
        var limit = now.AddMinutes(-Math.Max(options.Value.CycleTimeoutMinutes * 2, options.Value.StaleLeaseMinutes));
        var stale = await db.MatrixPilots.AsNoTracking().Where(p => p.ActiveCycleId != null && p.ActiveCycleStartedAtUtc < limit)
            .Select(p => p.ActiveCycleId!.Value).ToListAsync(ct);
        foreach (var id in stale) await MarkFailedAsync(id, "The cycle was abandoned (no progress for too long); nothing further was traded.", ct);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var open = await db.MatrixPilotCycles.AsNoTracking().Where(c => c.Outcome == PilotCycleOutcome.Pending).Select(c => c.Id).ToListAsync(ct);
            foreach (var id in open) await MarkFailedAsync(id, "Interrupted by an application restart; nothing further was traded.", ct);
            if (open.Count > 0) logger.LogWarning("Matrix Pilot: {Count} unfinished cycle(s) from a previous run recorded as failed.", open.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ExceptionFilters.IsRecoverable(ex))
        {
            logger.LogError(ex, "Matrix Pilot recovery failed");
        }
    }

    private async Task MarkFailedAsync(Guid cycleId, string reason, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            var cycle = await db.MatrixPilotCycles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cycleId, ct);
            if (cycle is null) return;
            await db.MatrixPilotCycles.Where(c => c.Id == cycleId && c.Outcome == PilotCycleOutcome.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Outcome, PilotCycleOutcome.Failed).SetProperty(c => c.Phase, PilotCyclePhase.Complete)
                    .SetProperty(c => c.OutcomeReason, reason).SetProperty(c => c.CompletedAtUtc, now), ct);
            await db.MatrixPilots.Where(p => p.Id == cycle.PilotId && p.ActiveCycleId == cycleId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ActiveCycleId, (Guid?)null).SetProperty(p => p.ActiveCycleStartedAtUtc, (DateTime?)null)
                    .SetProperty(p => p.ConsecutiveFailures, p => p.ConsecutiveFailures + 1).SetProperty(p => p.Version, p => p.Version + 1), ct);
            db.MatrixPilotEvents.Add(new MatrixPilotEvent
            {
                PilotId = cycle.PilotId, AtUtc = now, Actor = PilotActor.System, Action = "cycle.failed", Reason = reason, CycleId = cycleId, CorrelationId = cycle.CorrelationId,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ExceptionFilters.IsRecoverable(ex))
        {
            logger.LogError(ex, "Could not record Matrix Pilot cycle {CycleId} as failed", cycleId);
        }
    }
}
