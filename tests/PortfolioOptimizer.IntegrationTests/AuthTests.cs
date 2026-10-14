using System.Net;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.IntegrationTests;

/// <summary>Authentication, approval workflow, role enforcement, per-user isolation, CSRF and audit logging.</summary>
public sealed class AuthTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = await ApiFactory.CreateAsync();
        _admin = await _factory.CreateAdminSessionAsync();
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
        await _factory.DeleteDatabaseAsync();
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized_ForDataAndAdminEndpoints()
    {
        using var anonymous = _factory.CreateSession();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/portfolios")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutJsonAsync("/api/v1/settings", new { })).StatusCode);
    }

    [Fact]
    public async Task Registration_StartsPending_AndPendingUserHasNoAccess()
    {
        using var client = _factory.CreateSession();
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        var reg = await (await client.PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest { Email = email, Password = ApiFactory.UserPassword, DisplayName = "Pending" }))
            .EnsureStatus(HttpStatusCode.Accepted).ReadAsync<RegistrationResultDto>();

        // The same response for an existing address: registration cannot be used to enumerate accounts.
        var dup = await (await client.PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest { Email = email, Password = ApiFactory.UserPassword, DisplayName = "Again" }))
            .EnsureStatus(HttpStatusCode.Accepted).ReadAsync<RegistrationResultDto>();
        Assert.Equal(reg.Message, dup.Message);

        var me = await client.LoginAsync(email, ApiFactory.UserPassword);
        Assert.Equal(UserAccountStatus.Pending, me.Status);
        Assert.Equal([Roles.User], me.Roles);
        Assert.False(me.IsAdministrator);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/portfolios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Registration_CannotChooseRoles_AndWeakPasswordsAreRejected()
    {
        using var client = _factory.CreateSession();
        var email = $"sneaky-{Guid.NewGuid():N}@test.local";
        // Extra properties such as roles/status are ignored by the API contract.
        (await client.PostJsonAsync("/api/v1/auth/register",
            new { email, password = ApiFactory.UserPassword, displayName = "Sneaky", roles = new[] { "Administrator" }, status = "Approved" }))
            .EnsureStatus(HttpStatusCode.Accepted);
        var me = await client.LoginAsync(email, ApiFactory.UserPassword);
        Assert.Equal(UserAccountStatus.Pending, me.Status);
        Assert.DoesNotContain(Roles.Administrator, me.Roles);

        var weak = await client.PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest { Email = $"weak-{Guid.NewGuid():N}@test.local", Password = "alllowercase12", DisplayName = "Weak" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Fails_AndRepeatedFailuresLockOut()
    {
        var (_, user) = await _factory.CreateApprovedUserAsync(_admin);
        using var client = _factory.CreateSession();
        for (var i = 0; i < 5; i++)
        {
            var r = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest { Email = user.Email, Password = "Wrong-Passw0rd" });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        // Locked out now, even with the right password.
        var locked = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest { Email = user.Email, Password = ApiFactory.UserPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("Too many", await locked.Content.ReadAsStringAsync());

        var unknown = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest { Email = "nobody@test.local", Password = "Wrong-Passw0rd" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task ApprovedUser_CanUseApp_ButNotAdminEndpoints()
    {
        var (client, user) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(UserAccountStatus.Approved, user.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/portfolios")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/audit-events")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutJsonAsync("/api/v1/settings",
            new UpdateSettingsRequest { ActiveProvider = "Mock", DefaultCurrency = "USD", DefaultBenchmark = "SPY" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync("/api/v1/assets", new CreateAssetRequest { Symbol = "IBM" })).StatusCode);
        // Whole-universe syncs are administrator operations.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync("/api/v1/market-data/sync", new SyncRequest())).StatusCode);
        // Users cannot grant themselves roles.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutJsonAsync($"/api/v1/admin/users/{user.Id}/roles",
            new UpdateUserRolesRequest { Roles = [Roles.Administrator] })).StatusCode);
    }

    [Fact]
    public async Task Suspension_TakesEffectImmediately_AndReactivationRestoresAccess()
    {
        var (client, user) = await _factory.CreateApprovedUserAsync(_admin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/portfolios")).StatusCode);

        await (await _admin.PutJsonAsync($"/api/v1/admin/users/{user.Id}/status",
            new UpdateUserStatusRequest { Status = UserAccountStatus.Suspended, Reason = "Test" })).ReadAsync<UserSummaryDto>();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/portfolios")).StatusCode);

        var reactivated = await (await _admin.PutJsonAsync($"/api/v1/admin/users/{user.Id}/status",
            new UpdateUserStatusRequest { Status = UserAccountStatus.Approved })).ReadAsync<UserSummaryDto>();
        Assert.Equal(UserAccountStatus.Approved, reactivated.Status);
        using var again = _factory.CreateSession();
        await again.LoginAsync(user.Email, ApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/portfolios")).StatusCode);
    }

    [Fact]
    public async Task RejectedUser_HasNoAccess()
    {
        using var client = _factory.CreateSession();
        var email = $"rejected-{Guid.NewGuid():N}@test.local";
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest { Email = email, Password = ApiFactory.UserPassword, DisplayName = "R" });
        var me = await client.LoginAsync(email, ApiFactory.UserPassword);
        var rejected = await (await _admin.PutJsonAsync($"/api/v1/admin/users/{me.Id}/status",
            new UpdateUserStatusRequest { Status = UserAccountStatus.Rejected, Reason = "Unknown applicant" })).ReadAsync<UserSummaryDto>();
        Assert.Equal(UserAccountStatus.Rejected, rejected.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/portfolios")).StatusCode);
    }

    [Fact]
    public async Task Users_CannotSeeOrChangeEachOthersData()
    {
        var (alice, _) = await _factory.CreateApprovedUserAsync(_admin);
        var (bob, _) = await _factory.CreateApprovedUserAsync(_admin);
        var portfolio = await (await alice.PostJsonAsync("/api/v1/portfolios",
            new CreatePortfolioRequest { Name = "Alice", Settings = new PortfolioSettingsDto { Universe = ["AAPL", "MSFT", "JPM"], MaxWeight = 0.5 } }))
            .ReadAsync<PortfolioDto>();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/portfolios/{portfolio.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/portfolios/{portfolio.Id}/holdings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/portfolios/{portfolio.Id}/trade-proposals")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/portfolios/{portfolio.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostJsonAsync($"/api/v1/portfolios/{portfolio.Id}/optimization-runs", new RunOptimizationRequest())).StatusCode);
        var bobsList = await (await bob.GetAsync("/api/v1/portfolios")).ReadAsync<List<PortfolioSummaryDto>>();
        Assert.DoesNotContain(bobsList, p => p.Id == portfolio.Id);
        var alicesList = await (await alice.GetAsync("/api/v1/portfolios")).ReadAsync<List<PortfolioSummaryDto>>();
        Assert.Contains(alicesList, p => p.Id == portfolio.Id);
    }

    [Fact]
    public async Task UnsafeRequests_WithoutAntiforgeryToken_AreRejected()
    {
        var (client, _) = await _factory.CreateApprovedUserAsync(_admin);
        using var raw = new HttpRequestMessage(HttpMethod.Post, "/api/v1/portfolios")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new CreatePortfolioRequest { Name = "x" }),
        };
        raw.Headers.Add("X-XSRF-TOKEN", "forged-token");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(raw)).StatusCode);
    }

    [Fact]
    public async Task Administrators_CannotLockThemselvesOut()
    {
        var me = await (await _admin.GetAsync("/api/v1/auth/me")).ReadAsync<CurrentUserDto>();
        Assert.True(me.IsAdministrator);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PutJsonAsync($"/api/v1/admin/users/{me.Id}/status",
            new UpdateUserStatusRequest { Status = UserAccountStatus.Suspended })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PutJsonAsync($"/api/v1/admin/users/{me.Id}/roles",
            new UpdateUserRolesRequest { Roles = [Roles.User] })).StatusCode);
    }

    [Fact]
    public async Task Admin_CanPromoteUser_AndActionsAreAudited()
    {
        var (client, user) = await _factory.CreateApprovedUserAsync(_admin);
        var promoted = await (await _admin.PutJsonAsync($"/api/v1/admin/users/{user.Id}/roles",
            new UpdateUserRolesRequest { Roles = [Roles.Administrator] })).ReadAsync<UserSummaryDto>();
        Assert.Contains(Roles.Administrator, promoted.Roles);
        Assert.Contains(Roles.User, promoted.Roles);

        // Role claims are refreshed at sign-in.
        using var fresh = _factory.CreateSession();
        await fresh.LoginAsync(user.Email, ApiFactory.UserPassword);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v1/admin/users")).StatusCode);

        var audit = await (await _admin.GetAsync("/api/v1/admin/audit-events?pageSize=200")).ReadAsync<PagedResult<AuditEventDto>>();
        Assert.Contains(audit.Items, e => e.Action == AuditActions.UserApproved && e.TargetId == user.Id.ToString());
        Assert.Contains(audit.Items, e => e.Action == AuditActions.RolesChanged && e.TargetId == user.Id.ToString());
        Assert.Contains(audit.Items, e => e.Action == AuditActions.AdminBootstrapped);
        Assert.Contains(audit.Items, e => e.Action == AuditActions.Login && e.ActorUserId == user.Id);
        client.Dispose();
    }

    [Fact]
    public async Task Admin_ListsPendingApplicationsFirst()
    {
        using var client = _factory.CreateSession();
        var email = $"applicant-{Guid.NewGuid():N}@test.local";
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest { Email = email, Password = ApiFactory.UserPassword, DisplayName = "Applicant" });
        var pending = await (await _admin.GetAsync("/api/v1/admin/users?status=Pending")).ReadAsync<PagedResult<UserSummaryDto>>();
        Assert.Contains(pending.Items, u => u.Email == email);
        Assert.All(pending.Items, u => Assert.Equal(UserAccountStatus.Pending, u.Status));
        var search = await (await _admin.GetAsync($"/api/v1/admin/users?search={Uri.EscapeDataString("applicant-")}")).ReadAsync<PagedResult<UserSummaryDto>>();
        Assert.Contains(search.Items, u => u.Email == email);
    }
}
