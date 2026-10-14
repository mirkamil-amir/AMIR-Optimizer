using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>
/// Writes audit events through a dedicated, short-lived DbContext so that recording an event never commits (or is rolled
/// back with) unrelated pending changes of the request's unit of work. Failures are logged, never thrown.
/// </summary>
public sealed class AuditLog(
    DbContextOptions<AppDbContext> options,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<AuditLog> logger) : IAuditLog
{
    public Task RecordAsync(string action, string? targetType = null, string? targetId = null, string? details = null, bool success = true,
        CancellationToken ct = default) =>
        RecordForAsync(currentUser.UserId, currentUser.Email, action, targetType, targetId, details, success, ct);

    public async Task RecordForAsync(Guid? actorId, string? actorEmail, string action, string? targetType = null, string? targetId = null,
        string? details = null, bool success = true, CancellationToken ct = default)
    {
        try
        {
            await using var db = new AppDbContext(options);
            db.AuditEvents.Add(new AuditEvent
            {
                TimestampUtc = clock.GetUtcNow().UtcDateTime,
                ActorUserId = actorId,
                ActorEmail = Truncate(actorEmail, 256),
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Success = success,
                Details = Truncate(details, 2000),
                IpAddress = currentUser.IpAddress,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Audit {Action} by {ActorId} on {TargetType} {TargetId}: {Success}", action, actorId, targetType, targetId, success);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record audit event {Action}", action);
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
