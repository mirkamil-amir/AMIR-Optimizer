import { venueByCountry, type GeoBox } from './markets'
import type { MapView } from './world-map'

/**
 * Camera of the background world map: which part of the world to show for a set of markets, and the CSS transform that
 * shows it. Pure functions – the component applies the result as one CSS transform transition (native easing,
 * interruptible), so camera movement never re-renders React or touches portfolio data.
 *
 * Frame of reference: the equirectangular map drawn by `world-map-geo.ts` (x = lon + 180, y = MAP_TOP − lat, in degrees),
 * scaled to fit ("meet") and centred in a viewport of W × H pixels.
 */
export const MAP_TOP = 84
export const MAP_WIDTH = 360
export const MAP_HEIGHT = 142

/** Never zoom further in than this (a single small market would otherwise fill the screen). */
export const MAX_ZOOM = 5
/** Share of the viewport kept free around the framed markets on each side. */
export const PADDING = 0.1

export interface MapBox { x0: number; y0: number; x1: number; y1: number }
export interface Camera { k: number; tx: number; ty: number }

export const WORLD: Camera = { k: 1, tx: 0, ty: 0 }

/** A geographic box in map units. */
export function toMapBox([west, south, east, north]: GeoBox): MapBox {
  return { x0: west + 180, x1: east + 180, y0: MAP_TOP - north, y1: MAP_TOP - south }
}

/**
 * Region to frame for a set of markets: one market → its region (e.g. Europe for a German listing); several → the union of
 * their regions. Unknown markets are ignored; nothing known → null (the world view). The map has one copy of the world
 * from −180° to 180°; every supported exchange lies between −125° and 155°, so no frame crosses the antimeridian.
 */
export function frameFor(countries: Iterable<string>): MapBox | null {
  let box: MapBox | null = null
  for (const country of new Set(countries)) {
    const venue = venueByCountry.get(country)
    if (!venue) continue
    const b = toMapBox(venue.frame)
    box = box ? { x0: Math.min(box.x0, b.x0), y0: Math.min(box.y0, b.y0), x1: Math.max(box.x1, b.x1), y1: Math.max(box.y1, b.y1) } : b
  }
  return box
}

/** Scale and offset of the map drawn to fit ("meet") and centred in a W × H viewport. */
export function baseFit(width: number, height: number) {
  const s = Math.min(width / MAP_WIDTH, height / MAP_HEIGHT)
  return { s, ox: (width - MAP_WIDTH * s) / 2, oy: (height - MAP_HEIGHT * s) / 2 }
}

/**
 * Camera (CSS `translate(tx, ty) scale(k)`, origin top-left) that shows `box` centred with padding in a W × H viewport.
 * k is clamped to 1…MAX_ZOOM; when zoomed in, the offset is clamped so the map does not drift away from the viewport edges.
 */
export function cameraFor(box: MapBox | null, width: number, height: number): Camera {
  if (!box || width <= 0 || height <= 0) return WORLD
  const { s, ox, oy } = baseFit(width, height)
  const bw = Math.max((box.x1 - box.x0) * s, 1)
  const bh = Math.max((box.y1 - box.y0) * s, 1)
  const usable = 1 - 2 * PADDING
  const k = Math.min(MAX_ZOOM, Math.max(1, Math.min((width * usable) / bw, (height * usable) / bh)))
  const cx = ox + ((box.x0 + box.x1) / 2) * s
  const cy = oy + ((box.y0 + box.y1) / 2) * s
  let tx = width / 2 - k * cx
  let ty = height / 2 - k * cy
  // Keep the drawn map covering the viewport where it is large enough to do so; otherwise keep it centred.
  const mapW = MAP_WIDTH * s * k
  const mapH = MAP_HEIGHT * s * k
  tx = mapW >= width ? clamp(tx, width - k * (ox + MAP_WIDTH * s), -k * ox) : (width - mapW) / 2 - k * ox
  ty = mapH >= height ? clamp(ty, height - k * (oy + MAP_HEIGHT * s), -k * oy) : (height - mapH) / 2 - k * oy
  return { k: round(k), tx: round(tx), ty: round(ty) }
}

/**
 * Duration of a camera move: short for small adjustments, calm for intercontinental moves (450–1400 ms), from the zoom
 * change and the distance travelled relative to the viewport.
 */
export function transitionMs(from: Camera, to: Camera, width: number, height: number): number {
  const zoom = Math.abs(Math.log(to.k / from.k))
  const diagonal = Math.hypot(width, height) || 1
  // Distance between the viewport centres in map pixels before and after (independent of zoom).
  const centre = (c: Camera) => [(width / 2 - c.tx) / c.k, (height / 2 - c.ty) / c.k]
  const [ax, ay] = centre(from)
  const [bx, by] = centre(to)
  const travel = Math.hypot(ax - bx, ay - by) / diagonal
  return Math.round(clamp(450 + 500 * zoom + 1600 * travel, 450, 1400))
}

export function sameCamera(a: Camera, b: Camera) {
  return Math.abs(a.k - b.k) < 0.001 && Math.abs(a.tx - b.tx) < 0.5 && Math.abs(a.ty - b.ty) < 0.5
}

/** Pixel position of a map point (longitude, latitude) under a camera. */
export function project(lon: number, lat: number, camera: Camera, width: number, height: number): [number, number] {
  const { s, ox, oy } = baseFit(width, height)
  return [round(camera.tx + camera.k * (ox + (lon + 180) * s)), round(camera.ty + camera.k * (oy + (MAP_TOP - lat) * s))]
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))
const round = (v: number) => Math.round(v * 1000) / 1000

/** Rapid selections (several clicks in a row) are combined into one camera move. */
export const CAMERA_DEBOUNCE_MS = 220

/** Countries the camera frames for a view: the highlighted markets plus the focused one, the whole world, or one market. */
export function targetCountries(view: MapView, highlighted: string[], focus: string | null): string[] {
  if (view === 'world') return []
  if (view !== 'auto') return [view]
  return focus && !highlighted.includes(focus) ? [...highlighted, focus] : highlighted
}
