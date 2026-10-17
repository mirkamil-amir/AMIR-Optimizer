import {
  Activity, BarChart3, BookOpen, Briefcase, BrainCircuit, Database, Eye, Gauge, GitCompareArrows, GraduationCap, History,
  LayoutDashboard, ListChecks, Newspaper, Search, Settings, ShieldCheck, Sigma, Sparkles, Users,
} from 'lucide-react'

/**
 * The single navigation model of the application shell. Primary destinations form the top bar; the Optimizer
 * workspace's sections and the administration area form the left sidebar. Routes are defined in `main.tsx`; this file
 * only links to them. Visibility here is cosmetic – the API authorizes every request itself.
 */

export interface NavEntry {
  to: string
  label: string
  icon: typeof Search
  /** `to` matches only the exact path (needed for `/`); `match` prefixes still apply. */
  end?: boolean
  /** Further path prefixes that belong to this entry (e.g. detail pages reached from it). */
  match?: string[]
}

export interface NavSection { title: string; items: NavEntry[] }

export type PrimaryId = 'optimizer' | 'research' | 'news' | 'watchlist' | 'market-data' | 'settings'

export interface PrimaryEntry extends NavEntry { id: PrimaryId }

/** Sections of the Optimizer workspace, shown in the left sidebar. */
export const optimizerSections: NavSection[] = [
  {
    title: 'Workspace',
    items: [
      { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
      { to: '/stocks', label: 'Stock selection', icon: Search },
    ],
  },
  {
    title: 'Optimize',
    items: [
      { to: '/optimize', label: 'Optimize', icon: Sigma },
      { to: '/history', label: 'Optimization history', icon: ListChecks, match: ['/reality-check'] },
    ],
  },
  {
    title: 'Test & compare',
    items: [
      { to: '/backtest', label: 'Backtest', icon: Gauge },
      { to: '/strategy-comparison', label: 'Strategy comparison', icon: GitCompareArrows },
      { to: '/machine-learning', label: 'Machine learning', icon: BrainCircuit },
    ],
  },
  {
    title: 'Paper portfolio',
    items: [
      { to: '/holdings', label: 'Holdings', icon: Briefcase },
      { to: '/trades', label: 'Trades & ledger', icon: History },
      { to: '/analytics', label: 'Analytics', icon: BarChart3 },
    ],
  },
  {
    title: 'Learn',
    items: [
      { to: '/learn', label: 'Strategy guide', icon: GraduationCap },
      { to: '/learn/glossary', label: 'Glossary', icon: BookOpen },
    ],
  },
]

/** Administration stays in the sidebar, below the Optimizer sections, for administrators only. */
export const adminSection: NavSection = {
  title: 'Administration',
  items: [
    { to: '/admin', label: 'Admin overview', icon: Activity, end: true },
    { to: '/admin/users', label: 'Users', icon: Users },
    { to: '/admin/settings', label: 'Admin settings', icon: ShieldCheck },
  ],
}

const optimizerEntries = optimizerSections.flatMap((s) => s.items)

/** Top navigation, in this order. Optimizer owns every route of its sidebar sections. */
export const primaryNav: PrimaryEntry[] = [
  { id: 'optimizer', to: '/', label: 'Optimizer', icon: Sigma, end: true, match: optimizerEntries.flatMap((e) => (e.end ? [] : [e.to, ...(e.match ?? [])])) },
  { id: 'research', to: '/research', label: 'AI Research', icon: Sparkles },
  { id: 'news', to: '/news', label: 'Financial News', icon: Newspaper },
  { id: 'watchlist', to: '/watchlist', label: 'Watchlist', icon: Eye },
  { id: 'market-data', to: '/market-data', label: 'Market Data', icon: Database },
  { id: 'settings', to: '/settings', label: 'Settings', icon: Settings },
]

/** Sidebar sections for the current user: Optimizer sections, plus Administration for administrators. */
export function sidebarSections(isAdministrator: boolean): NavSection[] {
  return isAdministrator ? [...optimizerSections, adminSection] : optimizerSections
}

/** Length of the longest prefix of `entry` that matches `pathname`, or -1. */
function matchLength(pathname: string, entry: NavEntry): number {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname
  if (path === entry.to) return entry.to.length
  let best = -1
  for (const prefix of [...(entry.end ? [] : [entry.to]), ...(entry.match ?? [])]) {
    if ((path === prefix || path.startsWith(prefix + '/')) && prefix.length > best) best = prefix.length
  }
  return best
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
