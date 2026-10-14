using Microsoft.AspNetCore.Identity;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>ASP.NET Core Identity user with an approval lifecycle.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";
    public UserAccountStatus Status { get; set; } = UserAccountStatus.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }
    public Guid? StatusChangedBy { get; set; }
    public string? StatusReason { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}

/// <summary>Administrator bootstrap configuration (<c>Admin__Email</c>, <c>Admin__Password</c>).</summary>
public sealed class AdminBootstrapOptions
{
    public const string Section = "Admin";

    /// <summary>E-mail of the first administrator. Used only while no administrator exists.</summary>
    public string? Email { get; set; }

    /// <summary>Initial password (secret – supply via environment variable or user-secrets).</summary>
    public string? Password { get; set; }

    public string DisplayName { get; set; } = "Administrator";
}
