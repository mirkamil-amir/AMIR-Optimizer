import type { Hypothesis, ResearchBrief, ResearchCandidate, ResearchEvent } from './types'

export const objectiveNames: Record<string, string> = {
  MinimumVariance: 'Minimum variance',
  MaximumSharpe: 'Maximum Sharpe ratio',
  EqualWeight: 'Equal weight',
}

export const marketNames: Record<string, string> = {
  US: 'United States (S&P 500)', DE: 'Germany (DAX 40)', FR: 'France (CAC 40)', GB: 'United Kingdom (FTSE 100)', JP: 'Japan (Nikkei 225)',
  CH: 'Switzerland (SMI)', ES: 'Spain (IBEX 35)', IT: 'Italy (FTSE MIB)', SE: 'Sweden (OMX Stockholm 30)', DK: 'Denmark (OMX Copenhagen 25)',
  CA: 'Canada (S&P/TSX 60)', AU: 'Australia (S&P/ASX 50)', HK: 'Hong Kong (Hang Seng Index)', IN: 'India (NIFTY 50)',
  SG: 'Singapore (Straits Times Index)',
  NO: 'Norway (OBX)', FI: 'Finland (OMX Helsinki 25)', BE: 'Belgium (BEL 20)', PT: 'Portugal (PSI)', IE: 'Ireland (ISEQ 20)',
  AT: 'Austria (ATX)', PL: 'Poland (WIG20)', HU: 'Hungary (BUX)', GR: 'Greece (FTSE/Athex Large Cap)', IS: 'Iceland (OMX Iceland 15)',
  IL: 'Israel (TA-35)', KR: 'South Korea (KOSPI 200)', CN: 'China (SSE 50)', MY: 'Malaysia (FTSE Bursa Malaysia KLCI)',
  TH: 'Thailand (SET50)', BR: 'Brazil (Ibovespa)', ID: 'Indonesia (LQ45)', MX: 'Mexico (S&P/BMV IPC)',
  ZA: 'South Africa (FTSE/JSE Top 40)', NZ: 'New Zealand (S&P/NZX 50)',
}

export const sectors = [
  'Information Technology', 'Health Care', 'Financials', 'Consumer Discretionary', 'Communication Services', 'Industrials',
  'Consumer Staples', 'Energy', 'Utilities', 'Real Estate', 'Materials',
]

export const toolNames: Record<string, string> = {
  search_investable_universe: 'Researched the investable universe',
  run_portfolio_optimization: 'Ran the portfolio optimizer',
  run_sensitivity_and_risk_review: 'Reviewed risk and sensitivity',
  get_company_context: 'Looked up company context',
  save_research_run: 'Saved a research run',
  research_brief: 'Built the research brief',
  interpret_result: 'Explained a result',
  interpret_refinement: 'Interpreted your request',
  objective: 'Your objective',
  edit_brief: 'You edited the brief',
  choose_universe: 'You chose the universe',
  refinement_request: 'Your request',
  confirm_refinement: 'You confirmed a change',
  discard_refinement: 'You discarded a change',
  next_step: 'Next step',
}

/** Human-readable brief rows with whether the value was stated by the user or assumed by the system (pure). */
export function briefRows(b: ResearchBrief): { key: string; label: string; value: string; origin: 'user' | 'assumption' | null; reason: string | null }[] {
  const o = (k: string) => b.origins[k]
  const row = (key: string, label: string, value: string) => ({ key, label, value, origin: o(key)?.source ?? null, reason: o(key)?.reason ?? null })
  return [
    row('market', 'Market', b.market ? marketNames[b.market] ?? b.market : 'Not chosen yet'),
    row('riskPreference', 'Willingness to accept fluctuations', b.riskPreference),
    row('lossCapacity', 'Ability to bear losses', b.lossCapacity ?? 'not stated'),
    row('horizonYears', 'Horizon', b.horizonYears ? `${b.horizonYears} years` : 'not stated'),
    row('objective', 'Method', objectiveNames[b.objective] ?? b.objective),
    row('maxWeight', 'Per-stock maximum', `${Math.round(b.maxWeight * 100)} %`),
    row('excludeSectors', 'Excluded sectors', b.excludeSectors.length ? b.excludeSectors.join(', ') : 'none'),
    row('candidateCount', 'Shortlist size', String(b.candidateCount)),
    row('lookbackDays', 'History window', `${b.lookbackDays} trading days`),
  ]
}

/** Candidates the user may put into the universe: screened stocks that meet the data requirements. */
export const isEligible = (c: ResearchCandidate) => c.included || c.reason.startsWith('Meets')

/** The most useful label for where a hypothesis came from. */
export function originLabel(h: Hypothesis): string {
  if (h.origin === 'initial') return 'From your brief'
  if (h.origin === 'baseline') return 'Baseline'
  return h.origin.startsWith('refinement') ? h.origin.replace(/^refinement of /, 'Refined: ') : h.origin
}

export function eventLabel(e: ResearchEvent): string {
  return toolNames[e.name] ?? e.name
}

/** Max turnover of the assumption-change scenarios (null when none solved). */
export function maxScenarioTurnover(h: Hypothesis): number | null {
  const t = (h.review?.scenarios ?? []).filter((s) => s.key !== 'resolved' && s.key !== 'equal' && s.turnover !== null).map((s) => s.turnover!)
  return t.length ? Math.max(...t) : null
}
