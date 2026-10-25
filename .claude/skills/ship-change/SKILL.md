---
name: ship-change
description: Checklist to finish any code change in AMIR Optimizer – build, all test suites (backend with PostgreSQL, ML, Vitest, Playwright), EF migration, security/ownership checks, container rebuild and docs. Use after editing backend, frontend, ML or schema code, before reporting the change as done.
---

# Ship a change

Toolchain once per shell: `export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"`

1. **Schema changed?** (entity or `AppDbContext`)
   ```bash
   dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations
   dotnet build        # required: the migration files are written after the tool's build
   ```
   New user-owned entity? Give it `OwnerId`, filter every query by `ICurrentUser`, and add a cross-user 404 test.
2. **Security review of new endpoints:** default policy is "approved user"; admin-only → `Administrator` policy;
   unsafe methods are antiforgery-protected automatically; expensive work → `[EnableRateLimiting(RateLimitPolicies.Expensive)]`;
   audit security-relevant actions via `IAuditLog`.
3. **Backend tests:** `./scripts/test.sh` – unit, ML and integration (real PostgreSQL) must all pass. Add tests:
   financial math → `UnitTests` (closed-form / hand-computed expectations); ML logic → `MachineLearning.Tests`
   (leakage: data after t must not change anything at t); API behaviour → `IntegrationTests` (`ApiFactory`).
4. **ML changes:** feature formula changed → bump `FeatureCatalog.Version`. Keep the ML project free of
   mean-variance optimizer references (architecture test). Never report training metrics as performance.
5. **API contract changed?** Update DTOs in `Application/Dtos`, mirror them in `frontend/.../src/lib/types.ts` and hooks in `lib/queries.ts`.
6. **Frontend** (in `frontend/portfolio-dashboard`, prefer `./node_modules/.bin/…` over `npx`):
   `npm test` · `npm run build` · `npm run lint` (0 errors). Add a Vitest test for new logic in `lib/` or shared components.
   After `npm ci`, restore the `node_modules` → `node_modules.nosync` symlink (docs/testing.md).
7. **Running stack:** `podman compose up --build -d` (or only `api` / `web`), then `curl -s localhost:5080/health/ready` → Healthy.
   A schema change is applied by the `migrate` service on `up`. Builds fail with "no space left on device" when the Mac
   is full: check `df -h /`, then `podman machine ssh sudo fstrim -av` (frees the VM's unused blocks, deletes nothing);
   never prune volumes.
8. **End-to-end** (user-visible flows: auth, prices, selection, Mathematical/ML Optimization, backtest, comparison, learn, admin): `./scripts/e2e.sh`
   from the repo root – builds a disposable `po-e2e` stack (Mock data, own DB and ports), runs Playwright, removes it.
   **Never** point Playwright at the development stack: it creates accounts (the setup step refuses non-E2E targets).
9. **Visual change?** Screenshot the page (Playwright or Chrome tools) and check labels, legends (once), overflow, dark mode.
   No decorative icons (text-only buttons, navigation, badges, alerts); explanations via `InfoTip of={label}` (the label
   shows the tip, no ⓘ, no underline); an icon-only control needs an `aria-label`. After rebuilding the stack, tell the user to reload open
   tabs: a tab from before the rebuild requests old bundle files and fails with "Failed to fetch dynamically imported module".
10. **Docs:** `README.md` (usage/config/API) and the matching `docs/*.md` (architecture, database, market-data,
    quantitative-strategies, machine-learning, security, testing, deployment, decisions, implementation-status,
    limitations). Strategy or model change → also `frontend/src/lib/strategy-guide.ts` and `interpretation.ts`.
    Durable rules/pitfalls → `AGENTS.md` (keep it short; `CLAUDE.md` imports it).
11. Report what was verified (commands + pass counts) and what was not. Commit only if the user asks.
