using System.ComponentModel.DataAnnotations;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Dtos;

public sealed record RegisterRequest
{
    /// <example>analyst@example.com</example>
    [Required, EmailAddress, StringLength(256)] public string Email { get; init; } = "";
    [Required, StringLength(128, MinimumLength = 10)] public string Password { get; init; } = "";
    /// <example>Jane Analyst</example>
    [Required, StringLength(100, MinimumLength = 1)] public string DisplayName { get; init; } = "";
}

/// <summary>
/// Outcome of a registration. Deliberately identical whether or not the e-mail address was already registered, so the
/// endpoint cannot be used to discover accounts.
/// </summary>
public sealed record RegistrationResultDto(string Message);

public sealed record LoginRequest
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; init; } = "";
    [Required, StringLength(128)] public string Password { get; init; } = "";
}

public sealed record ChangePasswordRequest
{
    [Required, StringLength(128)] public string CurrentPassword { get; init; } = "";
    [Required, StringLength(128, MinimumLength = 10)] public string NewPassword { get; init; } = "";
}

/// <summary>The signed-in user. <see cref="Status"/> other than Approved means no access to application data.</summary>
public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    UserAccountStatus Status,
    IReadOnlyList<string> Roles,
    bool IsAdministrator,
    DateTime CreatedAtUtc);

public sealed record UserSummaryDto(
    Guid Id,
    string Email,
    string DisplayName,
    UserAccountStatus Status,
    IReadOnlyList<string> Roles,
    DateTime CreatedAtUtc,
    DateTime? StatusChangedAtUtc,
    string? StatusReason,
    DateTime? LastLoginAtUtc,
    bool IsLockedOut,
    /// <summary>Automated-test account (reserved .test domain); removed by the purge-test-data command.</summary>
    bool IsTestAccount);

public sealed record UpdateUserStatusRequest
{
    [Required] public UserAccountStatus Status { get; init; }
    /// <summary>Optional note shown to administrators (e.g. reason for rejection or suspension).</summary>
    [StringLength(500)] public string? Reason { get; init; }
}

public sealed record UpdateUserRolesRequest
{
    /// <summary>Roles to assign. Allowed values: Administrator, User.</summary>
    [Required] public List<string> Roles { get; init; } = [];
}

public sealed record AuditEventDto(
    long Id,
    DateTime TimestampUtc,
    Guid? ActorUserId,
    string? ActorEmail,
    string Action,
    string? TargetType,
    string? TargetId,
    bool Success,
    string? Details,
    string? IpAddress);

/// <summary>Antiforgery request token; send it back in the <c>X-XSRF-TOKEN</c> header on state-changing requests.</summary>
public sealed record AntiforgeryTokenDto(string HeaderName, string Token);

/// <summary>Administrator dashboard. Contains metadata and counts only: no passwords, keys, tokens or analysis results.</summary>
public sealed record AdminOverviewDto(
    AccountCountsDto Accounts,
    ActivityCountsDto Activity,
    IReadOnlyList<RecentRunDto> RecentRuns,
    MarketDataHealthDto MarketData,
    DateTime GeneratedAtUtc);

public sealed record AccountCountsDto(int Pending, int Approved, int Rejected, int Suspended, int Administrators, int TestAccounts);

/// <summary>Genuine analysis records (owners under the reserved test domain are excluded).</summary>
public sealed record ActivityCountsDto(
    int OptimizationRuns, int OptimizationRunsLast30Days, int MlExperiments, int MlExperimentsFailed, int MlExperimentsActive,
    int StrategyComparisons, int Backtests, int Portfolios);

/// <summary>A recent run of any user (what and when, not its results).</summary>
public sealed record RecentRunDto(string Kind, Guid Id, string Name, string Status, string OwnerName, string OwnerEmail, DateTime CreatedAtUtc);

public sealed record MarketDataHealthDto(
    string ActiveProvider,
    IReadOnlyList<ProviderHealthDto> Providers,
    IReadOnlyList<IndexHealthDto> Indices,
    IReadOnlyList<QuoteRefreshRunDto> RecentQuoteRefreshes,
    SyncJobSummaryDto? LastHistorySync);

/// <summary>Provider configuration status. Never contains the API key itself.</summary>
public sealed record ProviderHealthDto(string Name, string DisplayName, bool IsConfigured, bool RequiresApiKey, bool IsSimulated, string RateLimit,
    int? RemainingRequestsToday);

public sealed record IndexHealthDto(string Code, string Name, int Members, int WithQuote, int WithQuoteToday, int WithHistory, DateOnly? VerifiedOn);

public sealed record SyncJobSummaryDto(Guid Id, string Status, DateTime RequestedAtUtc, DateTime? CompletedAtUtc, int AssetsTotal, int AssetsFailed, string? Message);
