# AMIR Optimizer – Claude Code notes

@AGENTS.md

## Claude-specific

- Skills: `/run-app` (start + verify the stack), `/ship-change` (checklist after any change), `/ml-experiment`
  (run ML Optimizations through the API, backtest/compare them within a portfolio, report honestly).
- UI names: "Math Optimization" (`/portfolios/:id/optimize`, an `OptimizationRun`) and "ML Optimization"
  (`/portfolios/:id/ml-optimize`, an `MlExperiment` linked to that portfolio) – two tabs of a portfolio with one shared
  layout (results in `ResultSection`s, never tabs; "What your result means" last). Keep both pages alike when changing
  one. Backtest and Strategy comparison work on any optimization of one portfolio. Use these names in UI text, docs and tests.
- Portfolio tabs: Overview · Holdings · Math Optimization · ML Optimization · Optimization runs · Backtest ·
  Strategy comparison · Transactions – every tab stays under `/portfolios/:id/…` (no Performance tab: its charts are on the Overview, each
  once). The background world map (`components/world-map.tsx`) is decorative and `aria-hidden` (its text counterpart is the
  header's `MapControl`); pages light markets with `useMapHighlight`, stock interactions use `useMapSignal`. The camera is a
  CSS transform set imperatively (`lib/map-camera.ts`) – never put camera positions into React state or context.
- No decorative icons (the owner finds them childish): buttons, navigation, badges, alerts, empty states and toasts are
  text only. Icons only where they are the control itself (fold/sort/pager arrows, remove ×, phone menu, spinner, delete
  bin). Explanations use `InfoTip of={label}` – the label itself shows the tip on hover/focus, unmarked (no ⓘ, no underline).
- Typography: `.heading-display` (Sora) only for page titles, section headings and the wordmark; body,
  tables and figures stay Geist. Text outside cards must stay readable over the map: put dense lists on `.map-surface`.
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
