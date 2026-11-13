# Matrix Pilot – AI-managed experimental portfolio

Matrix Pilot (AI Probe › Matrix Pilot, `/research/pilot`) lets a language model manage one **paper** portfolio on its own:
it researches with the platform's tools, chooses holdings and weights, and decides whether to rebalance. The platform
checks every decision against the owner's mandate, books it in the existing ledger (simulated – no real order is ever
sent), records every step and measures the result from the day the pilot is switched on. Decision D-60 makes this the one
exception to "the language model never produces weights or decisions" (D-21); the research assistant is unchanged.

```text
AI = investment decision-maker            Platform = data, calculations, validation, booking, accounting, audit, measurement
```

Related: [agentic architecture](agentic-architecture.md) (research assistant) · [threat model](threat-model.md#matrix-pilot) ·
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
| `pages/pilot.tsx` | frontend | AI Probe › Matrix Pilot list and control room; `PilotInArena` in the portfolio's Arena tab |

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
the outcome). Shown in AI Probe › Matrix Pilot and in the portfolio's Arena tab (`PilotInArena`).

The scorecard (`/pilot/scorecard`) lists each decision as recorded at the time with the moves of its holdings and of the
live portfolio 1, 5 and 21 trading days later – measured afterwards, never fed back into that decision.

## 10. Configuration

| Setting | Default | Meaning |
|---|---|---|
| `MatrixPilot:Provider` / `Model` | empty | the pilot's own model (Anthropic, Gemini, Mock, None); empty = `Agent:Llm` |
| `MatrixPilot:SchedulerEnabled` | true | create due cycles every `SchedulerIntervalMinutes` (15) |
| `MatrixPilot:DecisionHourUtc` | 22 | after the trading day's closes are stored |
| `MaxResearchRounds` / `MaxToolCallsPerRound` | 6 / 6 | model-tool rounds |
| `ModelTimeoutSeconds` / `CycleTimeoutMinutes` | 300 / 20 | |
| `MaxInputTokensPerCycle` / `MaxConsecutiveFailures` | 600 000 / 3 | cost and failure limits |

Compose: `MATRIX_PILOT_PROVIDER`, `MATRIX_PILOT_MODEL`, `MATRIX_PILOT_SCHEDULER`. The e2e stack uses `Mock` (a
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
* Browser (`e2e/pilot.spec.ts`): set up, confirm (checkbox required), decision cycle, journal and tool trace, refused
  second run, kill switch, audit trail, live record in the Arena tab.

Not covered by automated tests: a real provider (no API key in CI), the drawdown pause end-to-end (needs a real loss
in the live record; the threshold check itself is a one-line comparison), quality of real model decisions – see
[limitations](limitations.md#matrix-pilot).
