/**
 * Geometry of the start-up globe (pure, no DOM; unit-tested in globe.test.ts): land and sea dots sampled from the Natural Earth
 * country outlines the background map already uses (`components/world-map-geo.ts`, equirectangular: x = longitude + 180,
 * y = MAP_TOP − latitude), an orthographic projection with rotation, and great-circle arcs between exchange cities.
 * Drawn by `components/globe-intro.tsx`; our own drawing, no third-party artwork.
 */

export interface Vec3 { x: number; y: number; z: number }
export interface LatLon { lat: number; lon: number }

const RAD = Math.PI / 180

/** Closed rings (lists of [x, y]) of an SVG path made of M, L and Z commands. */
export function parseRings(path: string): [number, number][][] {
  const rings: [number, number][][] = []
  let ring: [number, number][] = []
  for (const m of path.matchAll(/([MLZ])\s*(?:(-?[\d.]+)[ ,](-?[\d.]+))?/g)) {
    if (m[1] === 'Z') {
      if (ring.length > 2) rings.push(ring)
      ring = []
      continue
    }
    if (m[1] === 'M' && ring.length > 2) rings.push(ring)
    if (m[1] === 'M') ring = []
    ring.push([Number(m[2]), Number(m[3])])
  }
  if (ring.length > 2) rings.push(ring)
  return rings
}

/** A grid dot; `coast` marks a land dot whose neighbour east or west in its row is sea. */
export interface GridDot extends LatLon { coast: boolean }

/**
 * Land dots on an even grid: rows every `spacing` degrees of latitude, and within a row every `spacing / cos(latitude)`
 * degrees of longitude, so dots are about equally far apart on the sphere. A dot is land when it lies inside the
 * outlines (even–odd rule, one scanline per row over all edges).
 */
export function landDots(paths: Iterable<string>, mapTop: number, mapHeight: number, spacing = 1.6): GridDot[] {
  return gridDots(paths, mapTop, mapHeight, spacing, true)
}

/** The same grid over the sea (outside the outlines), within the map's latitudes. */
export function seaDots(paths: Iterable<string>, mapTop: number, mapHeight: number, spacing = 2.2): GridDot[] {
  return gridDots(paths, mapTop, mapHeight, spacing, false)
}

function gridDots(paths: Iterable<string>, mapTop: number, mapHeight: number, spacing: number, land: boolean): GridDot[] {
  const edges: [number, number, number, number][] = []
  for (const p of paths)
    for (const r of parseRings(p))
      for (let i = 0; i < r.length; i++) {
        const [x0, y0] = r[i]
        const [x1, y1] = r[(i + 1) % r.length]
        if (y0 !== y1) edges.push([x0, y0, x1, y1])
      }
  const dots: GridDot[] = []
  for (let lat = mapTop - spacing / 2; lat > mapTop - mapHeight; lat -= spacing) {
    const y = mapTop - lat
    const xs: number[] = []
    for (const [x0, y0, x1, y1] of edges)
      if ((y0 <= y && y < y1) || (y1 <= y && y < y0)) xs.push(x0 + ((y - y0) / (y1 - y0)) * (x1 - x0))
    xs.sort((a, b) => a - b)
    const step = spacing / Math.max(Math.cos(lat * RAD), 0.15)
    // Inside or outside for every grid column of the row (x = 0 … 360), walking the sorted crossings once.
    const inside: boolean[] = []
    for (let k = 0, j = 0; k * step < 360; k++) {
      while (j < xs.length && xs[j] <= k * step) j++
      inside.push(j % 2 === 1)
    }
    for (let k = 0; k < inside.length; k++) {
      if (inside[k] !== land) continue
      const coast = land && (k === 0 || !inside[k - 1] || k === inside.length - 1 || !inside[k + 1])
      dots.push({ lat, lon: k * step - 180, coast })
    }
  }
  return dots
}

/** Unit vector of a point on the sphere (y up, z towards the viewer at longitude 0). */
export function toVec(p: LatLon): Vec3 {
  const lat = p.lat * RAD
  const lon = p.lon * RAD
  return { x: Math.cos(lat) * Math.sin(lon), y: Math.sin(lat), z: Math.cos(lat) * Math.cos(lon) }
}

