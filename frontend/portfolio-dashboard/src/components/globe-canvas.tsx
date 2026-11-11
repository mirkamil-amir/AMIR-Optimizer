import { useEffect, useRef } from 'react'
import { COUNTRY_PATHS, MAP_HEIGHT, MAP_TOP } from '@/components/world-map-geo'
import { arcPairs, arcPoints, landDots, orbitPoint, placeGlobe, seaDots, toVec, type GlobeLayout, type Vec3 } from '@/lib/globe'
import { venues, type Venue } from '@/lib/markets'

// Loaded on demand by the start-up intro (globe-intro.tsx): the country outlines stay out of the initial bundle.
// Matrix's globe (D-56), all drawn here on a Canvas 2D: a field of stars; the dots of the land fly in and settle on a lit,
// glassy sphere; radial spikes fringe its outline; arcs between the catalogue's exchanges carry gold packets in the
// brand's accent colour; an exchange is named when an arc lands on it; city markers are small dot matrices like the logo;
// an orbit ring with two gold satellites circles the globe; the pointer tilts it slightly; a soft bloom pass makes the
// light glow. No dependency.

const RAD = Math.PI / 180
const LAND_SPACING = 1.1 // degrees between land dots (about 9,000)
const SEA_SPACING = 2.2 // the faint sea grid (about 5,700)
const STRIDE = 6 // per point: x, y, z, kind (0 sea, 1 land, 2 cyan coast), spike length (× radius, 0 = none), phase
const STARS = 240

/** Deterministic pseudo-random number in [0, 1), so every start looks alike. */
function hash(i: number) {
  const s = Math.sin(i * 12.9898 + 78.233) * 43758.5453
  return s - Math.floor(s)
}
const clamp01 = (v: number) => Math.min(1, Math.max(0, v))
const easeOut = (v: number) => 1 - (1 - clamp01(v)) ** 3

interface Arc { pts: Vec3[]; to: Venue }
interface Scene { points: Float32Array; view: Float32Array; count: number; cities: Vec3[]; arcs: Arc[]; stars: Float32Array }

/** The globe's geometry, built once on first use (about 14,600 dots; about 70 ms). */
let scene: Scene | null = null
function globeScene(): Scene {
  if (scene) return scene
  const paths = Object.values(COUNTRY_PATHS)
  const dots = [
    ...landDots(paths, MAP_TOP, MAP_HEIGHT, LAND_SPACING).map((d) => ({ d, land: true })),
    ...seaDots(paths, MAP_TOP, MAP_HEIGHT, SEA_SPACING).map((d) => ({ d, land: false })),
  ]
  const points = new Float32Array(dots.length * STRIDE)
  dots.forEach(({ d, land }, i) => {
    const v = toVec(d)
    const [a, b, c] = [hash(i * 3 + 1), hash(i * 3 + 2), hash(i * 3 + 3)]
    const o = i * STRIDE
    points[o] = v.x
    points[o + 1] = v.y
    points[o + 2] = v.z
    points[o + 3] = land ? (d.coast && c < 0.35 ? 2 : 1) : 0
    points[o + 4] = land ? (a < 0.45 ? 0.015 + 0.14 * b ** 2.5 : 0) : (a < 0.14 ? 0.01 + 0.07 * b ** 2 : 0)
    points[o + 5] = c
  })
  // Two strides through the catalogue give more distinct pairs than one; a pair already drawn either way is skipped.
  const seen = new Set<string>()
  const pairs = [...arcPairs(venues, 30, 7), ...arcPairs(venues, 30, 11)].filter(([a, b]) => {
    const key = [a.city, b.city].sort().join('|')
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })
  const stars = new Float32Array(STARS * 4) // x, y (0…1 of the window), size, brightness
  for (let i = 0; i < STARS; i++) {
    stars[i * 4] = hash(9001 + i)
    stars[i * 4 + 1] = hash(7001 + i)
    stars[i * 4 + 2] = 0.6 + 1.1 * hash(5001 + i) ** 3
    stars[i * 4 + 3] = 0.12 + 0.5 * hash(3001 + i) ** 2
  }
  scene = {
    points,
    view: new Float32Array(dots.length * 3),
    count: dots.length,
    cities: venues.map(toVec),
    arcs: pairs.map(([a, b]) => ({ pts: arcPoints(a, b, 64, 0.24), to: b })),
    stars,
  }
  return scene
}

