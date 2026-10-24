import { useEffect } from 'react'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { CAMERA_DEBOUNCE_MS, cameraFor, frameFor, WORLD, type Camera } from '@/lib/map-camera'
import { SelectionProvider, useSelection } from '@/lib/selection'
import { useMapHighlight, useMapSignal, WorldMapProvider } from '@/lib/world-map'
import { MapControl, SelectionOnMap, WorldMapBackdrop } from './world-map'

vi.mock('./world-map-geo', () => ({ COUNTRY_PATHS: { '840': 'M0 0L1 1Z', '276': 'M2 2L3 3Z', '392': 'M4 4L5 5Z' } }))

const W = 1200
const H = 600
let size = { width: W, height: H }
let reducedMotion = false
const observers: { callback: () => void; disconnect: ReturnType<typeof vi.fn> }[] = []

class FakeResizeObserver {
  disconnect = vi.fn()
  callback: () => void
  constructor(callback: () => void) {
    this.callback = callback
    observers.push(this)
  }
  observe() {}
  unobserve() {}
}

beforeEach(() => {
  size = { width: W, height: H }
  reducedMotion = false
  observers.length = 0
  vi.stubGlobal('ResizeObserver', FakeResizeObserver)
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({ ...size, x: 0, y: 0, top: 0, left: 0, right: size.width, bottom: size.height, toJSON: () => ({}) }))
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('reduce') && reducedMotion, media: query, addEventListener() {}, removeEventListener() {} }))
  vi.useFakeTimers({ shouldAdvanceTime: true })
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

const actions: {
  add: (s: string) => void; remove: (s: string) => void; clear: () => void; signal: ReturnType<typeof useMapSignal>
} = { add: () => {}, remove: () => {}, clear: () => {}, signal: () => {} }
function Controls() {
  const selection = useSelection()
  const signal = useMapSignal()
  useEffect(() => {
    actions.add = (symbol) => selection.add({ symbol, name: null, sector: null })
    actions.remove = selection.remove
    actions.clear = selection.clear
    actions.signal = signal
  })
  return null
}
function Page({ countries }: { countries: string[] }) {
  useMapHighlight('page', countries)
  return null
}

function renderMap(countries: string[] = []) {
  const ui = (c: string[]) => (
    <TooltipProvider>
      <SelectionProvider>
        <WorldMapProvider>
          <SelectionOnMap />
          <Page countries={c} />
          <Controls />
          <MapControl />
          <WorldMapBackdrop />
        </WorldMapProvider>
      </SelectionProvider>
    </TooltipProvider>
  )
  const result = render(ui(countries))
  return { ...result, setPage: (c: string[]) => result.rerender(ui(c)) }
}

const map = () => screen.getByTestId('world-map')
const city = (country: string) => map().querySelector(`[data-country="${country}"]`)!
const camera = () => map().querySelector<HTMLElement>('.world-map-camera')!
const transformOf = (c: Camera) => `translate(${c.tx}px, ${c.ty}px) scale(${c.k})`
const frame = (...countries: string[]) => transformOf(cameraFor(frameFor(countries), W, H))
const settle = () => act(() => { vi.advanceTimersByTime(CAMERA_DEBOUNCE_MS + 10) })

