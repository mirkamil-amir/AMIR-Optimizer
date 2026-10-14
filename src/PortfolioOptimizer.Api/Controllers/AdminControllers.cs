using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortfolioOptimizer.Api.Security;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Administration;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>User management: approve, reject, suspend and reactivate accounts; assign roles. Administrators only.</summary>
[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Policy = Policies.Administrator)]
public sealed class AdminUsersController(IUserAdministrationService users) : ControllerBase
{
    /// <summary>Lists accounts (pending applications first).</summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="search">Case-insensitive match on e-mail or display name.</param>
    /// <param name="includeTestAccounts">Also list automated-test accounts (reserved .test domain); hidden by default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Rows per page (max 200).</param>
    [HttpGet]
    public Task<PagedResult<UserSummaryDto>> List([FromQuery] UserAccountStatus? status, [FromQuery] string? search, CancellationToken ct,
        [FromQuery] bool includeTestAccounts = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        users.ListAsync(status, search, includeTestAccounts, page, pageSize, ct);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public Task<UserSummaryDto> Get(Guid id, CancellationToken ct) => users.GetAsync(id, ct);

    /// <summary>Approves, rejects, suspends or reactivates (Approved) an account.</summary>
    /// <response code="403">Attempt to change the caller's own status.</response>
    /// <response code="409">Would leave no approved administrator.</response>
    [HttpPut("{id:guid}/status")]
    [ProducesResponseType<UserSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<UserSummaryDto> SetStatus(Guid id, UpdateUserStatusRequest request, CancellationToken ct) =>
        users.SetStatusAsync(id, request.Status, request.Reason, ct);

    /// <summary>Replaces the roles of an account (User is always kept). Users can never assign roles to themselves.</summary>
    [HttpPut("{id:guid}/roles")]
    [ProducesResponseType<UserSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<UserSummaryDto> SetRoles(Guid id, UpdateUserRolesRequest request, CancellationToken ct) => users.SetRolesAsync(id, request.Roles, ct);
}

/// <summary>Administrator dashboard: accounts, analysis activity across users and market-data health. Administrators only.</summary>
[ApiController]
[Route("api/v1/admin/overview")]
[Authorize(Policy = Policies.Administrator)]
public sealed class AdminOverviewController(IAdminOverviewService overview) : ControllerBase
{
    [HttpGet]
    public Task<AdminOverviewDto> Get(CancellationToken ct) => overview.GetAsync(ct);
}

/// <summary>Audit trail (sign-ins, approvals, role and settings changes, simulated executions). Administrators only.</summary>
[ApiController]
[Route("api/v1/admin/audit-events")]
[Authorize(Policy = Policies.Administrator)]
public sealed class AuditEventsController(AuditQueryService audit) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditEventDto>> List([FromQuery] string? action, [FromQuery] Guid? actorUserId, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100) => audit.ListAsync(action, actorUserId, page, pageSize, ct);
}