const SPIN_START = -45 // the Atlantic faces the viewer first: the Americas, Europe and Africa are in view
const SPIN_PER_MS = 0.005 // 5° per second
const TILT = 18
const LOOK_SPIN = 10 // degrees the pointer turns the globe at the window's edge
const LOOK_TILT = 6
// The entrance: the sphere fades in, each dot flies in from further out, then spikes, orbit and arcs follow.
const SPHERE_IN_MS = 1200
const DOT_FLIGHT_MS = 1400
const DOT_SPREAD_MS = 900
const SPIKES_FROM_MS = 2000
const ORBIT_FROM_MS = 1000
const ARCS_FROM_MS = 1800
const STILL_AT_MS = 9000 // the moment shown as the still frame (reduced motion): everything has arrived
const ARC_EVERY_MS = 330 // a new arc starts this often; with a 6.6 s life about 20 are in view at once
const ARC_GROW_MS = 1800
const ARC_HOLD_MS = 3600
const ARC_FADE_MS = 1200
const ARC_LIFE_MS = ARC_GROW_MS + ARC_HOLD_MS + ARC_FADE_MS
const PACKET_MS = 1500 // a gold packet runs along a standing arc this often
const LABEL_MS = 2600 // how long an exchange stays named after an arc lands on it
const ORBIT = { radius: 1.2, tilt: 14, roll: -16 }
const LIGHT = (() => {
  const [x, y, z] = [-0.45, 0.55, 0.7] // from the upper left, towards the viewer
  const n = Math.hypot(x, y, z)
  return { x: x / n, y: y / n, z: z / n }
})()

const GOLD = '236, 196, 110' // the brand accent (--brand-accent, oklch(0.8 0.12 85)) on the dark globe
const DOT_COLOURS = ['120, 145, 220', '205, 220, 255', '110, 235, 255'] // sea, land, coast
const DOT_STRENGTH = [0.2, 1.15, 1.15]
const BANDS = 6
const FONT = '"Geist Variable", system-ui, sans-serif'

interface Look { x: number; y: number } // pointer position, −1 … 1 across the window

