import { act, renderHook } from '@testing-library/react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { SelectionProvider, useSelection } from './selection'

const wrapper = ({ children }: { children: ReactNode }) => <SelectionProvider>{children}</SelectionProvider>
const stock = (symbol: string, sector = 'Technology') => ({ symbol, name: `${symbol} Inc.`, sector })

describe('temporary stock selection', () => {
  it('adds, toggles and removes stocks, sorted by symbol without duplicates', () => {
    const { result } = renderHook(() => useSelection(), { wrapper })
    act(() => result.current.add(stock('MSFT')))
    act(() => result.current.addMany([stock('AAPL'), stock('MSFT'), stock('XOM', 'Energy')]))
    expect(result.current.symbols).toEqual(['AAPL', 'MSFT', 'XOM'])
    expect(result.current.count).toBe(3)

    act(() => result.current.toggle(stock('AAPL')))
    expect(result.current.has('AAPL')).toBe(false)
    act(() => result.current.toggle(stock('AAPL')))
    expect(result.current.has('AAPL')).toBe(true)

    act(() => result.current.remove('XOM'))
    expect(result.current.symbols).toEqual(['AAPL', 'MSFT'])
    act(() => result.current.clear())
    expect(result.current.count).toBe(0)
  })

  it('keeps the selection in memory only (no browser storage, no network)', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem')
    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)
    const { result } = renderHook(() => useSelection(), { wrapper })
    act(() => result.current.addMany([stock('AAPL'), stock('MSFT')]))
    expect(setItem).not.toHaveBeenCalled()
    expect(fetchSpy).not.toHaveBeenCalled()
    expect(document.cookie).not.toContain('AAPL')
    vi.unstubAllGlobals()
  })

  it('is lost when the provider remounts (e.g. a page reload)', () => {
    const first = renderHook(() => useSelection(), { wrapper })
    act(() => first.result.current.add(stock('AAPL')))
    first.unmount()
    const second = renderHook(() => useSelection(), { wrapper })
    expect(second.result.current.count).toBe(0)
  })

  it('throws outside its provider', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    expect(() => renderHook(() => useSelection())).toThrow(/SelectionProvider/)
  })
})
