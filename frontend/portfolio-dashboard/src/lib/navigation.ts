/**
 * The single navigation model of the application shell. Primary destinations form the top bar; the Optimizer
 * workspace's sections and the administration area form the left sidebar. Routes are defined in `main.tsx`; this file
 * only links to them. Visibility here is cosmetic – the API authorizes every request itself.
 */

export interface NavEntry {
  to: string
  label: string
  /** `to` matches only the exact path (needed for `/`); `match` prefixes still apply. */
  end?: boolean
  /** Further path prefixes that belong to this entry (e.g. detail pages reached from it); `*` matches one path segment. */
  match?: string[]
  /** The everyday name and what the page does, shown on hover/focus and given to screen readers as the description. */
  hint?: string
}

export interface NavSection { title: string; items: NavEntry[] }

export type PrimaryId = 'optimizer' | 'research' | 'news' | 'watchlist' | 'market-data' | 'strategy-guide' | 'glossary' | 'settings'

export interface PrimaryEntry extends NavEntry { id: PrimaryId }

/**
 * Names of the Matrix's sections, shared by the sidebar and a portfolio's tab bar so one page has one name everywhere.
 * The names follow the brand; `hint` keeps the plain term (owner's choice, 2026-10-27).
 */
export const SECTION_NAMES = {
  view: { label: 'Lens', hint: 'Portfolios: all of your portfolios at a glance' },
  new: { label: 'Forge', hint: 'New portfolio: choose stocks and build a portfolio from them' },
  overview: { label: 'Core', hint: 'Overview: value, performance, holdings and positions of this portfolio' },
  math: { label: 'Solver', hint: 'Math Optimization: weights from a mean-variance optimizer' },
  ml: { label: 'Pattern', hint: 'ML Optimization: weights from a model that ranks stocks by patterns in past prices' },
  history: { label: 'Trace', hint: 'History: every Math and ML Optimization run so far' },
  backtest: { label: 'Rewind', hint: 'Backtesting: replay an optimization on past prices' },
  comparison: { label: 'Arena', hint: 'Comparison: several optimizations side by side over the same period' },
  transactions: { label: 'Ledger', hint: 'Transactions: recorded bookings, proposals, executions, orders and cash' },
} as const

/** Sections of the Optimizer workspace, shown in the left sidebar. */
export const optimizerSections: NavSection[] = [
  {
    title: 'Portfolios',
    items: [
      // The overview lists all of the user's portfolios; each portfolio's workspace (/portfolios/:id/…) belongs to it.
      { to: '/', ...SECTION_NAMES.view, end: true, match: ['/portfolios'] },
      { to: '/stocks', ...SECTION_NAMES.new },
    ],
  },
  {
    title: 'Optimize',
    items: [
      // Opens the optimizer of the most recently opened portfolio; inside a workspace its own optimizer is highlighted.
      { to: '/optimize', ...SECTION_NAMES.math, match: ['/portfolios/*/optimize'] },
      // Like Solver: the ML Optimization of the current portfolio (earlier /machine-learning URLs redirect).
      { to: '/ml-optimize', ...SECTION_NAMES.ml, match: ['/portfolios/*/ml-optimize', '/machine-learning'] },
      { to: '/history', ...SECTION_NAMES.history, match: ['/reality-check', '/portfolios/*/runs'] },
    ],
  },
  {
    title: 'Test & compare',
    items: [
      // All of the user's backtests and comparisons; a portfolio's own tabs (/portfolios/:id/backtest, …/comparison) belong here too.
      { to: '/backtest', ...SECTION_NAMES.backtest, match: ['/portfolios/*/backtest'] },
      { to: '/strategy-comparison', ...SECTION_NAMES.comparison, match: ['/portfolios/*/comparison'] },
    ],
  },
]

/** Administration stays in the sidebar, below the Matrix sections, for administrators only. */
export const adminSection: NavSection = {
  title: 'Administration',
  items: [
    { to: '/admin', label: 'Admin overview', end: true },
    { to: '/admin/users', label: 'Users' },
    { to: '/admin/settings', label: 'Admin settings' },
  ],
}

const optimizerEntries = optimizerSections.flatMap((s) => s.items)

/** Top navigation, in this order (branded like the sidebar, D-47; Settings stays plain). Optimizer owns every route of its sidebar sections. */
export const primaryNav: PrimaryEntry[] = [
  {
    id: 'optimizer', to: '/', label: 'Matrix', hint: 'Portfolio workspace: build, optimize, backtest and compare your portfolios', end: true,
    // Earlier portfolio-less URLs (/holdings, /trades, /analytics) redirect into a portfolio workspace.
    match: [...optimizerEntries.flatMap((e) => [...(e.end ? [] : [e.to]), ...(e.match ?? [])]), '/holdings', '/trades', '/analytics'],
  },
  { id: 'research', to: '/research', label: 'AI Probe', hint: 'AI Research: an assistant turns your question into a brief and runs the real optimizer on it' },
  { id: 'news', to: '/news', label: 'Wire', hint: 'Financial News: articles from news providers, linked to stocks by the providers\' own tags' },
  { id: 'watchlist', to: '/watchlist', label: 'Radar', hint: 'Watchlist: stocks you follow without holding them' },
  { id: 'market-data', to: '/market-data', label: 'Atlas', hint: 'Market Data: stocks, ETFs, indexes and exchange rates of 35 markets' },
  // Strategy pages (/learn/:strategyId) belong to the guide; the longer /learn/glossary wins for the glossary.
  { id: 'strategy-guide', to: '/learn', label: 'Codex', hint: 'Strategy Guide: how each optimization strategy and model works' },
  { id: 'glossary', to: '/learn/glossary', label: 'Lexicon', hint: 'Glossary: the terms used in the app, explained' },
  { id: 'settings', to: '/settings', label: 'Settings' },
]

/** Sidebar sections for the current user: Matrix sections, plus Administration for administrators. */
export function sidebarSections(isAdministrator: boolean): NavSection[] {
  return isAdministrator ? [...optimizerSections, adminSection] : optimizerSections
}

/** Length of the longest prefix of `entry` that matches `pathname`, or -1. */
function matchLength(pathname: string, entry: NavEntry): number {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname
  if (path === entry.to) return entry.to.length
  let best = -1
  for (const prefix of [...(entry.end ? [] : [entry.to]), ...(entry.match ?? [])]) {
    if (prefixMatches(path, prefix) && prefix.length > best) best = prefix.length
  }
  return best
}

/** Whether `prefix` (segments, `*` = any one segment) is the start of `path` at a segment boundary. */
function prefixMatches(path: string, prefix: string): boolean {
  const p = path.split('/')
  const q = prefix.split('/')
  if (q.length > p.length) return false
  return q.every((seg, i) => seg === '*' ? p[i] !== '' : seg === p[i])
}

/** The entry that owns `pathname` (most specific match wins), or undefined. */
export function activeEntry<T extends NavEntry>(pathname: string, entries: T[]): T | undefined {
  let found: T | undefined
  let best = -1
  for (const entry of entries) {
    const n = matchLength(pathname, entry)
    if (n > best) { best = n; found = entry }
  }
  return found
}

/** The top-level area of `pathname`; undefined for the administration area and unknown paths. */
export function primaryForPath(pathname: string): PrimaryEntry | undefined {
  return activeEntry(pathname, primaryNav)
}

/** The highlighted sidebar entry for `pathname`. */
export function sidebarEntryForPath(pathname: string, isAdministrator: boolean): NavEntry | undefined {
  return activeEntry(pathname, sidebarSections(isAdministrator).flatMap((s) => s.items))
}
