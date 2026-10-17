import { describe, expect, it } from 'vitest'
import {
  adminSection, optimizerSections, primaryForPath, primaryNav, sidebarEntryForPath, sidebarSections,
} from './navigation'

const sidebarPaths = (admin: boolean) => sidebarSections(admin).flatMap((s) => s.items.map((i) => i.to))

describe('navigation model', () => {
  it('has exactly six primary destinations in the required order', () => {
    expect(primaryNav.map((p) => p.label)).toEqual(['Optimizer', 'AI Research', 'Financial News', 'Watchlist', 'Market Data', 'Settings'])
    expect(primaryNav.map((p) => p.to)).toEqual(['/', '/research', '/news', '/watchlist', '/market-data', '/settings'])
  })

  it('does not repeat the moved destinations in the sidebar', () => {
    for (const admin of [false, true]) {
      for (const moved of ['/research', '/news', '/watchlist', '/market-data', '/settings']) expect(sidebarPaths(admin)).not.toContain(moved)
    }
  })

  it('keeps every other former sidebar destination under Optimizer', () => {
    const optimizer = optimizerSections.flatMap((s) => s.items.map((i) => i.to))
    expect(optimizer.sort()).toEqual([
      '/', '/analytics', '/backtest', '/history', '/holdings', '/learn', '/learn/glossary', '/machine-learning', '/optimize',
      '/stocks', '/strategy-comparison', '/trades',
    ])
    expect(new Set(sidebarPaths(true)).size).toBe(sidebarPaths(true).length)
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
    ['/history', 'optimizer'], ['/reality-check/7', 'optimizer'], ['/machine-learning/3', 'optimizer'], ['/learn/glossary', 'optimizer'],
    ['/holdings', 'optimizer'], ['/research', 'research'], ['/research/abc', 'research'], ['/news', 'news'],
    ['/watchlist', 'watchlist'], ['/market-data', 'market-data'], ['/settings', 'settings'], ['/settings/', 'settings'],
  ])('assigns %s to the %s area', (path, id) => {
    expect(primaryForPath(path)?.id).toBe(id)
  })

  it('assigns no primary area to the administration area or unknown paths', () => {
    for (const path of ['/admin', '/admin/settings', '/no-such-page', '/newsletter']) expect(primaryForPath(path)).toBeUndefined()
  })

  it.each([
    ['/', '/'], ['/stocks/MSFT', '/stocks'], ['/optimize/5', '/optimize'], ['/reality-check/5', '/history'],
    ['/learn', '/learn'], ['/learn/max-sharpe', '/learn'], ['/learn/glossary', '/learn/glossary'], ['/news', undefined],
  ])('highlights the most specific sidebar entry for %s', (path, to) => {
    expect(sidebarEntryForPath(path, false)?.to).toBe(to)
  })

  it('highlights administration entries only for administrators', () => {
    expect(sidebarEntryForPath('/admin/settings', true)?.label).toBe('Admin settings')
    expect(sidebarEntryForPath('/admin', true)?.label).toBe('Admin overview')
    expect(sidebarEntryForPath('/admin/settings', false)).toBeUndefined()
  })
})
