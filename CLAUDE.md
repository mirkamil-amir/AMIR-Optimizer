# Matrix – Claude Code notes

@AGENTS.md

## Claude-specific

- Skills: `/run-app` (start + verify the stack), `/ship-change` (checklist after any change), `/ml-experiment`
  (run ML Optimizations through the API, backtest/compare them within a portfolio, report honestly); subsystem skills
  `/quant-formulas`, `/optimization-backtesting`, `/market-data-fx`, `/ml-pipeline`, `/portfolio-accounting`,
  `/platform-architecture`, `/maintain-knowledge` (see AGENTS.md › Skills).
- UI names: "Math Optimization" (`/portfolios/:id/optimize`, an `OptimizationRun`) and "ML Optimization"
  (`/portfolios/:id/ml-optimize`, an `MlExperiment` linked to that portfolio) – two tabs of a portfolio with one shared
  layout (results in `ResultSection`s, never tabs; "What your result means" last). Keep both pages alike when changing
  one. Backtesting and Comparison work on any optimization of one portfolio. Use these names in explanatory UI text, docs and
  tests; navigation uses the branded labels below (owner's choice, 2026-10-27, D-47).
- Navigation labels (`SECTION_NAMES` in `lib/navigation.ts`, shared by sidebar and tabs): top bar View · AI Probe · Wire · Radar · Atlas · Codex · Lexicon · Settings; sidebar
  Lens · Forge · Solver · Pattern · Trace · Rewind · Arena. Portfolio tabs: Core · Solver · Pattern · Trace · Rewind · Arena ·
  Ledger (Ledger's sub-tabs keep plain names). Each entry's `hint` gives the plain name (Backtesting, …) as tooltip and
  `aria-description`; page titles, docs and explanatory text keep the plain names. Buttons: a section's main action
  echoes its tab (Forge portfolio, Solve, Learn patterns, Rewind, Face off) and section links say "Open <tab>"; money,
  deletion, data, account and admin actions stay literal (D-47). Every tab stays under `/portfolios/:id/…` (no
  Performance or Holdings tab: their charts, cards and the positions table are on the Overview, each once; no efficient
  frontier there). A page title that repeats the tab or sidebar entry right above it is screen-reader only
  (`PageHeader titleHidden`); the app name is just "Matrix" (no tagline; "MatrixView" until 2026-11-01, D-49). The background world map (`components/world-map.tsx`) is decorative and `aria-hidden` (its text counterpart is the
  header's `MapControl`); pages light markets with `useMapHighlight`, stock interactions use `useMapSignal`. The camera is a
  CSS transform set imperatively (`lib/map-camera.ts`) – never put camera positions into React state or context.
- Start-up globe (`components/globe-intro.tsx`, `lib/globe.ts`, `lib/intro.ts`, D-56): a dotted globe with arcs between
  the catalogue's exchange cities, drawn on a canvas from the Natural Earth outlines – never the Dribbble video it was
  inspired by (no licence). Always dark. **The sign-in page is the globe screen** (`GlobeScreen` in `pages/auth.tsx`: globe
  left of the sign-in panel, above it on narrow screens; same form, labels and messages as before); after sign-in it grows
  and fades into the app. Signed-in users get the Enter intro once per browser session: it keeps turning until they
  choose "Enter" (focused, so the Enter key works; owner, 2026-11-10) while the app stays inert underneath. Still frame
  with reduced motion; the Enter intro is off in Settings › Appearance and skipped by automated browsers unless `?intro=1`.
- No decorative icons (the owner finds them childish): buttons, navigation, badges, alerts, empty states and toasts are
  text only. Icons only where they are the control itself (fold/sort/pager arrows, remove ×, phone menu, delete bin).
  Work in progress shows the moving logo (`MatrixLoader`, `WorkingPanel` in `components/matrix-loader.tsx`) – never a
  spinner; they register as running work, and the shell's logo moves only then (`lib/logo-animator.ts`, D-49). Explanations use `InfoTip of={label}` – the label itself shows the tip on hover/focus, unmarked (no ⓘ, no underline).
- Typography: `.heading-display` (Sora) only for page titles, section headings and the wordmark; `.font-nav` (Sora too,
  chosen from nine options) for the top bar and sidebar; body, tables and figures stay Geist. Sidebar and top-bar entries magnify near the
  pointer (`lib/nav-zoom.ts`, CSS variable set imperatively); the selected entry stays enlarged. Text outside cards must stay readable over the map: put dense lists on `.map-surface`.
- Long lists: `SearchableTable` (search, paging, first/last, scroll under a fixed header, optional `fullScreen`); big matrices: `PannableArea` +
  windowed rendering + Full screen. Never a plain list that grows with the data – see `/platform-architecture`
  › "Frontend lists and large grids".
- Disk: the Podman VM disk file (`~/.local/share/containers/podman/machine`) grows with every image build and never
  shrinks by itself; when the Mac runs out of space, `podman machine ssh sudo fstrim -av` returns the VM's free blocks
  (2026-10-24: 47 → 20 GB) without deleting anything. Never prune volumes (the development database lives in one).
  Free space automatically when the disk is (nearly) full – `./scripts/free-space.sh` (`--check` before builds, < 10 GB
  → run it; `--restart-vm` when podman cannot connect); one image build at a time; anything beyond the script needs a yes.
- Integration tests in Rider/IDEs: Testcontainers reads `~/.testcontainers.properties` (Podman socket,
  `ryuk.disabled=true`) – set up on this machine; without it every integration test fails.
- Browser checks: Playwright from `frontend/portfolio-dashboard` against http://localhost:5173 (dev) or 8081
  (containers) with screenshots into the scratchpad; delete throwaway specs afterwards. Anything that creates
  accounts or runs must use `./scripts/e2e.sh`, never the development stack.
- The development database contains the user's data. Before any destructive SQL: dry run, backup
  (`pg_dump` into `../PortfolioOptimizer-backups/`), transaction, and report what was deleted.
- Commit only when asked, on a feature branch; `main` receives the user's merged pull requests. Ask before every
  commit and every push – approval is per change, never carried over (owner, 2026-10-30; `/ship-change` step 13).
  Branches `<type>/amir_<n>_<description>` (lowercase, underscores, never hyphens), PR titles and commit subjects
  `<type>(AMIR-<N>): <Description>` (e.g. `chore(AMIR-1): Commit, branch naming and disk space rules`), next
  free number from AMIR-1 on, types feat/fix/chore/docs/refactor/perf/test/ci (`/ship-change` step 14).
