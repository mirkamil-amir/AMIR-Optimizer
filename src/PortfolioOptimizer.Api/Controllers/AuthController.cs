using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Registration, sign-in and the caller's own account. Uses an HttpOnly session cookie.</summary>
/// <remarks>
/// State-changing requests (POST/PUT/PATCH/DELETE) must send the antiforgery token from
/// <c>GET /api/v1/auth/antiforgery</c> in the <c>X-XSRF-TOKEN</c> header. Fetch a new token after signing in or out.
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IAccountService accounts, IAntiforgery antiforgery) : ControllerBase
{
    /// <summary>Issues an antiforgery token (also set as the readable <c>XSRF-TOKEN</c> cookie).</summary>
    [HttpGet("antiforgery")]
    [AllowAnonymous]
    public AntiforgeryTokenDto Antiforgery() => AntiforgeryTokens.Issue(HttpContext, antiforgery);

    /// <summary>Applies for an account. New accounts are pending until an administrator approves them.</summary>
    /// <remarks>The response is identical whether or not the address is already registered.</remarks>
    /// <response code="202">Application received.</response>
    /// <response code="400">Invalid input or password policy not met.</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [ProducesResponseType<RegistrationResultDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RegistrationResultDto>> Register(RegisterRequest request, CancellationToken ct) =>
        Accepted(await accounts.RegisterAsync(request, ct));

    /// <summary>Signs in. Pending, rejected or suspended accounts can sign in but cannot access application data.</summary>
    /// <response code="200">Signed in; the profile includes the approval status.</response>
    /// <response code="401">Invalid credentials or temporarily locked out.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public Task<CurrentUserDto> Login(LoginRequest request, CancellationToken ct) => accounts.LoginAsync(request, ct);

    /// <summary>Signs out and clears the session cookie.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await accounts.LogoutAsync(ct);
        return NoContent();
    }

    /// <summary>The signed-in user and approval status.</summary>
    /// <response code="401">Not signed in.</response>
    [HttpGet("me")]
    [Authorize(Policy = Policies.Authenticated)]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken ct) =>
        await accounts.GetCurrentAsync(ct) is { } user ? user : Unauthorized();

    /// <summary>Changes the caller's password (other sessions are signed out within a minute).</summary>
    [HttpPost("change-password")]
    [Authorize(Policy = Policies.Authenticated)]
    [EnableRateLimiting(RateLimitPolicies.Authentication)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await accounts.ChangePasswordAsync(request, ct);
        return NoContent();
    }
}
