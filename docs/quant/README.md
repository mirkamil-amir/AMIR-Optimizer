# Quantitative knowledge system

How MatrixView keeps its formulas, their documentation and the agent skills in step with the code.

| File | Role | Edited by |
|---|---|---|
| [formula-reference.md](formula-reference.md) | canonical formula entries (math, conventions, code, tests, status) | people/agents, reviewed |
| [formula-index.json](formula-index.json) | machine-readable index generated from the reference | `maintain.py index --write` only |
| [audit-2026-10-28.md](audit-2026-10-28.md) | audit method, findings, remaining-risk register | per audit |
| [subsystem-inventory.md](subsystem-inventory.md) | what exists, data flow, process review | when a subsystem changes |
| `scripts/quant/manifest.json` | which code is quantitative, formula-free files, review paths, skill coverage | when files move |
| `scripts/quant/maintain.py` | the checks below (Python 3 standard library, offline, deterministic) | – |
| `.claude/skills/*/SKILL.md` | task-oriented instructions per subsystem | with the code they describe |

## Commands

Run from the repository root (`python3` ≥ 3.10; .NET 10 SDK for `test-quant`).

| Command | What it checks | Exit 1 when |
|---|---|---|
| `python3 scripts/quant/maintain.py validate` | unique IDs, required metadata, allowed statuses, every `path#Symbol` and test exists, every quantitative file mapped, index up to date, all relative doc links/anchors | any error |
| `python3 scripts/quant/maintain.py index --write` | regenerates `formula-index.json` | – |
| `python3 scripts/quant/maintain.py check-skills` | skill frontmatter, repository paths and links inside skills, skill ↔ manifest coverage, skills named in AGENTS.md/CLAUDE.md | any error |
| `python3 scripts/quant/maintain.py review-changes [--base REF] [--output F] [--strict]` | changed files and symbols vs. base → affected formulas, skills, tests, gaps; markdown report | `--strict`: quant code changed, reference not updated, no acknowledgement |
| `python3 scripts/quant/maintain.py audit [--fail-on-stale]` | status counts, open findings, untested formulas, entries whose code has commits after their Verified date, entries older than 180 days | `--fail-on-stale` and stale entries |
| `python3 scripts/quant/maintain.py test-quant` | `dotnet test` unit + ML projects, Vitest worked examples/interpretations (skipped without `node_modules`) | a test fails |
| `python3 scripts/quant/maintain.py full-check [--integration]` | all of the above (+ `./scripts/test.sh` with PostgreSQL) | any part fails |

`review-changes` compares the working tree (staged, unstaged, untracked) with `--base` (default: merge-base with
`origin/main`/`main`). Symbol detection (enclosing C#/TypeScript declarations of changed lines, and usages of formula
symbols in other changed files) is a **heuristic** that points a reviewer at code; it never proves correctness. A
change that needs no reference update is acknowledged with a commit trailer, e.g.
`Formula-Review: refactoring only, outputs bit-identical (BacktestTests)`.

## Workflow for a change to quantitative code

1. Before editing: find the entry (`grep -n "path/to/File.cs" docs/quant/formula-reference.md`) and read its conventions,
   edge cases and tests. Load the skill named in its `Skills` row.
2. Change the code; add or update a test that would fail on the old behaviour (hand-computed value or an independent
   re-implementation – never the production function compared with itself).
3. Update the entry: formula text, `Code` symbols, `Tests`, `Status` (and `Severity` for defects), `Verified` date. A new
   formula gets the next free ID of its group (IDs are never reused or renumbered). Run `index --write`.
4. Update the skill and the docs that describe the behaviour (AGENTS.md rule 10); new ML feature formulas also bump
   `FeatureCatalog.Version`.
5. `python3 scripts/quant/maintain.py full-check` (and `./scripts/test.sh` for API changes); attach the
   `review-changes` report to the pull request.

## Adding things

* **A new quantitative file:** reference it from an entry's `Code`, or list it under `noFormulaFiles` with a reason;
  a new directory of quantitative code goes into `quantPaths`.
* **A new skill:** `.claude/skills/<name>/SKILL.md` with `name`/`description` frontmatter, an entry in the manifest's
  `skills` map (the code it covers) and a mention in AGENTS.md/CLAUDE.md – `check-skills` enforces all three.
* **A new status or field:** extend `STATUSES`/`REQUIRED_FIELDS` in `maintain.py` and this README together.

## CI

`.github/workflows/quant-maintenance.yml` runs on pushes and pull requests:

* `knowledge` (every change, seconds): `validate`, `check-skills`, `review-changes` (report in the job summary and as an
  artifact; `--strict` on pull requests).
* `quant-tests` (only when quantitative code, tests or the reference change): `test-quant --no-frontend` on .NET 10.
* Weekly schedule (Monday 06:00 UTC): `audit` and `test-quant`.

The workflow was added on 2026-10-28. Integration (PostgreSQL) and end-to-end suites run in `ci.yml`
([testing.md](../testing.md#continuous-integration)). No step calls a language model or needs a secret.

## Optional AI-assisted review

An agent can review a change reproducibly: input = the `review-changes` report + the listed entries of the formula
reference + the diff; task = re-derive each affected formula and compare (the `quant-formulas` skill describes the
procedure); output = findings classified with the audit categories. Its findings are advisory and never replace the
deterministic checks or a person's review; nothing in CI depends on it.
