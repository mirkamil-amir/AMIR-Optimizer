using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.Application.Administration;

/// <summary>Read access to the audit trail (administrators only).</summary>
public sealed class AuditQueryService(IApplicationDbContext db)
{
    public async Task<PagedResult<AuditEventDto>> ListAsync(string? action, Guid? actorUserId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 500) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 500).");
        var q = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(e => e.Action == action);
        if (actorUserId is { } actor) q = q.Where(e => e.ActorUserId == actor);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new AuditEventDto(e.Id, e.TimestampUtc, e.ActorUserId, e.ActorEmail, e.Action, e.TargetType, e.TargetId, e.Success, e.Details, e.IpAddress))
            .ToListAsync(ct);
        return new PagedResult<AuditEventDto>(items, page, pageSize, total);
    }
}