/**
 * Turns the globe so that longitude `spin` faces the viewer, tilted by `tilt` degrees (north pole towards the viewer
 * for positive tilt). The result's z > 0 is the visible hemisphere; x right, y up.
 */
export function rotate(v: Vec3, spin: number, tilt: number): Vec3 {
  const s = -spin * RAD
  const x1 = v.x * Math.cos(s) + v.z * Math.sin(s)
  const z1 = -v.x * Math.sin(s) + v.z * Math.cos(s)
  const t = tilt * RAD
  return { x: x1, y: v.y * Math.cos(t) - z1 * Math.sin(t), z: v.y * Math.sin(t) + z1 * Math.cos(t) }
}

/** Angle between two points of the sphere, in degrees. */
export function angularDistance(a: LatLon, b: LatLon): number {
  const u = toVec(a)
  const v = toVec(b)
  return Math.acos(Math.min(1, Math.max(-1, u.x * v.x + u.y * v.y + u.z * v.z))) / RAD
}

/**
 * `n` + 1 points of the great circle from a to b (spherical interpolation), lifted off the surface by up to `lift`
 * (fraction of the radius) in the middle, as arcs between cities are drawn.
 */
export function arcPoints(a: LatLon, b: LatLon, n = 48, lift = 0.18): Vec3[] {
  const u = toVec(a)
  const v = toVec(b)
  const omega = angularDistance(a, b) * RAD
  const points: Vec3[] = []
  for (let i = 0; i <= n; i++) {
    const t = i / n
    const [wa, wb] = omega < 1e-6 ? [1 - t, t] : [Math.sin((1 - t) * omega) / Math.sin(omega), Math.sin(t * omega) / Math.sin(omega)]
    const h = 1 + lift * Math.sin(Math.PI * t) * Math.min(1, omega / (Math.PI / 2))
    points.push({ x: (wa * u.x + wb * v.x) * h, y: (wa * u.y + wb * v.y) * h, z: (wa * u.z + wb * v.z) * h })
  }
  return points
}

/**
 * Pairs of cities for the arcs, in a fixed order: each city to the one `stride` places further, skipping pairs closer
 * than `minDegrees` (European neighbours would draw stubs). Deterministic, so every start looks alike.
 */
export function arcPairs<T extends LatLon>(cities: readonly T[], count: number, stride = 7, minDegrees = 25): [T, T][] {
  const pairs: [T, T][] = []
  const n = cities.length
  if (n < 2) return pairs
  for (let i = 0; pairs.length < count && i < n * 4; i++) {
    const a = cities[i % n]
    const b = cities[(i * stride + 3) % n]
    if (a !== b && angularDistance(a, b) >= minDegrees) pairs.push([a, b])
  }
  return pairs
}

export type GlobeLayout = 'center' | 'login'

/**
 * Where the globe sits: centred above the name and Enter button (intro), or – on the sign-in screen – left of the sign-in
 * panel on wide screens and above it on narrow ones.
 */
export function placeGlobe(layout: GlobeLayout, w: number, h: number): { cx: number; cy: number; r: number } {
  if (layout === 'center') return { cx: w / 2, cy: h * 0.44, r: Math.min(w, h) * (w < 640 ? 0.4 : 0.34) }
  if (w >= 1024) return { cx: w * 0.36, cy: h * 0.5, r: Math.min(w * 0.26, h * 0.4) }
  return { cx: w / 2, cy: Math.min(h * 0.23, w * 0.48), r: Math.min(w * 0.4, h * 0.19) }
}

/**
 * A point of the orbit ring around the globe, in view space: a circle of `radius` (× the globe's radius) at angle `a`
 * (radians), its plane tipped `tilt` degrees towards the viewer and rolled `roll` degrees in the screen plane. z > 0 is
 * the half in front of the globe. Independent of the globe's spin.
 */
export function orbitPoint(a: number, radius: number, tilt: number, roll: number): Vec3 {
  const [t, r] = [tilt * RAD, roll * RAD]
  const x = Math.cos(a) * radius
  const y = -Math.sin(a) * radius * Math.sin(t)
  const z = Math.sin(a) * radius * Math.cos(t)
  return { x: x * Math.cos(r) - y * Math.sin(r), y: x * Math.sin(r) + y * Math.cos(r), z }
}
