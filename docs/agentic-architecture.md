# Agentic research workflow – architecture

The AI research assistant (`/research`) turns an investor's objective into a reproducible research process:
objective → editable brief → universe research with evidence → portfolio hypotheses from the real optimizer →
critical review → explanation → refinement (proposed, then confirmed) → comparison → deliberate save → reopen.

It is an **orchestrated workflow with bounded model calls**, not an autonomous agent. Code decides every step and every
number; a language model (optional) only proposes structured interpretations of the user's words and writes
explanations that are checked against the computed results. With no model configured the whole workflow still works.

Related: [threat model](threat-model.md) · [evaluation](agent-evaluation.md) · decisions D-21–D-24 in
[decisions.md](decisions.md).

## 1. Audit and ADR answers (2026-10-15)

**Starting point.** The platform already had: catalogue + stored prices for five markets, mean-variance optimizer with
constraints (`OptimizationService`), the Portfolio Reality Check (`RealityCheckService`), strategy comparison, ML lab,
news and company profiles. Every analysis needed the user to know which page to use and which settings matter. No
language model was integrated and no API key for one is configured on this machine.

| Question | Answer |
|---|---|
| What does the assistant decide, and what does code decide? | Code decides the sequence (state machine), which tools run, all validation, all numbers, weights, review flags and suggestions. The model may only propose: brief fields (each with a verbatim quote from the user), a structured refinement (only when the rule parser does not understand the request) and an explanation (only accepted if every number and ticker is in the evidence). |
| One agent or several? | One deterministic orchestrator (`ResearchAgent`) with five typed tools. Multiple autonomous agents would add non-determinism, cost and an attack surface without a task that needs them: the workflow is short, sequential and known in advance. |
| Which model, and where? | Anthropic Claude via the official C# SDK (`Anthropic` NuGet), server-side only, model `claude-opus-5-5` by default, structured JSON output, low effort, 60 s timeout, 2 retries. Off by default (`Agent:Llm:Provider = None`). |
| Source of truth for numbers? | The optimizer (`OptimizationService.EvaluateAsync`) and the review (`RealityCheckService.AnalyzeAsync`) on stored prices. Templates and the validator only quote those values. |
| What is persisted and how is it reproducible? | A `ResearchSession` (owner-scoped) holds the brief, candidate set, hypotheses (inputs + results + config hash), pending refinement and an append-only event log. Hypotheses are **not** optimization runs; only a confirmed save creates a normal `OptimizationRun`, recalculated with the stored inputs and linked back to the session in its diagnostics. |
| What if the model fails, is slow, rate-limited or manipulated? | Every model use has a deterministic path (rule parser, template). Failures, timeouts, refusals, malformed or contradicting output are logged as `rejected` events and the deterministic result is shown with a note. Calculations never depend on the model. |

## 2. Components

```
Browser (pages/research.tsx)                 never sees keys; only calls /api/v1/research
  │
ResearchController (auth: approved user, antiforgery, "Expensive" rate limit on every computing endpoint)
  │
ResearchAgent  ── state machine, budgets, idempotency, events, persistence (concurrency token)
  ├─ BriefInterpreter        rules → optional model proposal (quote-validated) → BriefRules.Complete
  ├─ ToolRunner              owner re-check, confirmation gate, budget, timeout, audit event per call
  │    ├─ search_investable_universe       read-only     StockDiscoveryService + PriceQueryService
  │    ├─ run_portfolio_optimization       calculation   OptimizationService.EvaluateAsync (no save)
  │    ├─ run_sensitivity_and_risk_review  calculation   RealityCheckService.AnalyzeAsync (no save)
  │    ├─ get_company_context              read-only     company profile + 5 stored news items (shown, never sent to the model)
  │    └─ save_research_run                user-data change, requires Confirm = true
  ├─ HypothesisReview        deterministic flags + suggestions with exact changes
  ├─ InterpretationService   template (7 sections) or validated model narrative
  ├─ RefinementInterpreter   rules → optional model proposal → RefinementRules.Check (allow-list + feasibility)
  └─ ILanguageModel          Disabled (default) | Anthropic | Mock (tests)
```

Files: `src/PortfolioOptimizer.Application/Research/*`, `Domain/Research.cs`,
`Infrastructure/Llm/LanguageModels.cs`, `Api/Controllers/ResearchController.cs`.

## 3. State machine

| State | Entered when | Allowed next actions |
|---|---|---|
| `AwaitingInput` | the brief has no supported market (the one blocking question) | edit brief |
| `BriefReady` | market known; universe search running | (transient) |
| `UniverseReady` | shortlist stored | edit brief (re-research), set universe (eligible stocks only), evaluate |
| `Evaluated` | at least one hypothesis | evaluate again (idempotent), propose/apply/discard refinement, compare, save, company context |

The UI shows the steps Objective → Research brief → Candidate universe → Portfolio hypotheses → Critical review →
Refinement → Saved result with status done/current/pending/waiting. Every request runs to completion while the user
waits; nothing runs in the background, and the UI never claims otherwise.

**Budgets per session:** 80 tool calls, 10 hypotheses, 150 000 model tokens (after that, rules only); 50 sessions per
user. Tool timeouts: 60 s default, review 90 s, company context 45 s. Exceeding a budget returns 409 with an explanation.

