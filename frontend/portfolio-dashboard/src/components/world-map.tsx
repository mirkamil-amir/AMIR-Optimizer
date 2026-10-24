import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Crosshair, Globe2, Maximize2 } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { CAMERA_DEBOUNCE_MS, cameraFor, frameFor, project, sameCamera, targetCountries, transitionMs, WORLD, type Camera } from '@/lib/map-camera'
import { countriesOf, venueByCountry, venues } from '@/lib/markets'
import { useSelection } from '@/lib/selection'
import { useMapHighlight, useMapSignal, useWorldMap } from '@/lib/world-map'
import { cn } from '@/lib/utils'

const NO_COUNTRIES: string[] = []

function prefersReducedMotion() {
  return typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
}

/**
 * Background of the app: a dotted, cool-toned world map (Natural Earth 1:50m countries) with the catalogue's exchange cities. The
 * markets the current page is about are highlighted and framed by a camera that moves smoothly as stocks are selected or
 * a portfolio changes (one market → its region, several → their combined extent, none → the world). A selected stock's
 * city ripples once, an opened stock's city blinks. Decorative and `aria-hidden`: the same information is in the page and
 * in the "Markets" control (`MapControl`). The camera is applied imperatively (one CSS transform transition), so moving it
 * never re-renders React or touches portfolio data.
 */
export function WorldMapBackdrop({ className }: { className?: string }) {
  const map = useWorldMap()
  const highlighted = map?.highlighted ?? NO_COUNTRIES
  const focus = map?.focus ?? null
  const signals = map?.signals ?? []
  const view = map?.view ?? 'auto'
  const [paths, setPaths] = useState<Record<string, string> | null>(null)
  // Zoom of the current camera target: the dot grid is drawn this much finer, so dots keep their size on screen and the
  // coastlines their detail at every zoom (updated once per move, never per frame).
  const [zoom, setZoom] = useState(1)

  const viewportRef = useRef<HTMLDivElement>(null)
  const cameraRef = useRef<HTMLDivElement>(null)
  const markerRefs = useRef(new Map<string, HTMLDivElement>())
  const size = useRef({ width: 0, height: 0 })
  const current = useRef<Camera>(WORLD)

  const targets = targetCountries(view, highlighted, focus)
  const targetKey = targets.join(',')
  const targetRef = useRef(targets)
  useLayoutEffect(() => { targetRef.current = targets })

  useEffect(() => {
    let alive = true
    // A separate chunk (≈270 KB, ≈100 KB compressed), loaded after the page so it never delays content; loaded once.
    void import('./world-map-geo').then((m) => { if (alive) setPaths(m.COUNTRY_PATHS) })
    return () => { alive = false }
  }, [])

  /** Moves the camera and the city markers together; `animate` = a native CSS transition, interruptible by the next call. */
  const apply = useCallback((camera: Camera, animate: boolean) => {
    const { width, height } = size.current
    const ms = animate && !prefersReducedMotion() ? transitionMs(current.current, camera, width, height) : 0
    const el = cameraRef.current
    if (el) {
      el.style.transitionDuration = `${ms}ms`
      el.style.transform = `translate(${camera.tx}px, ${camera.ty}px) scale(${camera.k})`
      el.style.setProperty('--map-k', String(camera.k))
    }
    for (const [country, marker] of markerRefs.current) {
      const v = venueByCountry.get(country)!
      const [x, y] = project(v.lon, v.lat, camera, width, height)
      marker.style.transitionDuration = `${ms}ms`
      marker.style.transform = `translate(${x}px, ${y}px)`
    }
    current.current = camera
    setZoom(Math.round(camera.k * 100) / 100)
  }, [])

  // Size of the map area; a layout change (sidebar, window) re-frames the same target without animation.
  useLayoutEffect(() => {
    const el = viewportRef.current
    if (!el) return
    const measure = () => {
      const { width, height } = el.getBoundingClientRect()
      if (width === size.current.width && height === size.current.height) return
      size.current = { width, height }
      apply(cameraFor(frameFor(targetRef.current), width, height), false)
    }
    measure()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [apply])

  // Selection changes: combined over a short pause, then one smooth move to the frame of the latest state.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      const { width, height } = size.current
      const next = cameraFor(frameFor(targetKey ? targetKey.split(',') : []), width, height)
      if (!sameCamera(next, current.current)) apply(next, true)
    }, CAMERA_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [targetKey, apply])

  const isoLit = useMemo(() => new Set(highlighted.map((c) => venueByCountry.get(c)?.iso).filter((iso): iso is string => !!iso)), [highlighted])
  const isoFocus = focus ? venueByCountry.get(focus)?.iso ?? null : null

  return (
    <div aria-hidden className={cn('world-map pointer-events-none select-none', className)} data-testid="world-map" data-view={view}
      data-targets={targetKey}>
      <div ref={viewportRef} className="absolute inset-x-0 top-24 bottom-0 overflow-hidden">
        <div ref={cameraRef} className="world-map-camera absolute inset-0 origin-top-left">
          {paths && <Countries paths={paths} lit={isoLit} focus={isoFocus} zoom={zoom} />}
        </div>
        {venues.map((v) => {
          const lit = highlighted.includes(v.country)
          const focused = focus === v.country
          const signal = signals.find((s) => s.country === v.country)
          return (
            <div key={v.country}
              ref={(el) => { if (el) markerRefs.current.set(v.country, el); else markerRefs.current.delete(v.country) }}
              data-country={v.country} data-state={signal?.kind ?? (focused ? 'focus' : lit ? 'lit' : 'idle')}
              className="world-map-city absolute top-0 left-0">
              {signal && <span key={signal.id} className={signal.kind === 'blink' ? 'world-map-blink-ring' : 'world-map-pulse-ring'} />}
              <span className="world-map-marker" />
              {(lit || focused || signal) && <span className={cn('world-map-label', `world-map-label-${v.label}`)}>{v.city}</span>}
            </div>
          )
        })}
      </div>
    </div>
  )
}

