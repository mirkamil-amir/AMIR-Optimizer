# MatrixView – Claude Code notes

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
- Navigation labels (`SECTION_NAMES` in `lib/navigation.ts`, shared by sidebar and tabs): top bar Matrix · AI Probe · Wire · Radar · Atlas · Codex · Lexicon · Settings; sidebar
  Lens · Forge · Solver · Pattern · Trace · Rewind · Arena. Portfolio tabs: Core · Solver · Pattern · Trace · Rewind · Arena ·
  Ledger (Ledger's sub-tabs keep plain names). Each entry's `hint` gives the plain name (Backtesting, …) as tooltip and
  `aria-description`; page titles, docs and explanatory text keep the plain names. Buttons: a section's main action
  echoes its tab (Forge portfolio, Solve, Learn patterns, Rewind, Face off) and section links say "Open <tab>"; money,
  deletion, data, account and admin actions stay literal (D-47). Every tab stays under `/portfolios/:id/…` (no
  Performance or Holdings tab: their charts, cards and the positions table are on the Overview, each once; no efficient
  frontier there). A page title that repeats the tab or sidebar entry right above it is screen-reader only
  (`PageHeader titleHidden`); the app name is just "MatrixView" (no tagline). The background world map (`components/world-map.tsx`) is decorative and `aria-hidden` (its text counterpart is the
  header's `MapControl`); pages light markets with `useMapHighlight`, stock interactions use `useMapSignal`. The camera is a
  CSS transform set imperatively (`lib/map-camera.ts`) – never put camera positions into React state or context.
- No decorative icons (the owner finds them childish): buttons, navigation, badges, alerts, empty states and toasts are
  text only. Icons only where they are the control itself (fold/sort/pager arrows, remove ×, phone menu, delete bin).
  Work in progress shows the logo's matrix lighting up dot by dot (`MatrixLoader`, `WorkingPanel` in
  `components/matrix-loader.tsx`) – never a spinner. Explanations use `InfoTip of={label}` – the label itself shows the tip on hover/focus, unmarked (no ⓘ, no underline).
- Typography: `.heading-display` (Sora) only for page titles, section headings and the wordmark; `.font-nav` (Sora too,
  chosen from nine options) for the top bar and sidebar; body, tables and figures stay Geist. Sidebar and top-bar entries magnify near the
  pointer (`lib/nav-zoom.ts`, CSS variable set imperatively); the selected entry stays enlarged. Text outside cards must stay readable over the map: put dense lists on `.map-surface`.
- Long lists: `SearchableTable` (search, paging, first/last, scroll under a fixed header); big matrices: `PannableArea` +
  windowed rendering + Full screen. Never a plain list that grows with the data – see `/platform-architecture`
  › "Frontend lists and large grids".
- Disk: the Podman VM disk file (`~/.local/share/containers/podman/machine`) grows with every image build and never
  shrinks by itself; when the Mac runs out of space, `podman machine ssh sudo fstrim -av` returns the VM's free blocks
  (2026-10-24: 47 → 20 GB) without deleting anything. Never prune volumes (the development database lives in one).
- Integration tests in Rider/IDEs: Testcontainers reads `~/.testcontainers.properties` (Podman socket,
  `ryuk.disabled=true`) – set up on this machine; without it every integration test fails.
- Browser checks: Playwright from `frontend/portfolio-dashboard` against http://localhost:5173 (dev) or 8081
  (containers) with screenshots into the scratchpad; delete throwaway specs afterwards. Anything that creates
  accounts or runs must use `./scripts/e2e.sh`, never the development stack.
- The development database contains the user's data. Before any destructive SQL: dry run, backup
  (`pg_dump` into `../PortfolioOptimizer-backups/`), transaction, and report what was deleted.
- Commit only when asked, on a feature branch; `main` receives the user's merged pull requests.
