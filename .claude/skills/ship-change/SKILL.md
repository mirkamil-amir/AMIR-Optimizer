---
name: ship-change
description: Checklist to finish any code change in Portfolio Optimizer – build, tests, EF migration, frontend checks, container rebuild and docs. Use after editing backend, frontend or schema code, before reporting the change as done.
---

# Ship a change

Set up the toolchain once per shell:
`export PATH="$HOME/.dotnet:/opt/homebrew/opt/node@22/bin:$PATH" DOTNET_ROOT="$HOME/.dotnet"`

1. **Schema changed?** (entity or `AppDbContext`)
   ```bash
   dotnet ef migrations add <Name> -p src/PortfolioOptimizer.Infrastructure -s src/PortfolioOptimizer.Api -o Persistence/Migrations
   dotnet build        # required: the migration files are written after the tool's build
   ```
2. **Backend:** `dotnet build && dotnet test` – all tests green. Add/adjust tests for new financial logic
   (closed-form or hand-computed expectations in `tests/PortfolioOptimizer.UnitTests`; API behaviour in `IntegrationTests`).
3. **API contract changed?** Update DTOs in `Application/Dtos`, then mirror them in `frontend/.../src/lib/types.ts`
   and hooks in `lib/queries.ts`.
4. **Frontend:** in `frontend/portfolio-dashboard`: `npm run build` (must pass) and `npm run lint` (no new errors).
5. **Rebuild what's running** (only if the stack is running):
   `podman compose up --build -d` (or only `api` / `web`), then verify `curl -s localhost:5080/health/ready` → Healthy.
6. **Visual change?** Open the affected page in Chrome and take one screenshot to confirm.
7. **Docs:** update `README.md` (usage/config) or `docs/*.md` (methodology, data sources, limitations) if behaviour,
   configuration or assumptions changed. Update `CLAUDE.md` only for new conventions/gotchas – keep it short.
8. Report what was verified (commands + results) and what was not. Commit only if the user asks.
