import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { venues } from './markets'

/** A one-off animation of a city marker: `pulse` = one ripple (a stock was selected), `blink` = flashes (a stock was opened). */
export interface MapSignal {
  id: number
  country: string
  kind: 'pulse' | 'blink'
}

/**
 * How the camera is chosen. `auto`: frame the highlighted markets (and the focused one); `world`: the whole world;
 * a country code: that market's region. A manual choice lasts until the highlighted markets or the focus change.
 */
export type MapView = 'auto' | 'world' | string

interface WorldMapContextValue {
  /** Markets highlighted by the pages currently shown (union of every registered source), in venue order. */
  highlighted: string[]
  /** The market of the stock selected or opened last; shown distinctly and always included in the frame. */
  focus: string | null
  signals: MapSignal[]
  view: MapView
  setView: (view: MapView) => void
  setSource: (key: string, countries: string[] | null) => void
  signal: (country: string, kind: MapSignal['kind']) => void
}

const WorldMapContext = createContext<WorldMapContextValue | null>(null)

const SIGNAL_MS = { pulse: 1400, blink: 2400 }

/**
 * State of the background world map: which markets are selected (from the pages' sources), which one is focused, and
 * the viewing mode. Pages register the markets they are about (`useMapHighlight`), stock interactions send short signals
 * (`useMapSignal`). Camera positions are not kept here – they are transient and live in the map component – so a moving
 * camera never re-renders pages. Nothing here is stored or sent anywhere.
 */
export function WorldMapProvider({ children }: { children: ReactNode }) {
  const [sources, setSources] = useState<Record<string, string[]>>({})
  const [signals, setSignals] = useState<MapSignal[]>([])
  const [focus, setFocus] = useState<string | null>(null)
  const [view, setView] = useState<MapView>('auto')
  const nextId = useRef(1)
  const timers = useRef(new Set<number>())

  const setSource = useCallback((key: string, countries: string[] | null) => setSources((current) => {
    if (countries === null) {
      if (!(key in current)) return current
      const next = { ...current }
      delete next[key]
      return next
    }
    const previous = current[key]
    if (previous && previous.length === countries.length && previous.every((c, i) => c === countries[i])) return current
    return { ...current, [key]: countries }
  }), [])

  const signal = useCallback((country: string, kind: MapSignal['kind']) => {
    const id = nextId.current++
    // A new signal for a city replaces the running one, so repeated clicks restart the animation.
    setSignals((s) => [...s.filter((x) => x.country !== country), { id, country, kind }])
    setFocus(country)
    setView('auto')
    const timer = window.setTimeout(() => {
      timers.current.delete(timer)
      setSignals((s) => s.filter((x) => x.id !== id))
    }, SIGNAL_MS[kind])
    timers.current.add(timer)
  }, [])

  useEffect(() => {
    const pending = timers.current
    return () => { for (const t of pending) window.clearTimeout(t) }
  }, [])

  const highlighted = useMemo(() => {
    const set = new Set(Object.values(sources).flat())
    return venues.filter((v) => set.has(v.country)).map((v) => v.country)
  }, [sources])
  const key = highlighted.join(',')

  // A change of the selected markets ends a manual view, and drops a focus that no longer belongs to them (e.g. after
  // clearing the selection) – so the camera follows the latest meaningful change.
  const previousKey = useRef(key)
  useEffect(() => {
    if (previousKey.current === key) return
    previousKey.current = key
    setView('auto')
    setFocus((f) => (f && key.split(',').includes(f) ? f : null))
  }, [key])

  const value = useMemo<WorldMapContextValue>(() => ({ highlighted, focus, signals, view, setView, setSource, signal }),
    [highlighted, focus, signals, view, setSource, signal])

  return <WorldMapContext.Provider value={value}>{children}</WorldMapContext.Provider>
}

export function useWorldMap(): WorldMapContextValue | null {
  return useContext(WorldMapContext)
}

/** Highlights the given markets on the background map while the calling component is mounted. */
export function useMapHighlight(key: string, countries: string[] | undefined) {
  const map = useWorldMap()
  const setSource = map?.setSource
  const joined = countries?.join(',')
  useEffect(() => {
    if (!setSource) return
    setSource(key, joined ? joined.split(',') : [])
    return () => setSource(key, null)
  }, [setSource, key, joined])
}

/** `signal(country, 'pulse' | 'blink')`; a no-op outside the provider (tests, isolated components) or for unknown markets. */
export function useMapSignal() {
  const map = useWorldMap()
  const signal = map?.signal
  return useCallback((country: string | null, kind: MapSignal['kind']) => { if (country) signal?.(country, kind) }, [signal])
}
