namespace PortfolioOptimizer.Domain;

/// <summary>
/// Lifecycle of a user account. Only <see cref="Approved"/> accounts can use the application; new registrations start
/// as <see cref="Pending"/> until an administrator decides.
/// </summary>
public enum UserAccountStatus { Pending, Approved, Rejected, Suspended }

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Administrator, User];
}

/// <summary>Append-only security/audit record (sign-ins, approvals, role and settings changes, simulated executions).</summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public bool Success { get; set; } = true;
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Well-known audit action names.</summary>
public static class AuditActions
{
    public const string Register = "auth.register";
    public const string Login = "auth.login";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";
    public const string PasswordChanged = "auth.password_changed";
    public const string UserApproved = "admin.user_approved";
    public const string UserRejected = "admin.user_rejected";
    public const string UserSuspended = "admin.user_suspended";
    public const string UserReactivated = "admin.user_reactivated";
    public const string RolesChanged = "admin.roles_changed";
    public const string AdminBootstrapped = "admin.bootstrapped";
    public const string SettingsChanged = "admin.settings_changed";
    public const string AssetCreated = "admin.asset_created";
    public const string AssetUpdated = "admin.asset_updated";
    public const string CatalogueImported = "admin.catalogue_imported";
    public const string PaperTradesExecuted = "trading.paper_trades_executed";
    public const string PortfolioDeleted = "portfolio.deleted";
    public const string TestDataPurged = "admin.test_data_purged";
    public const string ResearchRunSaved = "research.run_saved";
}

/// <summary>
/// Identifies automated-test accounts. Test suites (Playwright, integration tests) register users under the reserved
/// <c>.test</c> top-level domain (RFC 6761), which can never be a real mailbox. Such accounts are hidden from the
/// administrator's user list by default, may be refused in Production, and are removed by <c>purge-test-data</c>.
/// </summary>
public static class TestDataPolicy
{
    public const string ReservedTestDomain = ".test";

    public static bool IsTestEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.LastIndexOf('@');
        if (at < 0) return false;
        var domain = email[(at + 1)..].Trim().TrimEnd('.');
        return domain.EndsWith(ReservedTestDomain, StringComparison.OrdinalIgnoreCase) || domain.Equals("test", StringComparison.OrdinalIgnoreCase);
    }
}
