---
name: ship-change
description: Checklist to finish any code change in MatrixView – build, all test suites (backend with PostgreSQL, ML, Vitest, Playwright), EF migration, security/ownership checks, container rebuild and docs. Use after editing backend, frontend, ML or schema code, before reporting the change as done.
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
   financial math → `UnitTests` (closed-form / hand-computed expectations or an independent re-implementation, never
   the production function against itself); ML logic → `MachineLearning.Tests` (leakage: data after t must not change
   anything at t); API behaviour → `IntegrationTests` (`ApiFactory`).
   **Quantitative code changed?** `python3 scripts/quant/maintain.py review-changes` lists the affected formula entries,
   skills and tests: update each entry in `docs/quant/formula-reference.md` (Status, Code, Tests, Verified), run
   `python3 scripts/quant/maintain.py index --write`, and load the subsystem skill (`/quant-formulas`,
   `/optimization-backtesting`, `/ml-pipeline`, `/market-data-fx`, `/portfolio-accounting`).
4. **ML changes:** feature formula changed → bump `FeatureCatalog.Version`. Keep the ML project free of
   mean-variance optimizer references (architecture test). Never report training metrics as performance.
5. **API contract changed?** Update DTOs in `Application/Dtos`, mirror them in `frontend/.../src/lib/types.ts` and hooks in `lib/queries.ts`.
6. **Frontend** (in `frontend/portfolio-dashboard`, prefer `./node_modules/.bin/…` over `npx`):
   `npm test` · `npm run build` · `npm run lint` (0 errors). Add a Vitest test for new logic in `lib/` or shared components.
   After `npm ci`, restore the `node_modules` → `node_modules.nosync` symlink (docs/testing.md).
7. **Running stack:** `podman compose up --build -d` (or only `api` / `web`), then `curl -s localhost:5080/health/ready` → Healthy.
   A schema change is applied by the `migrate` service on `up`. **Disk space – free it automatically, no need to ask**
   (owner's rule, 2026-10-30): before every image build or `./scripts/e2e.sh` run `./scripts/free-space.sh --check`
   (exit 1 below 10 GB free); below that, or on "no space left on device", a podman "ssh: handshake failed"/"unexpected
   EOF" or a build dying mid-way, run `./scripts/free-space.sh` (untagged images, the disposable `po-e2e*` project,
   fstrim) and report what it freed. VM unreachable and disk full → `./scripts/free-space.sh --restart-vm`, then restart
   the stack in order (db until `pg_isready` → `migrate` until it exits 0 → api until `/health/ready` → web), resync the
   VM clock and confirm the data with read-only counts. Run **one image build at a time** (two parallel builds filled the
   Mac on 2026-10-29). The script never touches other volumes (the development database), other projects' containers or
   images, package caches or files outside the VM – anything beyond it (npm/NuGet caches, Trash, user files) still needs
   the owner's yes.
8. **End-to-end** (user-visible flows: auth, prices, selection, Mathematical/ML Optimization, backtest, comparison, learn, admin): `./scripts/e2e.sh`
   from the repo root – builds a disposable `po-e2e` stack (Mock data, own DB and ports), runs Playwright, removes it.
   **Never** point Playwright at the development stack: it creates accounts (the setup step refuses non-E2E targets).
9. **Visual change?** Screenshot the page (Playwright or Chrome tools) and check labels, legends (once), overflow, dark mode.
   No decorative icons (text-only buttons, navigation, badges, alerts); explanations via `InfoTip of={label}` (the label
   shows the tip, no ⓘ, no underline); an icon-only control needs an `aria-label`; running work uses `MatrixLoader`/`WorkingPanel`, not a spinner. After rebuilding the stack, tell the user to reload open
   tabs: a tab from before the rebuild requests old bundle files and fails with "Failed to fetch dynamically imported module".
10. **Docs:** `README.md` (usage/config/API) and the matching `docs/*.md` (architecture, database, market-data,
    quantitative-strategies, machine-learning, security, testing, deployment, decisions, implementation-status,
    limitations, `docs/quant/*`). Strategy or model change → also `frontend/portfolio-dashboard/src/lib/strategy-guide.ts`
    and `interpretation.ts`. Durable rules/pitfalls → `AGENTS.md` (keep it short; `CLAUDE.md` imports it); a skill whose
    subsystem changed → its `SKILL.md` (`/maintain-knowledge`).
11. **Knowledge checks:** `python3 scripts/quant/maintain.py validate && python3 scripts/quant/maintain.py check-skills`
    (formula reference, index, doc links, skills) – CI runs the same (`.github/workflows/quant-maintenance.yml`).
12. Report what was verified (commands + pass counts) and what was not.
13. **Commit and push: ask first, every time** (owner's rule, 2026-10-30). Never commit, push or open a pull request on
    your own – not even after a "commit and build" for an earlier change: each approval covers only the change it was
    given for. Before the first commit of a change, ask in one question: commit? which base (normally the latest
    `origin/main` – check `git fetch` first; stack on an unmerged branch only if the owner agrees)? push? A pending test
    run is no reason to commit early; report it and ask.
14. **Branch and pull-request names** (owner's rule, 2026-10-30). Every new branch gets the next ID, starting at AMIR-1:
    * branch, lowercase, words joined by underscores – never hyphens: `<type>/amir_<n>_<short_description>`,
      e.g. `feat/amir_2_searchable_lists`;
    * pull-request title: `<type>(AMIR-<N>): <Description>` – type lowercase, ID uppercase, description in sentence case,
      e.g. `chore(AMIR-1): Commit, branch naming and disk space rules`; the commit subject uses the same format;
    * types: `feat` (new feature), `fix` (bug fix), `chore` (maintenance, dependencies, tooling), `docs` (documentation
      only), `refactor` (no behaviour change), `perf` (performance), `test` (tests only), `ci` (workflows);
    * next ID = highest AMIR number used so far + 1, never reused – find it with
      `git fetch -q origin && { git branch -a; git log --merges --format=%s origin/main; } | grep -oiE 'amir[-_][0-9]+' | grep -oE '[0-9]+' | sort -n | tail -1`
      (merge commits keep the names of deleted branches; AMIR-1 was first pushed as `chore/amir-1-…`, before
      underscores were the rule); no match → AMIR-1;
    * propose the branch name, type and PR title together with the commit question (step 13); one branch = one ID = one PR;
    * CI checks title and branch on every PR (`.github/workflows/pr-title.yml`); check before proposing:
      `./scripts/check-pr-name.sh "<title>" "<branch>"`. Dependabot branches are exempt.
