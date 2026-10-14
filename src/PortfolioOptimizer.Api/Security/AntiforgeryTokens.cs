using Microsoft.AspNetCore.Antiforgery;
using PortfolioOptimizer.Application.Dtos;

namespace PortfolioOptimizer.Api.Security;

/// <summary>
/// Double-submit pattern for the cookie-authenticated SPA: the antiforgery cookie is HttpOnly; the request token is
/// returned in the body and in a script-readable <c>XSRF-TOKEN</c> cookie (SameSite=Strict) that the dashboard and the
/// Swagger UI copy into the <c>X-XSRF-TOKEN</c> header. MVC validates it on every unsafe request.
/// </summary>
public static class AntiforgeryTokens
{
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string ReadableCookieName = "XSRF-TOKEN";

    public static AntiforgeryTokenDto Issue(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(ReadableCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
        });
        return new AntiforgeryTokenDto(HeaderName, tokens.RequestToken!);
    }
}

public static class RateLimitPolicies
{
    /// <summary>Sign-in, registration and password changes: per client IP.</summary>
    public const string Authentication = "auth";

    /// <summary>Optimizations, syncs, backtests, ML training and comparisons: per user.</summary>
    public const string Expensive = "expensive";

    /// <summary>On-demand quote refreshes (page and single stock): per user.</summary>
    public const string Quotes = "quotes";
}

/// <summary>
/// Validates the antiforgery token on every unsafe request (POST/PUT/PATCH/DELETE). Equivalent to MVC's
/// AutoValidateAntiforgeryToken, which requires the view services this API does not register.
/// </summary>
public sealed class ValidateAntiforgeryOnUnsafeMethodsFilter(IAntiforgery antiforgery) : Microsoft.AspNetCore.Mvc.Filters.IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)) return;
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new Microsoft.AspNetCore.Mvc.ObjectResult(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Antiforgery token missing or invalid",
                Detail = $"Send the token from GET /api/v1/auth/antiforgery in the {AntiforgeryTokens.HeaderName} header (fetch a new one after signing in or out).",
            }) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/problem+json" } };
        }
    }
}