/** Draws one frame of the globe at time `t` (ms since start) on a canvas sized in CSS pixels. */
function drawGlobe(ctx: CanvasRenderingContext2D, w: number, h: number, t: number, layout: GlobeLayout, look: Look) {
  const { points, view, count, cities, arcs, stars } = globeScene()
  const { cx, cy, r } = placeGlobe(layout, w, h)
  const s = -(SPIN_START + t * SPIN_PER_MS + look.x * LOOK_SPIN) * RAD
  const tilt = (TILT - look.y * LOOK_TILT) * RAD
  const [cs, sn, ct, st] = [Math.cos(s), Math.sin(s), Math.cos(tilt), Math.sin(tilt)]
  /** Turns a point of the globe into view space (as `rotate` in lib/globe.ts, without its trigonometry per call). */
  const turn = (x: number, y: number, z: number): Vec3 => {
    const z1 = -x * sn + z * cs
    return { x: x * cs + z * sn, y: y * ct - z1 * st, z: y * st + z1 * ct }
  }
  const hiddenBehind = (p: Vec3) => p.z < 0 && p.x * p.x + p.y * p.y < 1
  const disc = () => {
    ctx.beginPath()
    ctx.arc(cx, cy, r, 0, Math.PI * 2)
  }
  const flare = (x: number, y: number, radius: number, alpha: number, colour = '160, 195, 255') => {
    const g = ctx.createRadialGradient(x, y, 0, x, y, radius)
    g.addColorStop(0, `rgba(235, 242, 255, ${alpha})`)
    g.addColorStop(0.25, `rgba(${colour}, ${alpha * 0.45})`)
    g.addColorStop(1, `rgba(${colour}, 0)`)
    ctx.fillStyle = g
    ctx.fillRect(x - radius, y - radius, 2 * radius, 2 * radius)
  }
  const sphereIn = easeOut(t / SPHERE_IN_MS)

  ctx.globalCompositeOperation = 'source-over'
  ctx.globalAlpha = 1
  ctx.clearRect(0, 0, w, h)

  // Stars, a little parallax against the pointer, twinkling slowly.
  ctx.fillStyle = 'rgb(205, 218, 255)'
  for (let i = 0; i < STARS; i++) {
    const size = stars[i * 4 + 2]
    ctx.globalAlpha = stars[i * 4 + 3] * (0.65 + 0.35 * Math.sin(t * 0.0011 + i * 2.3)) * sphereIn
    ctx.fillRect(stars[i * 4] * w - look.x * 6 * size, stars[i * 4 + 1] * h - look.y * 6 * size, size, size)
  }

  // Atmosphere, then the sphere lit from the upper left with a soft reflection along its lower edge.
  ctx.globalAlpha = sphereIn
  const glow = ctx.createRadialGradient(cx, cy, r * 0.9, cx, cy, r * 1.32)
  glow.addColorStop(0, 'rgba(70, 110, 255, 0.34)')
  glow.addColorStop(0.35, 'rgba(70, 110, 255, 0.12)')
  glow.addColorStop(1, 'rgba(70, 110, 255, 0)')
  ctx.fillStyle = glow
  ctx.beginPath()
  ctx.arc(cx, cy, r * 1.32, 0, Math.PI * 2)
  ctx.fill()
  const body = ctx.createRadialGradient(cx - r * 0.35, cy - r * 0.4, r * 0.05, cx, cy, r)
  body.addColorStop(0, '#565d84')
  body.addColorStop(0.55, '#28315e')
  body.addColorStop(1, '#111937')
  ctx.fillStyle = body
  disc()
  ctx.fill()
  ctx.save()
  disc()
  ctx.clip()
  const low = ctx.createRadialGradient(cx + r * 0.05, cy + r * 0.85, 0, cx + r * 0.05, cy + r * 0.85, r * 0.6)
  low.addColorStop(0, 'rgba(175, 195, 255, 0.22)')
  low.addColorStop(1, 'rgba(175, 195, 255, 0)')
  ctx.fillStyle = low
  ctx.fillRect(cx - r, cy - r, 2 * r, 2 * r)
  ctx.restore()
  ctx.globalAlpha = 1

  // Every dot in view space, once per frame.
  for (let i = 0; i < count; i++) {
    const o = i * STRIDE
    const p = turn(points[o], points[o + 1], points[o + 2])
    view[i * 3] = p.x
    view[i * 3 + 1] = p.y
    view[i * 3 + 2] = p.z
  }

  // Dots: each flies in from further out and settles on the sphere; the far side's land shows faintly through the
  // glass; the near side is lit and fades out towards the rim. Grouped into brightness bands per kind, one fill each.
  const unit = Math.max(0.8, r / 300)
  const bands: number[][] = Array.from({ length: 3 * BANDS + 1 }, () => [])
  const behind = 3 * BANDS
  for (let i = 0; i < count; i++) {
    const o = i * STRIDE
    const arrived = easeOut((t - 150 - points[o + 5] * DOT_SPREAD_MS) / DOT_FLIGHT_MS)
    if (arrived <= 0) continue
    const [x, y, z] = [view[i * 3], view[i * 3 + 1], view[i * 3 + 2]]
    const out = 1 + (1 - arrived) * 0.9
    const kind = points[o + 3]
    if (z < 0) {
      if (kind > 0 && z < -0.1 && arrived === 1) bands[behind].push(cx + x * r, cy - y * r, unit * 0.7)
      continue
    }
    const lit = 0.35 + 0.65 * Math.max(0, x * LIGHT.x + y * LIGHT.y + z * LIGHT.z)
    const level = Math.min(0.99, lit * Math.min(1, z / 0.18) * arrived)
    const size = unit * (kind === 0 ? 0.7 : 1.15) * (0.65 + 0.45 * z)
    bands[kind * BANDS + Math.floor(level * BANDS)].push(cx + x * r * out, cy - y * r * out, size)
  }
  bands.forEach((band, b) => {
    if (band.length === 0) return
    if (b === behind) ctx.fillStyle = 'rgba(150, 175, 255, 0.09)'
    else {
      const kind = Math.floor(b / BANDS)
      ctx.fillStyle = `rgba(${DOT_COLOURS[kind]}, ${(DOT_STRENGTH[kind] * ((b % BANDS) + 0.5)) / BANDS})`
    }
    ctx.beginPath()
    for (let k = 0; k < band.length; k += 3) ctx.rect(band[k] - band[k + 2] / 2, band[k + 1] - band[k + 2] / 2, band[k + 2], band[k + 2])
    ctx.fill()
  })

  ctx.globalCompositeOperation = 'lighter'

  // Spikes: short radial lines standing on the dots. Seen from the front they are points; at the rim they stand out
  // sideways, and those on the far side show only where they rise past the outline – that is the fringe.
  const spikesIn = easeOut((t - SPIKES_FROM_MS) / 1200)
  if (spikesIn > 0) {
    const spikes: [Path2D, Path2D, Path2D] = [new Path2D(), new Path2D(), new Path2D()] // near land, near sea, far side
    for (let i = 0; i < count; i++) {
      const o = i * STRIDE
      if (points[o + 4] === 0) continue
      const [x, y, z] = [view[i * 3], view[i * 3 + 1], view[i * 3 + 2]]
      const d = Math.hypot(x, y)
      if (d < 0.2) continue
      const tip = d * (1 + spikesIn * points[o + 4] * (0.8 + 0.2 * Math.sin(t * 0.0012 + points[o + 5] * 6.28)))
      const from = z >= 0 ? d : 1
      if (tip <= from) continue
      const path = z < 0 ? spikes[2] : points[o + 3] === 0 ? spikes[1] : spikes[0]
      path.moveTo(cx + (x / d) * from * r, cy - (y / d) * from * r)
      path.lineTo(cx + (x / d) * tip * r, cy - (y / d) * tip * r)
    }
    ctx.lineWidth = Math.max(0.6, r / 650)
    ;[0.42, 0.2, 0.24].forEach((alpha, k) => {
      ctx.strokeStyle = `rgba(160, 190, 255, ${alpha})`
      ctx.stroke(spikes[k])
    })
  }

  // Rim light all round, brightest towards the light, and a glassy sheen with a thin crescent in the upper left.
  ctx.globalAlpha = sphereIn
  const rim = ctx.createRadialGradient(cx, cy, r * 0.78, cx, cy, r)
  rim.addColorStop(0, 'rgba(120, 160, 255, 0)')
  rim.addColorStop(0.7, 'rgba(120, 160, 255, 0.07)')
  rim.addColorStop(1, 'rgba(150, 185, 255, 0.3)')
  ctx.fillStyle = rim
  disc()
  ctx.fill()
  const edge = ctx.createLinearGradient(cx - r * 0.7, cy - r * 0.7, cx + r * 0.7, cy + r * 0.7)
  edge.addColorStop(0, 'rgba(215, 228, 255, 0.7)')
  edge.addColorStop(0.5, 'rgba(150, 180, 255, 0.25)')
  edge.addColorStop(1, 'rgba(150, 180, 255, 0.35)')
  ctx.strokeStyle = edge
  for (const [width, alpha] of [[r * 0.04, 0.18], [Math.max(1, r * 0.005), 1]]) {
    ctx.globalAlpha = alpha * sphereIn
    ctx.lineWidth = width
    ctx.beginPath()
    ctx.arc(cx, cy, r - width / 2, 0, Math.PI * 2)
    ctx.stroke()
  }
  ctx.globalAlpha = sphereIn
  ctx.save()
  disc()
  ctx.clip()
  ctx.translate(cx - r * 0.4, cy - r * 0.5)
  ctx.rotate(-0.6)
  ctx.scale(1, 0.42)
  const sheen = ctx.createRadialGradient(0, 0, 0, 0, 0, r * 0.6)
  sheen.addColorStop(0, 'rgba(235, 242, 255, 0.13)')
  sheen.addColorStop(1, 'rgba(235, 242, 255, 0)')
  ctx.fillStyle = sheen
  ctx.fillRect(-r, -r, 2 * r, 2 * r)
  ctx.restore()
  ctx.strokeStyle = 'rgba(235, 242, 255, 0.3)'
  ctx.lineWidth = Math.max(1, r * 0.006)
  ctx.beginPath()
  ctx.arc(cx, cy, r * 0.955, Math.PI * 1.1, Math.PI * 1.4)
  ctx.stroke()
  ctx.globalAlpha = 1

  // Arcs between exchanges, staggered: each grows (eased), stands, then fades; parts behind the sphere show faintly
  // through it. While an arc stands, gold packets run along it; where it lands, a gold ring spreads and the exchange
  // is named.
  const line = Math.max(0.8, r / 320)
  const cycle = arcs.length * ARC_EVERY_MS
  const labels: { x: number; y: number; name: string; city: string; alpha: number }[] = []
  ctx.lineCap = 'round'
  ctx.lineJoin = 'round'
  for (let k = 0; k < arcs.length; k++) {
    const since = t - ARCS_FROM_MS - k * ARC_EVERY_MS
    if (since < 0) continue
    const age = since % cycle
    if (age > ARC_LIFE_MS) continue
    const grow = easeOut(age / ARC_GROW_MS)
    const fade = age < ARC_GROW_MS + ARC_HOLD_MS ? Math.min(1, age / 200) : 1 - (age - ARC_GROW_MS - ARC_HOLD_MS) / ARC_FADE_MS
    const { pts, to } = arcs[k]
    const last = Math.max(1, Math.round(grow * (pts.length - 1)))
    const near = new Path2D()
    const far = new Path2D()
    const screen: { x: number; y: number; hidden: boolean }[] = []
    let head: Vec3 = pts[0]
    for (let i = 0; i <= last; i++) {
      const p = turn(pts[i].x, pts[i].y, pts[i].z)
      const at = { x: cx + p.x * r, y: cy - p.y * r, hidden: hiddenBehind(p) }
      const prev = screen[i - 1]
      if (prev) {
        const path = prev.hidden || at.hidden ? far : near
        path.moveTo(prev.x, prev.y)
        path.lineTo(at.x, at.y)
      }
      screen.push(at)
      head = p
    }
    ctx.strokeStyle = `rgba(190, 215, 255, ${0.12 * fade})`
    ctx.lineWidth = line
    ctx.stroke(far)
    ctx.strokeStyle = `rgba(140, 180, 255, ${0.1 * fade})`
    ctx.lineWidth = line * 4
    ctx.stroke(near)
    ctx.strokeStyle = `rgba(200, 222, 255, ${0.72 * fade})`
    ctx.lineWidth = line
    ctx.stroke(near)

    if (grow === 1 && age < ARC_GROW_MS + ARC_HOLD_MS) {
      // A gold packet with a short tail.
      const at = (((age - ARC_GROW_MS) % PACKET_MS) / PACKET_MS) * (screen.length - 1)
      const i0 = Math.floor(at)
      for (let j = 0; j < 6; j++) {
        const a = screen[i0 - j]
        const b = screen[i0 - j + 1]
        if (!a || !b || a.hidden || b.hidden) continue
        ctx.strokeStyle = `rgba(${GOLD}, ${0.9 * (1 - j / 6) * fade})`
        ctx.lineWidth = line * 1.6
        ctx.beginPath()
        ctx.moveTo(a.x, a.y)
        ctx.lineTo(b.x, b.y)
        ctx.stroke()
      }
      const p = screen[i0]
      if (p && !p.hidden) flare(p.x, p.y, Math.max(5, r * 0.025), 0.8 * fade, GOLD)
    }
    if (hiddenBehind(head)) continue
    const [hx, hy] = [cx + head.x * r, cy - head.y * r]
    if (grow < 1) flare(hx, hy, Math.max(6, r * 0.035), fade)
    else if (head.z > 0) {
      const landed = age - ARC_GROW_MS
      if (landed < 900) {
        const p = landed / 900
        ctx.strokeStyle = `rgba(${GOLD}, ${0.8 * (1 - p)})`
        ctx.lineWidth = line
        ctx.beginPath()
        ctx.ellipse(hx, hy, r * (0.01 + 0.06 * p), r * (0.01 + 0.06 * p) * (0.35 + 0.65 * head.z), 0, 0, Math.PI * 2)
        ctx.stroke()
      }
      if (landed < LABEL_MS && head.z > 0.25)
        labels.push({ x: hx, y: hy, name: to.exchange.toUpperCase(), city: to.city, alpha: Math.min(1, landed / 300, (LABEL_MS - landed) / 600) })
    }
  }

  // Exchange cities: a soft glow and a 3 × 3 dot matrix like the logo's, with a gold centre; they twinkle slightly.
  cities.forEach((c, i) => {
    const p = turn(c.x, c.y, c.z)
    if (p.z <= 0.03) return
    const [x, y] = [cx + p.x * r, cy - p.y * r]
    const shine = (0.75 + 0.25 * Math.sin(t * 0.002 + i * 1.7)) * Math.min(1, p.z / 0.2) * sphereIn
    flare(x, y, Math.max(6, r * 0.04) * (0.6 + 0.4 * p.z), 0.45 * shine)
    const g = Math.max(0.9, r / 320) * (0.7 + 0.3 * p.z)
    for (let dx = -1; dx <= 1; dx++)
      for (let dy = -1; dy <= 1; dy++) {
        const centre = dx === 0 && dy === 0
        ctx.fillStyle = centre ? `rgba(${GOLD}, ${shine})` : `rgba(225, 235, 255, ${(dx === 0 || dy === 0 ? 0.75 : 0.4) * shine})`
        ctx.fillRect(x + dx * 2.4 * g - g / 2, y + dy * 2.4 * g - g / 2, g, g)
      }
  })

  // The orbit ring with tick marks, faint behind the globe's outline, and two gold satellites with trails.
  const orbitIn = easeOut((t - ORBIT_FROM_MS) / 1500)
  if (orbitIn > 0) {
    const front = new Path2D()
    const back = new Path2D()
    const ticks: number[] = []
    let prev: { x: number; y: number; hidden: boolean; z: number } | null = null
    for (let i = 0; i <= 180; i++) {
      const p = orbitPoint((i / 180) * Math.PI * 2, ORBIT.radius, ORBIT.tilt, ORBIT.roll)
      const at = { x: cx + p.x * r, y: cy - p.y * r, hidden: hiddenBehind(p), z: p.z }
      if (prev && !prev.hidden && !at.hidden) {
        const path = at.z >= 0 ? front : back
        path.moveTo(prev.x, prev.y)
        path.lineTo(at.x, at.y)
      }
      if (i % 4 === 0 && !at.hidden && at.z >= 0) ticks.push(at.x, at.y)
      prev = at
    }
    ctx.lineWidth = Math.max(0.7, r / 500)
    ctx.strokeStyle = `rgba(170, 195, 255, ${0.4 * orbitIn})`
    ctx.stroke(front)
    ctx.strokeStyle = `rgba(170, 195, 255, ${0.1 * orbitIn})`
    ctx.stroke(back)
    ctx.fillStyle = `rgba(200, 218, 255, ${0.45 * orbitIn})`
    const tick = Math.max(1, r / 400)
    for (let k = 0; k < ticks.length; k += 2) ctx.fillRect(ticks[k] - tick / 2, ticks[k + 1] - tick / 2, tick, tick)
    for (let k = 0; k < 2; k++) {
      const way = k === 0 ? 1 : -1
      const a = t * (k === 0 ? 0.00028 : -0.00019) + k * 2.7
      for (let j = 12; j >= 0; j--) {
        const p = orbitPoint(a - j * 0.014 * way, ORBIT.radius, ORBIT.tilt, ORBIT.roll)
        if (hiddenBehind(p)) continue
        const depth = p.z >= 0 ? 1 : 0.45
        const [x, y] = [cx + p.x * r, cy - p.y * r]
        if (j === 0) flare(x, y, Math.max(6, r * 0.03), 0.85 * orbitIn * depth, GOLD)
        else {
          ctx.fillStyle = `rgba(${GOLD}, ${0.5 * (1 - j / 12) * orbitIn * depth})`
          ctx.fillRect(x - 1, y - 1, 2, 2)
        }
      }
    }
  }

  // Names of the exchanges arcs just landed on: a thin leader line, the exchange in small capitals, the city below.
  ctx.globalCompositeOperation = 'source-over'
  const size = Math.max(10, Math.min(12, r / 28))
  const spacing = 'letterSpacing' in ctx
  const placed: [number, number][] = []
  for (const l of labels) {
    ctx.font = `600 ${size}px ${FONT}`
    if (spacing) ctx.letterSpacing = '0.08em'
    const width = ctx.measureText(l.name).width
    // Outwards from the globe's centre, flipped where that would leave the window; skipped if neither side fits.
    const fits = (d: number) => (d > 0 ? l.x + 22 + width <= w - 8 : l.x - 22 - width >= 8)
    const dir = [l.x >= cx ? 1 : -1, l.x >= cx ? -1 : 1].find(fits)
    if (!dir) continue
    const [ex, ey] = [l.x + dir * 14, l.y - 14]
    // Exchanges close together (Europe): the newer label waits until the older one has gone.
    if (placed.some(([px, py]) => Math.abs(px - ex) < 170 && Math.abs(py - ey) < size * 3)) continue
    placed.push([ex, ey])
    ctx.strokeStyle = `rgba(${GOLD}, ${0.7 * l.alpha})`
    ctx.lineWidth = 0.8
    ctx.beginPath()
    ctx.moveTo(l.x + dir * 3, l.y - 3)
    ctx.lineTo(ex, ey)
    ctx.lineTo(ex + dir * (width + 8), ey)
    ctx.stroke()
    const tx = dir > 0 ? ex + 4 : ex - 4 - width
    ctx.fillStyle = `rgba(232, 240, 255, ${0.95 * l.alpha})`
    ctx.fillText(l.name, tx, ey - 5)
    ctx.font = `400 ${size - 1}px ${FONT}`
    if (spacing) ctx.letterSpacing = '0.02em'
    ctx.fillStyle = `rgba(190, 205, 235, ${0.75 * l.alpha})`
    ctx.fillText(l.city, tx, ey + size + 2)
  }
  if (spacing) ctx.letterSpacing = '0px'
}

