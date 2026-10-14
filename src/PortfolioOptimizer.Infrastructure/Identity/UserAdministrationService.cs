using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>
/// Administrator-only account management. Guards against lock-out: administrators cannot change their own status or
/// remove their own administrator role, and the last approved administrator can never be demoted or suspended.
/// </summary>
public sealed class UserAdministrationService(
    UserManager<ApplicationUser> users,
    AppDbContext db,
    ICurrentUser currentUser,
    IAuditLog audit,
    TimeProvider clock) : IUserAdministrationService
{
    public async Task<PagedResult<UserSummaryDto>> ListAsync(UserAccountStatus? status, string? search, bool includeTestAccounts, int page, int pageSize,
        CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 200) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 200).");
        var q = db.Users.AsNoTracking();
        // Automated-test accounts (reserved .test domain, see TestDataPolicy) are hidden unless explicitly requested.
        if (!includeTestAccounts) q = q.Where(u => u.Email == null || !u.Email.ToLower().EndsWith(TestDataPolicy.ReservedTestDomain));
        if (status is { } s) q = q.Where(u => u.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            q = q.Where(u => EF.Functions.ILike(u.Email!, pattern, "\\") || EF.Functions.ILike(u.DisplayName, pattern, "\\"));
        }
        var total = await q.CountAsync(ct);
        // Pending applications first, then newest.
        var page_ = await q.OrderBy(u => u.Status == UserAccountStatus.Pending ? 0 : 1).ThenByDescending(u => u.CreatedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var roles = await RolesByUserAsync(page_.Select(u => u.Id).ToList(), ct);
        return new PagedResult<UserSummaryDto>(page_.Select(u => ToDto(u, roles.GetValueOrDefault(u.Id, []))).ToList(), page, pageSize, total);
    }

    public async Task<UserSummaryDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await FindAsync(userId);
        return ToDto(user, (await users.GetRolesAsync(user)).ToList());
    }

    public async Task<UserSummaryDto> SetStatusAsync(Guid userId, UserAccountStatus status, string? reason, CancellationToken ct)
    {
        var actor = currentUser.RequireUserId();
        if (actor == userId) throw new ForbiddenException("Administrators cannot change the status of their own account.");
        if (status == UserAccountStatus.Pending) throw new ValidationException("status", "An account cannot be moved back to Pending.");

        var user = await FindAsync(userId);
        var roles = (await users.GetRolesAsync(user)).ToList();
        if (status != UserAccountStatus.Approved && roles.Contains(Roles.Administrator) && await ApprovedAdministratorCountAsync(ct) <= 1)
            throw new ConflictException("The last approved administrator cannot be suspended or rejected.");

        var previous = user.Status;
        if (previous == status) return ToDto(user, roles);
        user.Status = status;
        user.StatusChangedAtUtc = clock.GetUtcNow().UtcDateTime;
        user.StatusChangedBy = actor;
        user.StatusReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw AccountService.IdentityValidation(result, "status");
        // Invalidate existing sessions of accounts that lose access (status is also checked on every request).
        if (status != UserAccountStatus.Approved) await users.UpdateSecurityStampAsync(user);

        var action = status switch
        {
            UserAccountStatus.Approved when previous == UserAccountStatus.Suspended => AuditActions.UserReactivated,
            UserAccountStatus.Approved => AuditActions.UserApproved,
            UserAccountStatus.Rejected => AuditActions.UserRejected,
            _ => AuditActions.UserSuspended,
        };
        await audit.RecordAsync(action, "User", user.Id.ToString(), $"{previous} → {status}" + (user.StatusReason is null ? "" : $": {user.StatusReason}"), ct: ct);
        return ToDto(user, roles);
    }

    public async Task<UserSummaryDto> SetRolesAsync(Guid userId, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var actor = currentUser.RequireUserId();
        var requested = roles.Select(r => Roles.All.FirstOrDefault(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase)) ?? r)
            .Append(Roles.User) // every account keeps the base role
            .Distinct().ToList();
        var invalid = requested.Except(Roles.All).ToList();
        if (invalid.Count > 0) throw new ValidationException("roles", $"Unknown role(s): {string.Join(", ", invalid)}. Allowed: {string.Join(", ", Roles.All)}.");

        var user = await FindAsync(userId);
        var current = (await users.GetRolesAsync(user)).ToList();
        var grantsAdmin = requested.Contains(Roles.Administrator) && !current.Contains(Roles.Administrator);
        var revokesAdmin = !requested.Contains(Roles.Administrator) && current.Contains(Roles.Administrator);
        if (revokesAdmin && actor == userId) throw new ForbiddenException("Administrators cannot remove their own administrator role.");
        if (revokesAdmin && await ApprovedAdministratorCountAsync(ct) <= 1) throw new ConflictException("The last administrator cannot be demoted.");
        if (grantsAdmin && user.Status != UserAccountStatus.Approved) throw new ConflictException("Only approved accounts can become administrators.");

        var remove = current.Except(requested).ToList();
        var add = requested.Except(current).ToList();
        if (remove.Count > 0) EnsureSucceeded(await users.RemoveFromRolesAsync(user, remove));
        if (add.Count > 0) EnsureSucceeded(await users.AddToRolesAsync(user, add));
        if (remove.Count > 0 || add.Count > 0)
        {
            await users.UpdateSecurityStampAsync(user); // role claims in existing cookies are refreshed
            await audit.RecordAsync(AuditActions.RolesChanged, "User", user.Id.ToString(),
                $"[{string.Join(", ", current.OrderBy(r => r))}] → [{string.Join(", ", requested.OrderBy(r => r))}]", ct: ct);
        }
        return ToDto(user, requested);
    }

    private async Task<int> ApprovedAdministratorCountAsync(CancellationToken ct) =>
        (await users.GetUsersInRoleAsync(Roles.Administrator)).Count(u => u.Status == UserAccountStatus.Approved);

    private async Task<ApplicationUser> FindAsync(Guid id) =>
        await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);

    private async Task<Dictionary<Guid, List<string>>> RolesByUserAsync(List<Guid> ids, CancellationToken ct)
    {
        var rows = await (from ur in db.UserRoles.AsNoTracking()
                          join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                          where ids.Contains(ur.UserId)
                          select new { ur.UserId, r.Name }).ToListAsync(ct);
        return rows.GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name!).OrderBy(n => n).ToList());
    }

    private UserSummaryDto ToDto(ApplicationUser u, IReadOnlyList<string> roles) => new(
        u.Id, u.Email ?? "", u.DisplayName, u.Status, roles.OrderBy(r => r).ToList(), u.CreatedAtUtc, u.StatusChangedAtUtc, u.StatusReason,
        u.LastLoginAtUtc, u.LockoutEnd is { } end && end > clock.GetUtcNow(), TestDataPolicy.IsTestEmail(u.Email));

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded) throw AccountService.IdentityValidation(result, "roles");
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