**Idempotency:** each hypothesis has a SHA-256 `ConfigHash` of its normalized configuration; evaluating an identical
configuration again is skipped with a logged reason. Saving a hypothesis twice returns the existing run.

**Concurrency:** `ResearchSession.Version` is an optimistic concurrency token; concurrent edits get 409.

## 4. Tool registry and permissions

| Tool | Category | Arguments validated | Writes |
|---|---|---|---|
| `search_investable_universe` | ReadOnlyResearch | market known, 4–25 candidates | – |
| `run_portfolio_optimization` | Calculation | objective allow-list, max weight 5–100 %, 2–40 stocks, n × max ≥ 1, covariance method, 126–1260 days, sector limits on known sectors | – |
| `run_sensitivity_and_risk_review` | Calculation | succeeded run | – |
| `get_company_context` | ReadOnlyResearch | symbol format; the agent only allows symbols screened in this session (else 404) | – |
| `save_research_run` | UserDataChange | `Confirm = true`; a portfolio of the user in the hypothesis currency; re-evaluates and flags a data change | `OptimizationRun`, audit `research.run_saved` |

Enforcement is in `ToolRunner`, not in prompts: the session owner is re-checked against the signed-in user on every
call, UserDataChange tools without confirmation are **denied** (logged), budgets and timeouts apply, and every call
writes an event (name, status ok/error/rejected/skipped/denied, duration, short summary). There is no high-impact
category: no tool can place orders, move money, change settings, other users' data or anything outside the session.
Errors map to Problem Details: invalid_arguments 400, unauthorized 404, confirmation_required/budget_exceeded 409,
timeout 504, failed 502.

## 5. Language model configuration

| Setting (env) | Default | Meaning |
|---|---|---|
| `Agent__Llm__Provider` (`AGENT_LLM_PROVIDER` in compose) | `None` | `None` (rules only), `Anthropic`, `Mock` (tests) |
| `Agent__Llm__Model` (`AGENT_LLM_MODEL`) | `claude-opus-5-5` | model id |
| `Agent__Llm__ApiKey` (`ANTHROPIC_API_KEY`) | empty | server-side only; never logged, never sent to the browser |
| `Agent__Llm__TimeoutSeconds` | 60 | per request |
| `Agent__Llm__MaxRetries` | 2 | SDK retries on 429/5xx |
| `Agent__Llm__Effort` | `low` | output effort |

Three purposes, each with a JSON schema and fenced, redacted input:

| Purpose | Input sent | Output accepted only if |
|---|---|---|
| `research-brief` | the redacted objective inside `<user_objective>` | value in the allow-list **and** a verbatim quote that occurs in the user's text; fields the rules already found from the user's words win |
| `refinement` (only if the rules say "unclear") | redacted request, universe symbols, eligible candidates (symbol, name, sector), objective, max weight | each change passes `RefinementRules.Check` (allow-listed operation, known symbol/sector, feasible values); it is still only a proposal the user must confirm |
| `interpret-optimization` | the evidence JSON: objective, cap, window, weights/volatility/risk share per stock, portfolio estimates, review figures, flags, suggestions | `NarrativeValidator`: every number within 0.05 of a value in the evidence (or its rounding), every ticker in the hypothesis, no forbidden promises ("guarantee", "risk-free", "you should buy", …) |

Not sent: e-mail, names, account or portfolio ids, other sessions, news or company descriptions, prices.
Redaction removes e-mail addresses, IBAN-like and phone-like numbers before any model call. Token usage is recorded
per session and shown in the UI. Requests opt into the server-side refusal fallback (`fallbacks: "default"` with the
`server-side-fallback-2026-07-01` beta, as the SDK documentation recommends); a refusal or truncated answer that still
arrives is treated as a failure → deterministic fallback. Live calls to Anthropic were **not** exercised in this pass
(no key available); the provider compiles against SDK 12.57.0 and is covered only by the mock-based tests.

## 6. Evidence and provenance

* Candidates carry observations, last price date, stale flag, one-year volatility/return/drawdown from stored closes, the
  inclusion/exclusion reason and the SIMULATED flag; the set records the price source, retrieval time and criterion.
* Hypotheses embed the full `OptimizationRunDto` (window, observations, price source, data snapshot hash, settings,
  diagnostics) and the review (`RealityCheckDto`).
* Interpretations state their source: "Generated from the computed values" or "Written by <model> · numbers verified".
* Saved runs record "Saved from research session <id>, hypothesis <label>" in diagnostics and an audit event.

## 7. Data flow of one evaluation

1. `POST /research/{id}/evaluate` → owner check → state check.
2. Config from the brief + universe → hash → skip if present.
3. `run_portfolio_optimization` → `EvaluateAsync` (stored prices, no save).
4. `run_sensitivity_and_risk_review` → `AnalyzeAsync` on the unsaved run.
5. `HypothesisReview.Flags/Suggestions` (deterministic).
6. `InterpretationService`: template; if a model is available and within budget, narrative → validator → accept or reject.
7. Repeat for the equal-weight baseline; persist; return the whole session.