/**
 * Bloom: a small, blurred copy of the bright parts only (darkening and contrast drop the sphere's mid-tones to black) added back on
 * top. Skipped where the canvas has no `filter` (older Safari).
 */
function bloomer(canvas: HTMLCanvasElement, ctx: CanvasRenderingContext2D) {
  const small = document.createElement('canvas')
  const sctx = small.getContext('2d')
  if (!sctx || typeof sctx.filter !== 'string') return { size: () => {}, apply: () => {} }
  return {
    size: () => {
      small.width = Math.max(1, Math.round(canvas.clientWidth / 4))
      small.height = Math.max(1, Math.round(canvas.clientHeight / 4))
    },
    apply: () => {
      sctx.clearRect(0, 0, small.width, small.height)
      sctx.filter = 'brightness(0.7) contrast(2.6) blur(3px)'
      sctx.drawImage(canvas, 0, 0, small.width, small.height)
      ctx.save()
      ctx.setTransform(1, 0, 0, 1, 0, 0)
      ctx.globalCompositeOperation = 'lighter'
      ctx.globalAlpha = 0.45
      ctx.drawImage(small, 0, 0, canvas.width, canvas.height)
      ctx.restore()
    },
  }
}

/** The globe on a full-window canvas: animated, or one still frame with reduced motion. Decorative (aria-hidden). */
export default function GlobeCanvas({ still, layout = 'center' }: { still: boolean; layout?: GlobeLayout }) {
  const ref = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const canvas = ref.current
    const ctx = canvas?.getContext('2d')
    if (!canvas || !ctx) return
    let frame = 0
    const start = performance.now()
    const bloom = bloomer(canvas, ctx)
    const look: Look = { x: 0, y: 0 }
    const aim: Look = { x: 0, y: 0 }
    const size = () => {
      const dpr = Math.min(window.devicePixelRatio || 1, 2)
      canvas.width = Math.round(canvas.clientWidth * dpr)
      canvas.height = Math.round(canvas.clientHeight * dpr)
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      bloom.size()
    }
    const paint = (now: number) => {
      drawGlobe(ctx, canvas.clientWidth, canvas.clientHeight, still ? STILL_AT_MS : now - start, layout, look)
      bloom.apply()
    }
    const loop = (now: number) => {
      look.x += (aim.x - look.x) * 0.04
      look.y += (aim.y - look.y) * 0.04
      paint(now)
      frame = requestAnimationFrame(loop)
    }
    const onPointer = (e: PointerEvent) => {
      aim.x = (e.clientX / window.innerWidth) * 2 - 1
      aim.y = (e.clientY / window.innerHeight) * 2 - 1
    }
    size()
    if (still) paint(start)
    else {
      frame = requestAnimationFrame(loop)
      window.addEventListener('pointermove', onPointer)
    }
    const onResize = () => {
      size()
      if (still) paint(start)
    }
    window.addEventListener('resize', onResize)
    return () => {
      cancelAnimationFrame(frame)
      window.removeEventListener('resize', onResize)
      window.removeEventListener('pointermove', onPointer)
    }
  }, [still, layout])
  return <canvas ref={ref} aria-hidden className="fixed inset-0 size-full" data-testid="globe-canvas" />
}
