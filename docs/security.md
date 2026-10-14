# Security

The platform is a multi-user web application with cookie authentication, administrator approval and owner-scoped
data. It places no real orders and stores no payment or brokerage credentials.

## Accounts and approval

- **Registration** (`POST /auth/register`) creates an account with status *Pending* and role *User*. The response is
  the same whether or not the e-mail is already registered, so it does not reveal existing accounts.
- **Approval** by an administrator (`PUT /admin/users/{id}/status`): *Approved*, *Rejected*, *Suspended*, or back to
  *Approved* (reactivation). Administrators cannot change their own status or remove their own administrator role,
  and the last administrator cannot be removed.
- **Bootstrap administrator** from `Admin:Email` / `Admin:Password`, created (or promoted) only while **no**
  administrator exists. Afterwards the configuration is ignored; an existing administrator or password is never
  changed by configuration.
- **Passwords:** ASP.NET Core Identity hashing (PBKDF2), at least 10 characters with upper case, lower case and a
  digit. **Lockout** for 15 minutes after 5 failed attempts. Sign-in errors are generic.

## Sessions

- HttpOnly cookie `po.auth`, `SameSite=Strict`, `Secure` on HTTPS requests, 8 h sliding expiration
  (`Authentication:SessionHours`).
- Every request is authorized against the account's **current** status in the database (`ApprovedAccountHandler`),
  so suspension or rejection takes effect immediately, not when the cookie expires. The security stamp is
  revalidated every minute, so a password change or role change ends other sessions.
- Signing out reloads the browser app, which discards in-memory state (cached data, the stock selection, the
  sign-in redirect target) so nothing carries over to the next person using the browser.
- Data-protection keys (which protect cookies and antiforgery tokens) are stored in the database, so sessions
  survive API restarts and work across instances.

## Authorization

| Policy | Applies to |
|---|---|
| Fallback: approved account | every endpoint unless marked otherwise |
| Anonymous | `auth/antiforgery`, `auth/register`, `auth/login`, `/health`, `/health/ready` |
| Authenticated (any status) | `auth/me`, `auth/logout`, `auth/change-password` |
| Administrator | `admin/*` (users, audit, overview, catalogue and constituent imports, manual quote refresh), `PUT /settings`, `POST/PATCH /assets` |
| Anonymous (test marker) | `GET /api/v1/system/test-environment` – returns only whether the environment is `E2E` |

**Owner scoping.** Portfolios, optimization runs, backtests, ML experiments and strategy comparisons carry an
`OwnerId`. Every query filters by the current user, and another user's id returns 404 (not 403), so ids do not leak.
Integration tests cover cross-user access for each area. The frontend route guards are only a usability layer.

## Request protection

- **Antiforgery:** unsafe methods (POST/PUT/PATCH/DELETE) require the `X-XSRF-TOKEN` header matching the
  session-bound token (`ValidateAntiforgeryOnUnsafeMethodsFilter`). The readable `XSRF-TOKEN` cookie carries the
  token for the SPA; the token is rotated on sign-in and sign-out.
- **Rate limits:** 600 requests/min per IP globally; 10/min per IP for register, login and change-password;
  30/min per user for expensive jobs (optimization, sync, backtests, ML experiments, comparisons); 60/min per user for
  on-demand quote refreshes. `429` on excess.
- **Headers:** API and nginx send `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`; nginx adds a Content-Security-Policy (`default-src 'self'`, no inline scripts,
  `frame-ancestors 'none'`).
- **CORS:** only configured origins (`Cors:AllowedOrigins`). In compose the browser talks to nginx on the same origin.
- **Forwarded headers** are trusted only when `ReverseProxy:TrustForwardedHeaders` is enabled (compose: behind nginx).
- **Validation and errors:** request DTOs are validated; errors are Problem Details without stack traces outside
  Development. Request logging does not log bodies, cookies or authorization headers.

## Audit trail

`AuditEvent` rows record registration, sign-in (success and failure), sign-out, password changes, user status and
role changes, administrator bootstrap, settings changes, asset and catalogue changes, paper-trade executions and
portfolio deletion, with actor, target, IP address and time. Audit rows are written through a separate database
context, so they persist even when the surrounding operation fails. Administrators can view them under **Admin
settings → Audit trail**.

## Test data

Automated tests never run against real databases (per-class integration databases; e2e on a disposable stack
guarded by the test-environment marker). Accounts under the reserved `.test` domain are treated as test data: hidden
from the admin user list by default, excluded from admin metrics, refused at registration in Production, removable with
`purge-test-data` (dry run by default, refuses Production, one transaction, audited). See [database.md](database.md).

## Database roles

| Role | Rights | Used by |
|---|---|---|
| Owner (`POSTGRES_OWNER_USER`) | owns database and schema; DDL | `migrate` command, local development |
| Application (`APP_DB_USER`) | `CONNECT`, `USAGE` on `public`, `SELECT/INSERT/UPDATE/DELETE` on tables, sequence usage; **no DDL** | the running API in compose/production |

`deploy/postgres/init/01-app-role.sh` creates the application role when the data directory is initialized and sets
default privileges so tables created by later migrations are usable by it. The API refuses to start against an
outdated schema instead of migrating with the application role.

## Secrets

- Never committed: `.env` is gitignored; `.env.example` contains placeholders only.
- Connection strings, administrator password and the Alpha Vantage key come from environment variables or
  user-secrets. The API never returns them; provider error messages redact the API key.
- The SQLite import tool reads its connection string from `PO_IMPORT_POSTGRES` so it does not appear in shell
  history or process lists.
- Model artifacts are created only by the server and verified by SHA-256 before loading; user input never names a
  file path.

## Not covered (deployment responsibilities)

- TLS termination: put the stack behind a TLS-terminating reverse proxy for anything beyond localhost
  (see [deployment](deployment.md)). Cookies become `Secure` automatically on HTTPS.
- No e-mail confirmation, password reset by e-mail, or multi-factor authentication. An administrator can suspend an
  account; a forgotten password currently needs an administrator to create a new account.
- Swagger UI is enabled only in Development. Set `ASPNETCORE_ENVIRONMENT=Production` outside local use.
