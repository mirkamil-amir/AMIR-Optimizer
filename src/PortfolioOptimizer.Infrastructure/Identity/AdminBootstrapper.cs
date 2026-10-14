using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Infrastructure.Identity;

/// <summary>
/// Ensures the roles exist and, while no administrator exists, creates (or promotes) the configured administrator.
/// Never changes an existing administrator or password: once an administrator exists, the configuration is ignored.
/// </summary>
public sealed class AdminBootstrapper(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IOptions<AdminBootstrapOptions> options,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<AdminBootstrapper> logger)
{
    /// <returns>The id of the bootstrapped administrator, or null when nothing was created.</returns>
    public async Task<Guid?> EnsureAsync(CancellationToken ct)
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                Check(await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() }), $"create role {role}");
        }

        if ((await users.GetUsersInRoleAsync(Roles.Administrator)).Count > 0) return null;

        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrWhiteSpace(o.Password))
        {
            logger.LogWarning("No administrator exists. Set Admin__Email and Admin__Password and restart to create one; " +
                              "until then new registrations cannot be approved.");
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = await users.FindByEmailAsync(o.Email.Trim());
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = o.Email.Trim(),
                Email = o.Email.Trim(),
                EmailConfirmed = true,
                DisplayName = o.DisplayName,
                Status = UserAccountStatus.Approved,
                CreatedAtUtc = now,
                StatusChangedAtUtc = now,
                StatusReason = "Bootstrap administrator",
            };
            Check(await users.CreateAsync(user, o.Password), "create the bootstrap administrator (check the password policy)");
        }
        else
        {
            user.Status = UserAccountStatus.Approved;
            user.StatusChangedAtUtc = now;
            user.StatusReason = "Promoted by administrator bootstrap";
            Check(await users.UpdateAsync(user), "approve the bootstrap administrator");
        }

        foreach (var role in Roles.All)
        {
            if (!await users.IsInRoleAsync(user, role)) Check(await users.AddToRoleAsync(user, role), $"assign {role}");
        }
        await audit.RecordForAsync(user.Id, user.Email, AuditActions.AdminBootstrapped, "User", user.Id.ToString(), ct: ct);
        logger.LogInformation("Bootstrap administrator {Email} is ready", user.Email);
        return user.Id;
    }

    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Could not {what}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
    }
}
