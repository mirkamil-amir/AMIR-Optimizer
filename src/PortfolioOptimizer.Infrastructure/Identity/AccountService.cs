using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Domain;
using PortfolioOptimizer.Infrastructure.Persistence;

namespace PortfolioOptimizer.Infrastructure.Identity;

public sealed class AccountService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    AppDbContext db,
    ICurrentUser currentUser,
    IAuditLog audit,
    QuoteRefreshService quoteRefresh,
    Microsoft.Extensions.Options.IOptions<QuoteRefreshOptions> quoteOptions,
    Microsoft.Extensions.Hosting.IHostEnvironment environment,
    TimeProvider clock,
    ILogger<AccountService> logger) : IAccountService
{
    private const string RegistrationMessage =
        "Thank you. If this e-mail address can be registered, the account has been created and is pending administrator approval. " +
        "You can sign in to check its status.";

    private const string InvalidCredentials = "Invalid e-mail address or password.";

    public async Task<RegistrationResultDto> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        // Automated tests register under the reserved .test domain; such accounts must never appear in a production database.
        if (TestDataPolicy.IsTestEmail(email) && Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsProduction(environment))
            throw new ValidationException("email", "Addresses under the reserved .test domain are for automated tests and cannot be registered here.");
        if (await users.FindByEmailAsync(email) is not null)
        {
            // Same response as a successful registration: do not reveal which addresses have accounts.
            await audit.RecordForAsync(null, email, AuditActions.Register, "User", null, "Address already registered.", success: false, ct);
            return new RegistrationResultDto(RegistrationMessage);
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Status = UserAccountStatus.Pending,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
                return new RegistrationResultDto(RegistrationMessage); // concurrent registration of the same address
            throw IdentityValidation(result, "password");
        }
        await users.AddToRoleAsync(user, Roles.User);
        await audit.RecordForAsync(user.Id, email, AuditActions.Register, "User", user.Id.ToString(), ct: ct);
        logger.LogInformation("New registration {UserId} pending approval", user.Id);
        return new RegistrationResultDto(RegistrationMessage);
    }

    public async Task<CurrentUserDto> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            await audit.RecordForAsync(null, email, AuditActions.LoginFailed, details: "Unknown account.", success: false, ct: ct);
            throw new AuthenticationFailedException(InvalidCredentials);
        }

        var check = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            await audit.RecordForAsync(user.Id, email, AuditActions.LoginFailed, "User", user.Id.ToString(), "Locked out.", success: false, ct);
            throw new AuthenticationFailedException("Too many failed sign-in attempts. Try again later.");
        }
        if (!check.Succeeded)
        {
            await audit.RecordForAsync(user.Id, email, AuditActions.LoginFailed, "User", user.Id.ToString(), "Wrong password.", success: false, ct);
            throw new AuthenticationFailedException(InvalidCredentials);
        }

        // Pending, rejected and suspended users may sign in to see their status; authorization blocks every data endpoint.
        await signIn.SignInAsync(user, isPersistent: false);
        user.LastLoginAtUtc = clock.GetUtcNow().UtcDateTime;
        await users.UpdateAsync(user);
        await audit.RecordForAsync(user.Id, email, AuditActions.Login, "User", user.Id.ToString(), $"Status {user.Status}.", ct: ct);
        if (user.Status == UserAccountStatus.Approved && quoteOptions.Value.RefreshOnSignIn)
        {
            // Every sign-in validates market data: a background cycle refreshes quotes (cached ones are reused) and queues an
            // incremental history download if tracked instruments are behind. A failure here must never block the sign-in.
            try { await quoteRefresh.RequestCycleAsync(QuoteRefreshService.TriggerLogin, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not start the market-data refresh after sign-in"); }
        }
        return await ToDtoAsync(user);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (currentUser.UserId is { } id) await audit.RecordAsync(AuditActions.Logout, "User", id.ToString(), ct: ct);
        await signIn.SignOutAsync();
    }

    public async Task<CurrentUserDto?> GetCurrentAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } id) return null;
        var user = await users.FindByIdAsync(id.ToString());
        return user is null ? null : await ToDtoAsync(user);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        var id = currentUser.RequireUserId();
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new ForbiddenException("Account not found.");
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
                throw new ValidationException("currentPassword", "The current password is incorrect.");
            throw IdentityValidation(result, "newPassword");
        }
        // The security stamp changed: other sessions are invalidated at their next validation; keep this one.
        await signIn.RefreshSignInAsync(user);
        await audit.RecordAsync(AuditActions.PasswordChanged, "User", id.ToString(), ct: ct);
    }

    public async Task<UserAccountStatus?> GetStatusAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (UserAccountStatus?)u.Status).FirstOrDefaultAsync(ct);

    private async Task<CurrentUserDto> ToDtoAsync(ApplicationUser user)
    {
        var roles = (await users.GetRolesAsync(user)).OrderBy(r => r).ToList();
        return new CurrentUserDto(user.Id, user.Email ?? "", user.DisplayName, user.Status, roles,
            roles.Contains(Roles.Administrator), user.CreatedAtUtc);
    }

    internal static ValidationException IdentityValidation(IdentityResult result, string field) =>
        new(new Dictionary<string, string[]> { [field] = result.Errors.Select(e => e.Description).ToArray() });
}
