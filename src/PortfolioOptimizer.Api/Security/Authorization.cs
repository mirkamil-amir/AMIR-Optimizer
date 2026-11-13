using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Api.Security;

public static class Policies
{
    /// <summary>Signed in, regardless of approval status (own profile, approval status, sign-out).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Signed in and approved. The fallback policy for every endpoint without explicit metadata.</summary>
    public const string ApprovedUser = "ApprovedUser";

    /// <summary>Approved administrator.</summary>
    public const string Administrator = "Administrator";

    public static void Configure(AuthorizationOptions o)
    {
        o.AddPolicy(Authenticated, p => p.RequireAuthenticatedUser());
        o.AddPolicy(ApprovedUser, p => p.RequireAuthenticatedUser().AddRequirements(new ApprovedAccountRequirement()));
        o.AddPolicy(Administrator, p => p.RequireAuthenticatedUser().AddRequirements(new ApprovedAccountRequirement()).RequireRole(Roles.Administrator));
        o.FallbackPolicy = o.GetPolicy(ApprovedUser);
    }
}

/// <summary>
/// The account must currently be Approved and must not be waiting for a new password after an administrator's reset (read
/// from the database on each request, so suspensions and resets apply at once).
/// </summary>
public sealed class ApprovedAccountRequirement : IAuthorizationRequirement;

public sealed class ApprovedAccountHandler(IAccountService accounts) : AuthorizationHandler<ApprovedAccountRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ApprovedAccountRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return;
        var access = await accounts.GetAccessAsync(id, (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None);
        if (access is { Status: UserAccountStatus.Approved, CredentialResetPending: false }) context.Succeed(requirement);
        else if (access?.CredentialResetPending == true) context.Fail(new AuthorizationFailureReason(this, "A new password must be chosen first."));
        else context.Fail(new AuthorizationFailureReason(this, $"Account status is {access?.Status.ToString() ?? "unknown"}."));
    }
}

/// <summary><see cref="ICurrentUser"/> backed by the request's claims principal.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId =>
        Principal?.Identity?.IsAuthenticated == true && Guid.TryParse(Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email) ?? Principal?.FindFirstValue(ClaimTypes.Name);

    public bool IsAdministrator => Principal?.IsInRole(Roles.Administrator) == true;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public Guid RequireUserId() => UserId ?? throw new ForbiddenException("An authenticated user is required.");
}

/// <summary>
/// The request's user, or – only where there is no request user – the user a background job acts for
/// (<see cref="PortfolioOptimizer.Application.Common.BackgroundActor"/>). A request identity always wins.
/// </summary>
public sealed class ActorAwareCurrentUser(HttpCurrentUser http, PortfolioOptimizer.Application.Common.BackgroundActor actor) : ICurrentUser
{
    public Guid? UserId => http.UserId ?? actor.UserId;
    public string? Email => http.UserId is not null ? http.Email : actor.Label;
    public bool IsAdministrator => http.UserId is not null && http.IsAdministrator;
    public string? IpAddress => http.IpAddress;
    public Guid RequireUserId() => UserId ?? throw new ForbiddenException("An authenticated user is required.");
}
