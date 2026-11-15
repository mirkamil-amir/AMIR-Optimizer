import { describe, expect, it } from 'vitest'
import {
  adminSection, optimizerSections, PILOT_SECTIONS, pilotPath, primaryForPath, primaryNav, sidebarEntryForPath, sidebarSections,
} from './navigation'

const sidebarPaths = (admin: boolean) => sidebarSections(admin).flatMap((s) => s.items.map((i) => i.to))

describe('navigation model', () => {
  it('has exactly seven primary destinations in the required order (Pilot is a View section, D-67)', () => {
    expect(primaryNav.map((p) => p.label)).toEqual([
      'View', 'Wire', 'Radar', 'Atlas', 'Codex', 'Lexicon', 'Settings',
    ])
    expect(primaryNav.map((p) => p.to)).toEqual(['/', '/news', '/watchlist', '/market-data', '/learn', '/learn/glossary', '/settings'])
  })

  it('does not repeat the moved destinations in the sidebar', () => {
    for (const admin of [false, true]) {
      for (const moved of ['/news', '/watchlist', '/market-data', '/learn', '/learn/glossary', '/settings']) expect(sidebarPaths(admin)).not.toContain(moved)
    }
  })

  it('keeps every other former sidebar destination under Optimizer', () => {
    const optimizer = optimizerSections.flatMap((s) => s.items.map((i) => i.to))
    expect(optimizer.sort()).toEqual([
      '/', '/backtest', '/history', '/ml-optimize', '/optimize', '/pilot', '/stocks', '/strategy-comparison',
    ])
    // Holdings, transactions and performance are sections of each portfolio's workspace, not global sidebar entries.
    expect(optimizerSections.flatMap((s) => s.items.map((i) => i.label))).toContain('Forge')
    expect(optimizerSections.flatMap((s) => s.items.map((i) => i.label))).not.toContain('Stock selection')
    expect(new Set(sidebarPaths(true)).size).toBe(sidebarPaths(true).length)
  })

  it('gives every branded top-bar area its plain name as hint and keeps Settings plain', () => {
    expect(primaryNav.map((p) => [p.label, p.hint?.split(':')[0]])).toEqual([
      ['View', 'Portfolio workspace'], ['Wire', 'Financial News'], ['Radar', 'Watchlist'],
      ['Atlas', 'Market Data'], ['Codex', 'Strategy Guide'], ['Lexicon', 'Glossary'], ['Settings', undefined],
    ])
  })

  it('gives every branded Matrix entry its plain name as hint', () => {
    expect(optimizerSections.flatMap((s) => s.items.map((i) => [i.label, i.hint?.split(':')[0]]))).toEqual([
      ['Lens', 'Portfolios'], ['Forge', 'New portfolio'], ['Solver', 'Math Optimization'], ['Pattern', 'ML Optimization'], ['Pilot', 'Matrix Pilot'],
      ['Trace', 'History'], ['Rewind', 'Backtesting'], ['Arena', 'Comparison'],
    ])
  })

  it('keeps Admin settings in the administration section, for administrators only', () => {
    expect(adminSection.items.map((i) => [i.label, i.to])).toEqual([
      ['Admin overview', '/admin'], ['Users', '/admin/users'], ['Admin settings', '/admin/settings'],
    ])
    expect(sidebarPaths(false).some((p) => p.startsWith('/admin'))).toBe(false)
    expect(sidebarSections(true).at(-1)).toBe(adminSection)
    expect(primaryNav.some((p) => p.to.startsWith('/admin'))).toBe(false)
  })

  it.each([
    ['/', 'optimizer'], ['/stocks', 'optimizer'], ['/stocks/AAPL', 'optimizer'], ['/optimize/12', 'optimizer'],
    ['/history', 'optimizer'], ['/reality-check/7', 'optimizer'], ['/machine-learning/3', 'optimizer'],
    ['/holdings', 'optimizer'], ['/portfolios/p1', 'optimizer'], ['/portfolios/p1/transactions', 'optimizer'], ['/pilot', 'optimizer'], ['/portfolios/p1/pilot/decisions', 'optimizer'], ['/news', 'news'],
    ['/watchlist', 'watchlist'], ['/market-data', 'market-data'], ['/market-data/currencies', 'market-data'], ['/learn', 'strategy-guide'], ['/learn/max-sharpe', 'strategy-guide'],
    ['/learn/glossary', 'glossary'], ['/settings', 'settings'], ['/settings/', 'settings'],
  ])('assigns %s to the %s area', (path, id) => {
    expect(primaryForPath(path)?.id).toBe(id)
  })

  it('assigns no primary area to the administration area or unknown paths', () => {
    for (const path of ['/admin', '/admin/settings', '/no-such-page', '/newsletter']) expect(primaryForPath(path)).toBeUndefined()
  })

  it.each([
    ['/', '/'], ['/stocks/MSFT', '/stocks'], ['/optimize/5', '/optimize'], ['/reality-check/5', '/history'],
    ['/portfolios/p1', '/'], ['/portfolios/p1/holdings', '/'], ['/portfolios/p1/optimize', '/optimize'], ['/portfolios/p1/optimize/r9', '/optimize'],
    ['/portfoliosX', undefined],
    ['/learn', undefined], ['/learn/glossary', undefined], ['/news', undefined],
  ])('highlights the most specific sidebar entry for %s', (path, to) => {
    expect(sidebarEntryForPath(path, false)?.to).toBe(to)
  })

  it('highlights administration entries only for administrators', () => {
    expect(sidebarEntryForPath('/admin/settings', true)?.label).toBe('Admin settings')
    expect(sidebarEntryForPath('/admin', true)?.label).toBe('Admin overview')
    expect(sidebarEntryForPath('/admin/settings', false)).toBeUndefined()
  })
})

describe('Pilot in View (D-67)', () => {
  it('sits right after Pattern in the sidebar and leads to the current portfolio', () => {
    const optimize = optimizerSections.find((s) => s.title === 'Optimize')!.items.map((i) => i.label)
    expect(optimize).toEqual(['Solver', 'Pattern', 'Pilot', 'Trace'])
    expect(sidebarEntryForPath('/portfolios/p1/pilot/audit', false)?.label).toBe('Pilot')
  })

  it('has the sub-tabs Performance, Decisions, Mandate and Audit inside the portfolio', () => {
    expect(PILOT_SECTIONS.map((s) => s.label)).toEqual(['Performance', 'Decisions', 'Mandate', 'Audit'])
    expect(PILOT_SECTIONS.map((s) => pilotPath(s.path, 'p1'))).toEqual([
      '/portfolios/p1/pilot/performance', '/portfolios/p1/pilot/decisions', '/portfolios/p1/pilot/mandate', '/portfolios/p1/pilot/audit',
    ])
    // Without a portfolio in the URL the sub-tab opens the current portfolio (resolved by the page, like View).
    expect(pilotPath('decisions')).toBe('/pilot/decisions')
  })
})