/** Dot spacing at zoom 1, in degrees (≈3 px on a desktop screen). */
const DOT = 0.9

/**
 * The countries filled with a dot grid (land, highlighted, focused), without borders. Rendered again
 * only when the highlighted or focused markets or the target zoom change – never per animation frame.
 */
const Countries = memo(function Countries({ paths, lit, focus, zoom }: {
  paths: Record<string, string>; lit: Set<string>; focus: string | null; zoom: number
}) {
  const d = DOT / zoom
  const dots = (id: string, r: number, className: string) => (
    <pattern id={id} width={d} height={d} patternUnits="userSpaceOnUse">
      <circle cx={d / 2} cy={d / 2} r={d * r} className={className} />
    </pattern>
  )
  return (
    <svg viewBox="0 0 360 142" preserveAspectRatio="xMidYMid meet" className="h-full w-full">
      <defs>
        {dots('world-map-dots', 0.3, 'world-map-dot')}
        {dots('world-map-dots-lit', 0.36, 'world-map-dot-lit')}
        {dots('world-map-dots-focus', 0.44, 'world-map-dot-focus')}
      </defs>
      {Object.entries(paths).map(([iso, path]) => (
        <path key={iso} d={path} data-iso={iso}
          fill={focus === iso ? 'url(#world-map-dots-focus)' : lit.has(iso) ? 'url(#world-map-dots-lit)' : 'url(#world-map-dots)'}
          className={cn('world-map-country', lit.has(iso) && 'world-map-country-lit', focus === iso && 'world-map-country-focus')} />
      ))}
      {/* Focused market drawn last, so its larger dots are never covered by a neighbour's. */}
      {focus && paths[focus] && <path d={paths[focus]} fill="url(#world-map-dots-focus)" className="world-map-country world-map-country-lit world-map-country-focus" />}
    </svg>
  )
})

/** Lights the markets of the temporary stock selection and ripples (and focuses) a city when one of its stocks is added. */
export function SelectionOnMap() {
  const { symbols } = useSelection()
  const signal = useMapSignal()
  useMapHighlight('selection', countriesOf(symbols))
  const previous = useRef<Set<string> | null>(null)
  useEffect(() => {
    const before = previous.current
    previous.current = new Set(symbols)
    if (!before) return
    const added = symbols.filter((s) => !before.has(s))
    for (const country of countriesOf(added)) signal(country, 'pulse')
  }, [symbols, signal])
  return null
}

/**
 * Header control of the map: the markets in view as text (the accessible counterpart of the decorative map) and the
 * viewing choices. "World view" or a single market pauses automatic framing until the selection changes; "Fit selected
 * markets" resumes it.
 */
export function MapControl() {
  const map = useWorldMap()
  if (!map) return null
  const { highlighted, focus, view, setView } = map
  const markets = focus && !highlighted.includes(focus) ? [...highlighted, focus] : highlighted
  return (
    <Popover>
      <PopoverTrigger render={<Button variant="ghost" size="sm" aria-label={`Market map: ${markets.length} market${markets.length === 1 ? '' : 's'} in view`} />}>
        <Globe2 /> <span className="hidden tabular xl:inline">{markets.length}</span>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80">
        <div>
          <div className="font-medium">Markets on the map</div>
          <p className="text-xs text-muted-foreground">Where the selected or held securities are listed (exchange), not where the companies are based.</p>
        </div>
        {markets.length === 0 ? <p className="text-xs text-muted-foreground">None selected – the map shows the world.</p> : (
          <ul aria-label="Markets in view" className="space-y-1 text-xs">
            {markets.map((c) => {
              const v = venueByCountry.get(c)!
              return (
                <li key={c} className="flex items-center justify-between gap-2">
                  <span>
                    <span className="font-medium">{v.countryName}</span> · {v.exchange}, {v.city}
                    {focus === c && <Badge variant="outline" className="ml-1.5 h-4 px-1 text-[10px]">focused</Badge>}
                  </span>
                  <Button size="icon-sm" variant="ghost" aria-label={`Show ${v.countryName} on the map`} aria-pressed={view === c} onClick={() => setView(c)}>
                    <Crosshair />
                  </Button>
                </li>
              )
            })}
          </ul>
        )}
        <div className="flex gap-2">
          <Button size="sm" variant={view === 'auto' ? 'secondary' : 'outline'} aria-pressed={view === 'auto'} onClick={() => setView('auto')} className="flex-1">
            <Maximize2 /> Fit selected markets
          </Button>
          <Button size="sm" variant={view === 'world' ? 'secondary' : 'outline'} aria-pressed={view === 'world'} onClick={() => setView('world')} className="flex-1">
            <Globe2 /> World view
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  )
}