describe('background world map', () => {
  it('is decorative, highlights the markets of the page and frames them after a short pause', async () => {
    renderMap(['US', 'DE'])
    expect(map()).toHaveAttribute('aria-hidden', 'true')
    expect(city('US')).toHaveAttribute('data-state', 'lit')
    expect(city('JP')).toHaveAttribute('data-state', 'idle')
    expect(screen.getByText('Frankfurt')).toBeInTheDocument()
    expect(camera().style.transform).toBe(transformOf(WORLD))
    await settle()
    expect(camera().style.transform).toBe(frame('US', 'DE'))
    expect(Number.parseInt(camera().style.transitionDuration)).toBeGreaterThan(0)
    // Country outlines load once, lazily; the highlighted ones are marked.
    expect(await vi.waitFor(() => map().querySelector('[data-iso="276"]'))).toHaveClass('world-map-country-lit')
    expect(map().querySelector('[data-iso="392"]')).not.toHaveClass('world-map-country-lit')
  })

  it('selecting a stock ripples, focuses and frames its market; removing and clearing restore the view', async () => {
    renderMap()
    await settle()
    act(() => actions.add('SAP.DE'))
    expect(city('DE')).toHaveAttribute('data-state', 'pulse')
    await settle()
    expect(camera().style.transform).toBe(frame('DE'))
    act(() => { vi.advanceTimersByTime(1500) })
    expect(city('DE')).toHaveAttribute('data-state', 'focus')

    act(() => actions.add('7203.T'))
    await settle()
    expect(camera().style.transform).toBe(frame('DE', 'JP'))
    expect(map()).toHaveAttribute('data-targets', 'DE,JP')

    act(() => actions.remove('7203.T'))
    await settle()
    expect(camera().style.transform).toBe(frame('DE'))
    act(() => actions.clear())
    await settle()
    expect(camera().style.transform).toBe(transformOf(WORLD))
    expect(city('DE')).toHaveAttribute('data-state', 'idle')
  })

  it('combines rapid selections into one move to the latest state (no jitter between countries)', async () => {
    renderMap()
    await settle()
    const el = camera()
    act(() => actions.add('AAPL'))
    act(() => { vi.advanceTimersByTime(50) })
    act(() => actions.add('SAP.DE'))
    act(() => { vi.advanceTimersByTime(50) })
    act(() => actions.add('7203.T'))
    expect(el.style.transform).toBe(transformOf(WORLD)) // nothing moved yet
    await settle()
    expect(el.style.transform).toBe(frame('US', 'DE', 'JP'))
  })

  it('an interrupting selection supersedes the running move', async () => {
    renderMap(['DE'])
    await settle()
    act(() => actions.add('BHP.AX'))
    await settle()
    // The CSS transition simply retargets from wherever the map is now.
    expect(camera().style.transform).toBe(frame('DE', 'AU'))
    expect(Number.parseInt(camera().style.transitionDuration)).toBeGreaterThan(0)
  })

  it('an opened stock blinks and joins the frame without being selected', async () => {
    renderMap(['US'])
    await settle()
    act(() => actions.signal('IN', 'blink'))
    expect(city('IN')).toHaveAttribute('data-state', 'blink')
    expect(screen.getByText('Mumbai')).toBeInTheDocument()
    await settle()
    expect(camera().style.transform).toBe(frame('US', 'IN'))
    act(() => actions.signal(null, 'blink')) // an unresolved market is ignored
    await settle()
    expect(camera().style.transform).toBe(frame('US', 'IN'))
  })

  it('respects reduced motion: the camera changes immediately', async () => {
    reducedMotion = true
    renderMap(['JP'])
    await settle()
    expect(camera().style.transform).toBe(frame('JP'))
    expect(camera().style.transitionDuration).toBe('0ms')
  })

  it('manual "World view" pauses automatic framing until the selection changes; "Fit selected markets" resumes it', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    renderMap(['US', 'DE'])
    await settle()
    await user.click(screen.getByRole('button', { name: /^Market map: 2 markets in view/ }))
    expect(screen.getByRole('list', { name: 'Markets in view' })).toHaveTextContent('Germany · XETRA, Frankfurt')
    await user.click(screen.getByRole('button', { name: 'World view' }))
    await settle()
    expect(camera().style.transform).toBe(transformOf(WORLD))
    await user.click(screen.getByRole('button', { name: 'Show Germany on the map' }))
    await settle()
    expect(camera().style.transform).toBe(frame('DE'))
    await user.click(screen.getByRole('button', { name: 'Fit selected markets' }))
    await settle()
    expect(camera().style.transform).toBe(frame('US', 'DE'))

    await user.click(screen.getByRole('button', { name: 'World view' }))
    await settle()
    act(() => actions.add('7203.T')) // a meaningful change returns to automatic framing
    await settle()
    expect(camera().style.transform).toBe(frame('US', 'DE', 'JP'))
  })

  it('switching the page’s portfolio re-frames from its markets', async () => {
    const { setPage } = renderMap(['US'])
    await settle()
    expect(camera().style.transform).toBe(frame('US'))
    setPage(['GB', 'FR'])
    await settle()
    expect(camera().style.transform).toBe(frame('GB', 'FR'))
    expect(city('US')).toHaveAttribute('data-state', 'idle')
  })

  it('re-frames without animation when the map area is resized, and cleans up on unmount', async () => {
    const { unmount } = renderMap(['DE'])
    await settle()
    size = { width: 600, height: 800 }
    act(() => observers[0].callback())
    expect(camera().style.transform).toBe(transformOf(cameraFor(frameFor(['DE']), 600, 800)))
    expect(camera().style.transitionDuration).toBe('0ms')
    unmount()
    expect(observers[0].disconnect).toHaveBeenCalled()
    act(() => { vi.runOnlyPendingTimers() })
    expect(vi.getTimerCount()).toBe(0)
  })
})
