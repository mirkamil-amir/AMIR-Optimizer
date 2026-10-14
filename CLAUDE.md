# AMIR Optimizer – Claude Code notes

@AGENTS.md

## Claude-specific

- Skills: `/run-app` (start + verify the stack), `/ship-change` (checklist after any change), `/ml-experiment`
  (run and honestly report ML experiments through the API).
- Integration tests in Rider/IDEs: Testcontainers reads `~/.testcontainers.properties` (Podman socket,
  `ryuk.disabled=true`) – set up on this machine; without it every integration test fails.
- Browser checks: Playwright from `frontend/portfolio-dashboard` against http://localhost:5173 (dev) or 8081
  (containers) with screenshots into the scratchpad; delete throwaway specs afterwards. Anything that creates
  accounts or runs must use `./scripts/e2e.sh`, never the development stack.
- The development database contains the user's data. Before any destructive SQL: dry run, backup
  (`pg_dump` into `../PortfolioOptimizer-backups/`), transaction, and report what was deleted.
- Commit only when asked; nothing is committed on `main` yet.
