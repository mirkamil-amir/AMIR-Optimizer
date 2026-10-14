using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Abstractions;

/// <summary>The authenticated caller of the current request. Never available inside background jobs.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAdministrator { get; }
    string? IpAddress { get; }

    /// <summary>The caller's id; throws <see cref="Common.ForbiddenException"/> when there is no authenticated user.</summary>
    Guid RequireUserId();
}

/// <summary>Registration, sign-in and the caller's own account (implemented with ASP.NET Core Identity).</summary>
public interface IAccountService
{
    Task<RegistrationResultDto> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<CurrentUserDto> LoginAsync(LoginRequest request, CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
    Task<CurrentUserDto?> GetCurrentAsync(CancellationToken ct);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct);

    /// <summary>Current status of an account (checked on every authorized request so suspensions apply immediately).</summary>
    Task<UserAccountStatus?> GetStatusAsync(Guid userId, CancellationToken ct);
}

/// <summary>Administrator-only user management.</summary>
public interface IUserAdministrationService
{
    Task<PagedResult<UserSummaryDto>> ListAsync(UserAccountStatus? status, string? search, bool includeTestAccounts, int page, int pageSize, CancellationToken ct);
    Task<UserSummaryDto> GetAsync(Guid userId, CancellationToken ct);
    Task<UserSummaryDto> SetStatusAsync(Guid userId, UserAccountStatus status, string? reason, CancellationToken ct);
    Task<UserSummaryDto> SetRolesAsync(Guid userId, IReadOnlyCollection<string> roles, CancellationToken ct);
}

/// <summary>Administrator dashboard: account counts, analysis activity across users and market-data health.</summary>
public interface IAdminOverviewService
{
    Task<AdminOverviewDto> GetAsync(CancellationToken ct);
}

/// <summary>Append-only audit trail. Actor and IP are taken from the current request when available.</summary>
public interface IAuditLog
{
    Task RecordAsync(string action, string? targetType = null, string? targetId = null, string? details = null, bool success = true,
        CancellationToken ct = default);

    /// <summary>Records an event for an explicit actor (e.g. failed sign-in before authentication).</summary>
    Task RecordForAsync(Guid? actorId, string? actorEmail, string action, string? targetType = null, string? targetId = null,
        string? details = null, bool success = true, CancellationToken ct = default);
}
