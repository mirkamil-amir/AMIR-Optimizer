# Matrix Pilot – AI-managed experimental portfolio

Matrix Pilot (a portfolio's **Pilot** tab in View, after Pattern) lets a language model manage one **paper** portfolio on its own:
it researches with the platform's tools, chooses holdings and weights, and decides whether to rebalance. The platform
checks every decision against the owner's mandate, books it in the existing ledger (simulated – no real order is ever
sent), records every step and measures the result from the day the pilot is switched on. Decision D-60 makes this the one
exception to "the language model never produces weights or decisions" (D-21); since the research assistant was removed
(D-63) it is the only part of Matrix that uses a language model.

```text
AI = investment decision-maker            Platform = data, calculations, validation, booking, accounting, audit, measurement
```

Related: [threat model](threat-model.md#matrix-pilot) ·
[portfolio accounting](portfolios.md) · [limitations](limitations.md#matrix-pilot) · [decisions D-60](decisions.md).

## 1. What the model decides, and what it cannot

| The model decides | The platform decides and enforces (outside the model) |
|---|---|
| which tools to call and with which arguments (READ / ANALYZE / SIMULATE only) | the tool allow-list, argument checks, per-cycle budget (30 calls), timeouts, result size |
| candidates, holdings, target weights, cash share | the mandate, every limit, schema and data checks (`PilotDecisionValidator`, `AllocationValidator`) |
| which approach to use (optimizer, minimum variance, momentum, ML signals, hybrid, discretionary, hold …) | every number: returns, risk, correlations, optimizer weights, trade estimates (platform formulas) |
| whether to rebalance or hold | whether a decision is executed (only after validation, only while the pilot is active) |
| its thesis, risks, invalidation conditions, holding period, confidence (low/medium/high) | proposal, booking, fees, FX, rounding (`TradingService`, the ledger) |
| requests to change the mandate (recorded, never applied) | the mandate itself (owner only, versioned), pause/resume, kill switch |

The model cannot: write a transaction, change its mandate or permissions, disable validation, touch past records or the
audit trail, read or trade another portfolio, see another user's data, or see any news or description text. It has no
tool that writes data; asking for one is answered with `not_permitted` and journaled as **denied**.

## 2. Components

| Piece | Where | Role |
|---|---|---|
| `MatrixPilot`, `MatrixPilotMandateVersion`, `MatrixPilotCycle`, `MatrixPilotStep`, `MatrixPilotEvent` | `Domain/MatrixPilot.cs` | pilot (one per portfolio), immutable mandate versions, decision cycles, journal entries, lifecycle audit |
| `PilotService` | `Application/Pilot/PilotService.cs` | owner actions (create, enable, kill switch, resume, switch off, mandate, run now) and reads (journal, live record, scorecard) – owner-scoped |
| `PilotEngine` | `Application/Pilot/PilotEngine.cs` | the decision cycle (state machine below), fail-closed |
| `PilotToolRunner` + 13 `PilotTool`s | `Application/Pilot/PilotToolRunner.cs`, `PilotTools.cs` | the model's tools over real services, permissions, budgets, journal |
| `PilotAnalytics` | `Application/Pilot/PilotAnalytics.cs` | point-in-time price series, metrics, risk of an allocation, trade estimate |
| `PilotDecisionValidator` | `Application/Pilot/PilotDecisionValidator.cs` | independent structure + mandate checks (pure, unit-tested) |
| `PilotPrompts` | `Application/Pilot/PilotPrompts.cs` | trusted instructions and JSON schemas (versioned `PilotVersions`) |
| `MatrixPilotWorker`, `ChannelPilotCycleQueue` | `Infrastructure/Jobs/MatrixPilotWorker.cs` | own queue and worker (a cycle waiting for the model never blocks imports or ML), scheduler, crash recovery |
| `BackgroundActor` + `ActorAwareCurrentUser` | `Application/Common`, `Api/Security/Authorization.cs` | the job acts as the pilot's stored owner, so every owner-scoped service applies its normal checks |
| `IPilotLanguageModel` | `Application/Abstractions/ILanguageModel.cs`, `Infrastructure/DependencyInjection.cs` | the pilot's model: `MatrixPilot:Provider`/`Model`, or the shared `Agent:Llm` model |
| `MatrixPilotController` | `Api/Controllers` | `/api/v1/pilots`, `/api/v1/portfolios/{id}/pilot/...` |
| `pages/pilot.tsx` | frontend | Pilot and its sub-sections (the Pilot tab with Performance, Decisions, Mandate, Audit – `PILOT_SECTIONS` in `lib/navigation.ts`); `PilotInArena` in the portfolio's Arena tab |

**Pages** (a portfolio tab in View, after Pattern – D-67; sub-tabs "Pilot sections", left to right):

| Sub-tab | URL | What it shows |
|---|---|---|
| Performance | `/portfolios/:id/pilot/performance` (default of `…/pilot`) | a reporting-currency box (as in View's Overview), at a glance (status, live record, behaviour, fit), live record vs another portfolio, decision scorecard, what the result means |
| Decisions | `/portfolios/:id/pilot/decisions` | the AI decision (proposed vs held, validation, fit), the unconstrained portfolio, every step of the cycle (AI activity) and the decision timeline to pick another cycle |
| Mandate | `/portfolios/:id/pilot/mandate` | the mandate and its versions, the model connection |
| Audit | `/portfolios/:id/pilot/audit` | the audit trail: switch-on, kill switch, pause, resume, mandate changes, cycle outcomes |

View's header names the portfolio and switches it; while the portfolio has a pilot it shows a "Matrix Pilot <STATUS>"
badge on every tab, linking to the Pilot tab – the AI books trades on its own here, unlike Solver and Pattern, which
only propose. Next to the sub-tabs sit the Matrix Pilot status and its controls (Switch on / Run decision now / Kill
switch / Resume / Switch off), so the kill switch is always one click away; without a pilot each sub-tab offers the
setup. Every portfolio's pilot status (status, last and next decision) is a row on its card on **Lens** – there is no
separate status page. The sidebar's **Pilot** and `/pilot`, `/pilot/<sub-tab>` open the current portfolio's Pilot (the
current portfolio if it has a pilot, else the first portfolio with one, else the current one). The reporting currency on
Performance is a view: `GET …/pilot/comparison` and `…/pilot/scorecard` take `?currency=XXX` and convert through the
performance service and `FxService` with dated rates (returns then include currency moves, and the response says so);
costs stay in the portfolio currency. Earlier `/pilot/…` and `/research…` URLs redirect.

## 3. Lifecycle

```text
 (none) ──create──▶ DISABLED ──enable (confirmed)──▶ ACTIVE ◀──resume (confirmed)── PAUSED / KILL SWITCH
                       ▲                               │  │                                   ▲
                       └────────── switch off ─────────┘  ├── kill switch (owner) ───────────┘
                                                          └── drawdown limit / 3 failures / unknown error (system)
```

`PilotStatus` is the lifecycle (Disabled, Active, Paused, KillSwitched). Whether a cycle runs is a separate fact (the
lease `ActiveCycleId`, or a queued cycle); the API shows it as `displayStatus = RUNNING` with the cycle's phase. The
model can never move the pilot between states; a system pause (drawdown, repeated failures, unknown state, archived
portfolio) is resumed only by the owner. Every transition writes a `MatrixPilotEvent` (actor, time, previous and new
status, reason, cycle, correlation id) and an `AuditEvent`.

**Activation** needs `confirm = true` and the provider/model the owner saw in the confirmation dialog (a model changed in
between is refused). The first activation captures the **baseline** (time, value, cash, positions, benchmark level).

**Model check** (`POST /api/v1/pilots/model-check`, "Check model connection" in the UI): one tiny call ("ping", a one-field
schema – no portfolio, account or user data) that reports `ok`, `not_configured`, `key_rejected` (401/403, or Gemini's 400
`API_KEY_INVALID`), `model_not_found`, `rate_limited`, `timeout`, `unavailable` or `provider_error`. Enable and resume run it
on the server and are refused unless it passes; the confirmation dialog runs it on opening and can only be confirmed
after it passed. A key rejected (or a model not found) during a cycle pauses the pilot at once – retrying cannot help.

**Kill switch**: status → KillSwitched; no new cycle starts; a running cycle re-reads the status right before booking and
stops (`SKIPPED – stopped before execution`). Booked trades stay in the ledger.

## 4. Mandate

`PilotMandate` (versioned, `MatrixPilotMandateVersion`): investable universe (index codes + symbols − exclusions),
min/max position weight, min/max holdings, min/max cash, max one-way turnover per decision, minimum trade value,
sector/currency/country limits, optional max ex-ante volatility, drawdown pause threshold, benchmark. Long-only, no
leverage (the platform supports nothing else). `Validate()` rejects inconsistent or infeasible mandates – never repaired.
A change needs confirmation, creates version n+1 with the changed fields (before/after) and the reason, and applies from
the next cycle; each cycle records the mandate version it ran under. The model's `mandateRequests` are shown to the owner
and never applied.

**Form** (set-up and Change mandate, `components/mandate-form.tsx`, `lib/mandate.ts`): only numbers are typed. Indexes are
checkboxes grouped by market (with a filter, at most 10); allowed and excluded stocks are chosen in the Forge stock chooser
(opened in a dialog with a selection of its own, so the user's temporary selection is untouched; at most 500; a stock moved
to one list leaves the other); sector, country and currency limits are checkboxes from the catalogue with a percent field
(default 20 %); the benchmark is a list of the indexes' benchmarks or the portfolio's own; frequency and effort are
toggle buttons.

## 5. Decision cycle (state machine)

```text
QUEUED → OBSERVE → RULES → RESEARCH (≤ 6 rounds, ≤ 6 tools/round, ≤ 30 tools) → CONSTRUCT → FIT (≤ 5 revisions)
       → CRITIQUE → FINALIZE (→ FIT) → VALIDATE → UNCONSTRAINED → COMPARE → EXECUTE → JOURNAL → COMPLETE
```

| Phase | Who | What |
|---|---|---|
| OBSERVE | code | lease; model configured and equal to the pilot's model; mandate; owner-scoped portfolio; universe; prices and FX of holdings and universe at the decision time; **no price dated after the decision date** (else SKIPPED – clock); live-record drawdown (≥ threshold → PAUSED) |
| RULES | model | **first**, before any market data: the model restates every mandate rule (limit, implication) and lists the tensions with the current holdings and the universe's composition (countries, sectors, currencies are given) – `PilotRulesCheck`, first entry of the research log |
| RESEARCH | model ↔ code | the market: the model asks for tools (universe, metrics, correlations, risk, optimizer, ML rankings, earlier decisions …) and tests candidates with `check_allocation` – the same validator as the final check; the runner executes and returns results (trimmed beyond 160k characters) |
| CONSTRUCT | model | structured `PilotDecision` (schema below), stored as the first proposal (`InitialDecisionJson`) |
| FIT | code ↔ model | `PilotFitChecker` validates the proposal (with its risk and trade estimate); every breach goes back to the model with the proposed and the allowed value ("Revision n", `pilot.revise`) until it fits, at most `MaxFitRevisions` (5). A HOLD is checked as "the current portfolio stays" – holding a portfolio that breaks the mandate (e.g. a position above its cap, or all cash) is a breach too |
| CRITIQUE | model (reviewer role) | concerns + `keep` / `revise` / `hold`; sees the fitted decision and the deterministic figures |
| FINALIZE | model | only when the reviewer did not say keep: a final decision with `critiqueResponse` (a named mandate breach must be fixed), then FIT again |
| VALIDATE | code | the final, independent check (same validator): BLOCKED if it still breaks a rule after every revision |
| UNCONSTRAINED | model | the model's own portfolio **without any mandate limit** – any listing of the catalogue, any weight, country, volatility or cash; long-only. Justified per holding and as a whole, with what it does differently. Structure-checked (known listings with prices, weights sum to 1); never traded. A failure here is journaled and does not affect the mandate decision |
| COMPARE | code (+ model text) | both portfolios on the same window and method: volatility, drawdown, VaR/ES, worst 20 days, concentration, beta, trailing 12-month return, country/sector mix, overlap, and which mandate rules the unconstrained one breaks; the model's comparison text passes `NarrativeValidator` or the platform's summary is shown |
| EXECUTE | code | status re-check (kill switch) → `TradingService.CreateForTargetsAsync` → `ExecuteAsync` (idempotency key `pilot:<cycle>`) |
| JOURNAL | code (+ model text) | executed weights next to the proposal; report written by the model only if `NarrativeValidator` accepts it, else the platform's own summary |

Limits enforced outside the model: rounds, tool calls per round and cycle, revisions per decision, 300 s per model call,
600 000 input tokens per cycle, 20 minutes per cycle, three failed cycles in a row → pause. No `while(true)`: every loop
is bounded. A cycle makes about 6–20 model calls (rules, research rounds, decision, revisions, review, final decision,
unconstrained portfolio, comparison, report).

The fit loop changes what is measured (D-61): the first proposal, every breach and every revision stay in the journal
(`InitialDecisionJson`, `FitRevisions`), and the evaluation counts how often the first proposal already fitted – the
model's raw behaviour remains visible although only a fitting portfolio can be booked.

**Decision schema** (`pilot-decision-1`, `PilotPrompts.DecisionSchema`): `action` REBALANCE|HOLD, `strategy` {approach,
components, reason}, `targets` [{symbol, weight, role, thesis, whyNow, risks, invalidation}], `cashWeight`, `rejected`,
`rationale`, `keyRisks`, `invalidationConditions`, `expectedHoldingDays`, `confidence` low|medium|high, `evidenceSteps`
(journal entries relied on), `mandateRequests`, `critiqueResponse`. Missing required fields → FAILED (malformed);
parseable but invalid content → BLOCKED with the violations.

### Outcomes (never shown as one another)

| Outcome | Meaning |
|---|---|
| REBALANCED | validated decision booked (≥ 1 order filled) |
| NO TRADE | the model held, or its target needed no order (drift threshold) – a decision |
| SKIPPED | not attempted: data not ready, future-dated prices, pilot stopped, another cycle holding the portfolio |
| BLOCKED | the decision broke a rule; the proposed values are kept next to the allowed ones |
| FAILED | model/tool/system failure, malformed answer, timeout, all orders rejected at execution – nothing traded |
| PAUSED – drawdown limit | the live record breached the mandate's drawdown threshold; the pilot paused itself |

A tool that returns an error (bad arguments, symbol outside the mandate, too little data) is journaled and its error is
given to the model as data; the cycle continues on the remaining evidence. Failures of the platform's own steps
(observation, the decision's risk and trade calculation, validation, booking) end the cycle without a trade.

## 6. Tools

All tools read owner, portfolio, mandate and decision time from the stored pilot (`PilotToolContext`); none accepts a
user or portfolio id. Every price series ends at the decision date and is converted to the portfolio currency with dated
rates (`FxService`).

| Tool | Category | Backed by |
|---|---|---|
| `get_portfolio_state` | READ | observation (valuation, freshness, live record) |
| `get_mandate` | READ | current mandate version (read-only) |
| `get_investable_universe` | READ | mandate universe with data availability (paged) |
| `get_past_decisions` | READ | earlier cycles of this pilot; moves since each decision measured only up to the current decision date |
| `get_news_signals` | READ | counts of provider-tagged articles by event type/category, published **and retrieved** before the decision – no text |
| `get_existing_optimizations` | READ | this portfolio's Math Optimizations created before the decision |
| `get_ml_signals` | READ | latest rankings of completed ML Optimizations linked to the portfolio, made before the decision |
| `screen_universe` | ANALYZE | trailing returns, volatility, drawdown, return/volatility; scope `mandate` (the universe) or `catalogue` (every listing with recent prices, for the unconstrained portfolio); filters by index, country, sector, currency (≤ 600 screened) |
| `get_security_metrics` | ANALYZE | metrics, beta to the benchmark, 20-day average volume |
| `get_correlations` | ANALYZE | sample correlation matrix (`CovarianceEstimator`) |
| `analyze_portfolio` | ANALYZE | ex-ante volatility, risk shares, diversification, VaR/ES, drawdown, worst periods, beta, exposures (`DiversificationMath`) |
| `run_optimizer` | SIMULATE | `OptimizationService.EvaluateAsync(..., asOf)` with the mandate's caps – not saved |
| `estimate_trades` | SIMULATE | `TradingService.PreviewAsync` – the same calculator as a real proposal, nothing stored |
| `check_allocation` | SIMULATE | `PilotFitChecker` – the mandate validator on a candidate: passed, or every breach with proposed and allowed values |

Not available (and not invented): fundamentals, valuation ratios, earnings, factor exposures, macro series, transaction
cost models beyond the portfolio's bps + minimum fee. See [limitations](limitations.md#matrix-pilot).

## 7. Execution

`targets → TradingService.CreateForTargetsAsync` (same checks as a rebalance preview; the proposal note names the cycle)
→ `TradingService.ExecuteAsync` (atomic, portfolio-version check, sells before buys, cash and fee checks, simulated FX,
idempotency key `pilot:<cycleId>`). Partial fills and rejected orders are recorded per order; the executed weights are
stored next to the AI's proposal (`ExecutedJson`). The portfolio's drift threshold and whole-share setting apply as for
any proposal and show up as differences between proposed and held weights.

## 8. Journal, audit and reproducibility

Each cycle stores: decision time, trigger, correlation id, provider, requested and reported model, effort, prompt/tools/
schema versions, mandate version, token counts, phase timings (data, tools, model, validation, execution, total), the
observation, the decision, the critique, the validation (checks + violations + risk + trade estimate), proposal and batch
ids, executed weights and the report. `MatrixPilotStep` holds every step in order with its category – **AI DECISION**,
**DETERMINISTIC CALCULATION** (tool or platform), **VALIDATION**, **EXECUTION**, **OBSERVED OUTCOME** – arguments,
result as given to the model (≤ 12 000 characters stored; SHA-256 prefix of the full result). No hidden reasoning is stored
or requested: model calls store their structured output only.

Idempotency: one *decision* per pilot and period. Each attempt has a unique key (`D:yyyy-MM-dd`, `W:yyyy-Www`,
`M:yyyy-MM`, retries `…#2`, `…#3`); a period with only FAILED or SKIPPED attempts (nothing decided or traded – e.g. a
provider outage or stale data) stays open: the scheduler tries again after `RetryAfterMinutes` (60) and the owner can
use "Run decision now", up to `PilotCycles.MaxAttemptsPerPeriod` (3) attempts. Once an attempt ends REBALANCED, NO TRADE,
BLOCKED or PAUSED the period is closed – a blocked decision is never retried until it passes. Three failed attempts in a
row also pause the pilot (`MaxConsecutiveFailures`). A cycle runs once (a finished cycle is ignored); the booking has its
own idempotency key. Concurrency: the lease is a conditional
update (`ActiveCycleId` null → this cycle); a second cycle does not start; owner actions and cycles use the pilot's
`Version` token and retry on conflict. On restart, unfinished cycles are recorded as failed and leases released.

## 9. Live record (View / Arena)

`GET /api/v1/portfolios/{id}/pilot/comparison?other=` – forward-only: time-weighted return of the portfolio's ledger from
the baseline date (`PortfolioAnalyticsService.GetPerformanceAsync(from: baseline)`), the other portfolio over the same
dates in the pilot's currency, and the benchmark. Total/annualized return, volatility, Sharpe, Sortino, max drawdown,
trades, fees, cash, holdings, monthly returns, timeline, evaluation counts (decisions by outcome, trades, turnover, cost
drag, tool failures, denied requests, tokens, decision interval, 1-month hit rate). Nothing before the baseline,
nothing back-filled; a backtest of Matrix Pilot does not exist (an LLM cannot be backtested honestly – it may know
the outcome). Shown in the portfolio's Pilot tab (Performance) and in its Arena tab (`PilotInArena`).

The scorecard (`/pilot/scorecard`) lists each decision as recorded at the time with the moves of its holdings and of the
live portfolio 1, 5 and 21 trading days later – measured afterwards, never fed back into that decision.

## 10. Configuration

| Setting | Default | Meaning |
|---|---|---|
| `Agent:Llm:Provider` (`AGENT_LLM_PROVIDER`) | `None` | shared model provider: `None`, `Anthropic`, `Gemini`, `Mock` (tests) |
| `Agent:Llm:Model` (`AGENT_LLM_MODEL`) | provider default | `claude-opus-5-5` / `gemini-3.8-flash` |
| `Agent:Llm:ApiKey` (`LLM_API_KEY`, or `ANTHROPIC_API_KEY`; Gemini also reads `GEMINI_API_KEY`) | empty | server-side only; never logged or sent to the browser (Gemini: `x-goog-api-key` header, never in the URL) |
| `Agent:Llm:TimeoutSeconds` / `MaxRetries` / `Effort` | 60 / 3 / `low` | per request; retries only on 429/5xx |
| `Agent:Llm:RetryBaseSeconds` / `MaxRetryWaitSeconds` | 5 / 60 | wait 5 s, 15 s, 45 s – or what the provider asks (`Retry-After`, Gemini `retryDelay`); a longer requested wait (used-up daily quota) is not waited for |
| `MatrixPilot:Provider` / `Model` | empty | the pilot's own (primary) model: Gemini, Anthropic, Groq, OpenRouter, Mistral, Cerebras, OpenAI, Mock; empty = `Agent:Llm` |
| `MatrixPilot:Fallbacks` (`MATRIX_PILOT_FALLBACKS`) | empty | ordered fallback models `Provider:model; Provider:model` – see [Models](#models-and-fallbacks) |
| `MatrixPilot:Keys:<Provider>` (`GEMINI_API_KEY`, `ANTHROPIC_API_KEY`, `GROQ_API_KEY`, `OPENROUTER_API_KEY`, `MISTRAL_API_KEY`, `CEREBRAS_API_KEY`, `OPENAI_API_KEY`) | empty | one key per provider, sent only to that provider; the shared `LLM_API_KEY` is only ever used for the primary |
| `MatrixPilot:SchedulerEnabled` | true | create due cycles every `SchedulerIntervalMinutes` (15) |
| `MatrixPilot:DecisionHourUtc` | 22 | after the trading day's closes are stored |
| `MaxResearchRounds` / `MaxToolCallsPerRound` | 6 / 6 | model-tool rounds |
| `ModelTimeoutSeconds` / `CycleTimeoutMinutes` | 300 / 20 | |
| `MaxInputTokensPerCycle` / `MaxConsecutiveFailures` | 600 000 / 3 | cost and failure limits |

### Models and fallbacks

Free tiers fail often (2026-11-13 – 11-19: 18 of 20 cycles on Gemini's free tier failed with HTTP 503, 429 or a
cut-off answer). A pilot can therefore name fallback models from other providers (D-68):

* **One model per decision.** A cycle starts with the primary. Only while **no model has answered yet** does a failed
  call (any `LanguageModelException`, after the client's own retries) move the cycle to the next model of the list, which
  then makes every remaining call – research, decision, critique, comparison and report. Once a model has answered, a
  later failure fails the cycle (no trade, retried later as before). A model without a key is skipped. The model that
  decided is recorded on the cycle (`Provider`, `Model`, shown in the Decisions table); each switch and skip is a
  journal step "Model fallback".
* **The owner confirms the list.** The pilot stores its list (`MatrixPilot.FallbackModels`); a configured list that
  differs (other models or another order) fails cycles until the owner adopts it in the mandate (a new version), as for a
  changed primary. Switching on and resuming check that the list the owner saw is the configured one.
* **Model check.** "Check model connection" calls each model once (no portfolio data) and lists the results; switching
  on needs at least one model that answers.
* **Providers.** Gemini (REST), Anthropic (SDK) and, through one OpenAI-compatible client
  (`OpenAiCompatibleLanguageModel`, `POST …/chat/completions`), Groq, OpenRouter, Mistral, Cerebras and OpenAI. The
  JSON schema is sent as `response_format` and in the instructions; a model that rejects schema output is asked once
  for a plain JSON object; a reasoning block or code fence around the answer is removed; the answer is schema-checked
  again by the engine. These providers need an explicit model id. OpenAI gets `max_completion_tokens` (its reasoning
  models reject `max_tokens`); the others get `max_tokens`. Live check: `PO_LIVE_SMOKE=1 OPENAI_API_KEY=… dotnet test
  --filter LiveOpenAiSmokeTests` (model `PO_LIVE_OPENAI_MODEL`, default `gpt-5.4-mini`).
* **Limits.** Free tiers allow far fewer tokens per minute than a full cycle uses (about 280 000 input tokens over
  10–11 calls on 2026-11-14/15), so a fallback can fail on its quota too. Use one account per provider – several free
  keys of one provider to get around its limits break its terms.
* **Gemini thinking.** Thinking tokens count against `maxOutputTokens`; an unbounded budget cut answers off. The budget
  is fixed per effort (low 0, medium 2 048, high 8 192, xhigh/max 16 384) and added on top of the answer's own limit.

Compose: `MATRIX_PILOT_PROVIDER`, `MATRIX_PILOT_MODEL`, `MATRIX_PILOT_FALLBACKS`, the provider keys above,
`MATRIX_PILOT_SCHEDULER`. The e2e stack uses `Mock` (a
deterministic script in `MockPilotScript` – equal weights over the platform's screen, labelled "test script – not an AI"
everywhere) and no scheduler. Cost: a cycle makes 4–10 model calls; on Claude Opus 5.5 at high effort expect roughly
$0.5–2 per cycle (estimate, not measured) – the cycle records its token counts.

## 11. Testing

* Unit (`tests/.../UnitTests/Pilot/PilotUnitTests.cs`): mandate validation and diff, period keys and next decision time,
  decision validator (structure, bounds, sectors, currency/country, holdings, cash, turnover, volatility, minimum trade,
  data), metrics windows, strict argument parsing, background actor.
* Integration (`MatrixPilotTests`, real API + PostgreSQL + optimizer + ledger, scripted model): end-to-end booking and
  journal; hold = NO TRADE; mandate violation BLOCKED with proposed vs allowed; malformed answer; model timeout and
  pause after three failures; no model; stale prices SKIPPED; future-dated prices SKIPPED; tool series end at the decision
  date; same cycle twice and duplicate tick; lease held by another cycle; kill switch (scheduled and mid-cycle) with
  audit; confirmed resume; mandate versions; unlisted tools denied (mandate, trades, validator, permissions, audit);
  news text never reaches the model; tool errors journaled; cross-user isolation (every route, comparison, list);
  comparison with another portfolio; portfolio deletion removes the pilot.
* Fallback models (`MatrixPilotFallbackTests`): the next model takes the whole decision when the primary fails before
  any answer; no switch after an answer; every model failing fails closed; a model without a key is skipped and shown in
  the check; a changed list stops cycles until adopted. Unit: retry waits (`LlmRetry`), Gemini thinking budget and
  quota delay, the OpenAI-compatible client (endpoint, key header, schema fallback, errors, wrapping), list parsing.
* Browser (`e2e/pilot.spec.ts`): set up, confirm (checkbox required), decision cycle, journal and tool trace, refused
  second run, kill switch, audit trail, live record in the Arena tab.

Not covered by automated tests: a real provider (no API key in CI), the drawdown pause end-to-end (needs a real loss
in the live record; the threshold check itself is a one-line comparison), quality of real model decisions – see
[limitations](limitations.md#matrix-pilot).
