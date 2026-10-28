---
name: maintain-knowledge
description: Keep MatrixView's formula reference, skills and quantitative docs in step with the code using scripts/quant/maintain.py (validate, index, check-skills, review-changes, audit, test-quant, full-check) and the quant-maintenance CI workflow. Use after changing quantitative code, a skill, the manifest or docs; before opening a pull request; when CI's knowledge or quant-tests job fails; or when asked to audit documentation drift.
---

# Maintain the knowledge system

Reference: `docs/quant/README.md` (commands, workflow, CI). Tool: `scripts/quant/maintain.py`; configuration:
`scripts/quant/manifest.json`; workflow: `.github/workflows/quant-maintenance.yml`.

## When to run what
| Situation | Command |
|---|---|
| Any change, before reporting it done | `python3 scripts/quant/maintain.py validate && python3 scripts/quant/maintain.py check-skills` |
| Quantitative code changed | `python3 scripts/quant/maintain.py review-changes` → act on every listed formula/skill/gap, then `test-quant` |
| Formula entry edited | `python3 scripts/quant/maintain.py index --write` (CI fails on a stale index) |
| Periodic or "is the documentation current?" | `python3 scripts/quant/maintain.py audit` |
| Before a pull request | `python3 scripts/quant/maintain.py full-check` (+ `--integration` when API behaviour changed) |

## Fixing failures (the message names the fix)
* "quantitative code file is not referenced" – add the file to an entry's `Code` (`path#Symbol`) or to
  `noFormulaFiles` in the manifest with a real reason. New quantitative directory → `quantPaths`.
* "symbol … not found" – a rename: update the entry; check the formula still means the same.
* "test … not found" – use `Class.Method` of an existing test (or a test file path for Vitest).
* "out of date" (index) – `index --write`; never edit `docs/quant/formula-index.json` by hand.
* "broken link / missing anchor" – headings are slugged GitHub-style (`### F-RET-001 · Simple daily return` →
  `#f-ret-001--simple-daily-return`).
* check-skills: frontmatter `name` = directory, description says when to use the skill, every repository path in
  backticks exists, every skill appears in the manifest's `skills` map and in AGENTS.md/CLAUDE.md.
* review-changes `--strict` (pull requests): quantitative code changed without a formula-reference change – update the
  affected entries, or add a commit trailer `Formula-Review: <why no entry changes>`.

## Writing and updating skills
One skill per real responsibility (not per topic); each states purpose and when it applies, data flow, key files and
symbols, rules, edge cases, how to change safely, exact test commands, limitations and related skills. Link to the
canonical doc instead of copying it; never invent files, commands, endpoints or settings – verify with `git grep`.
Label proposals separately from current behaviour. Keep `CLAUDE.md` (imports AGENTS.md) pointing at every skill.

## Honesty rules for reports
The tool's checks are deterministic; its symbol mapping is a heuristic and proves nothing about correctness. Report
what ran and its result; say when CI has not run (the workflow was added 2026-10-28 and has not run on GitHub yet).

## Related
`/quant-formulas` (audit procedure for a formula), `/ship-change` (full checklist), all subsystem skills.
