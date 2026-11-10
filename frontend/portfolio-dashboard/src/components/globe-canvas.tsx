import { useEffect, useRef } from 'react'
import { COUNTRY_PATHS, MAP_HEIGHT, MAP_TOP } from '@/components/world-map-geo'
import { arcPairs, arcPoints, landDots, rotate, toVec, type Vec3 } from '@/lib/globe'
import { venues } from '@/lib/markets'

// Loaded on demand by the start-up intro (globe-intro.tsx): the country outlines stay out of the initial bundle.

/** The globe's geometry, built once on first use (about 4,800 land dots, arcs between exchange cities; about 60 ms). */
let scene: { dots: Vec3[]; cities: Vec3[]; arcs: Vec3[][] } | null = null
function globeScene() {
  scene ??= {
    dots: landDots(Object.values(COUNTRY_PATHS), MAP_TOP, MAP_HEIGHT).map(toVec),
    cities: venues.map(toVec),
    arcs: arcPairs(venues, 24).map(([a, b]) => arcPoints(a, b)),
  }
  return scene
}

const SPIN_START = -45 // the Atlantic faces the viewer first: the Americas, Europe and Africa are in view
const SPIN_PER_MS = 0.012 // 12° per second
const TILT = 18
const ARC_EVERY_MS = 320
const ARC_GROW_MS = 900
const ARC_LIFE_MS = 2000

/** Draws one frame of the globe at time `t` (ms since start) on a canvas sized in CSS pixels. */
function drawGlobe(ctx: CanvasRenderingContext2D, w: number, h: number, t: number) {
  const { dots, cities, arcs } = globeScene()
  const r = Math.min(w, h) * (w < 640 ? 0.4 : 0.34)
  const cx = w / 2
  const cy = h * 0.44
  const spin = SPIN_START + t * SPIN_PER_MS
  ctx.clearRect(0, 0, w, h)

  // Atmosphere and the dark body of the sphere.
  const glow = ctx.createRadialGradient(cx, cy, r * 0.85, cx, cy, r * 1.35)
  glow.addColorStop(0, 'rgba(90, 130, 255, 0.28)')
  glow.addColorStop(1, 'rgba(90, 130, 255, 0)')
  ctx.fillStyle = glow
  ctx.fillRect(0, 0, w, h)
  const body = ctx.createRadialGradient(cx - r * 0.3, cy - r * 0.35, r * 0.1, cx, cy, r)
  body.addColorStop(0, '#16244f')
  body.addColorStop(1, '#060b1d')
  ctx.fillStyle = body
  ctx.beginPath()
  ctx.arc(cx, cy, r, 0, Math.PI * 2)
  ctx.fill()

  // Land dots, brighter and larger towards the viewer; drawn in four brightness bands (one fill each).
  const bands: number[][] = [[], [], [], []]
  for (const d of dots) {
    const p = rotate(d, spin, TILT)
    if (p.z <= 0.02) continue
    bands[Math.min(3, Math.floor(p.z * 4))].push(cx + p.x * r, cy - p.y * r, p.z)
  }
  bands.forEach((band, i) => {
    ctx.fillStyle = `rgba(150, 180, 255, ${0.22 + i * 0.22})`
    ctx.beginPath()
    for (let k = 0; k < band.length; k += 3) {
      const size = (0.55 + 0.75 * band[k + 2]) * (r / 260)
      ctx.moveTo(band[k] + size, band[k + 1])
      ctx.arc(band[k], band[k + 1], size, 0, Math.PI * 2)
    }
    ctx.fill()
  })

  // Arcs between exchange cities, one after another: each grows, then fades.
  ctx.lineCap = 'round'
  for (let k = 0; k < arcs.length; k++) {
    const age = (t - k * ARC_EVERY_MS) % (arcs.length * ARC_EVERY_MS)
    if (age < 0 || age > ARC_LIFE_MS) continue
    const grow = Math.min(1, age / ARC_GROW_MS)
    const fade = age < ARC_LIFE_MS - 600 ? 1 : (ARC_LIFE_MS - age) / 600
    const pts = arcs[k]
    const last = Math.max(1, Math.round(grow * (pts.length - 1)))
    ctx.strokeStyle = `rgba(185, 210, 255, ${0.75 * fade})`
    ctx.lineWidth = Math.max(1, r / 220)
    ctx.beginPath()
    let pen = false
    let head: Vec3 | null = null
    for (let i = 0; i <= last; i++) {
      const p = rotate(pts[i], spin, TILT)
      // Hidden behind the sphere: on its far side and inside its outline.
      const hidden = p.z < 0 && p.x * p.x + p.y * p.y < 1
      if (hidden) { pen = false; continue }
      const x = cx + p.x * r
      const y = cy - p.y * r
      if (pen) ctx.lineTo(x, y)
      else ctx.moveTo(x, y)
      pen = true
      head = i === last ? p : head
    }
    ctx.stroke()
    if (head && grow < 1) {
      ctx.fillStyle = `rgba(230, 240, 255, ${fade})`
      ctx.beginPath()
      ctx.arc(cx + head.x * r, cy - head.y * r, Math.max(1.5, r / 140), 0, Math.PI * 2)
      ctx.fill()
    }
  }

  // Exchange cities.
  ctx.fillStyle = 'rgba(235, 242, 255, 0.95)'
  ctx.beginPath()
  for (const c of cities) {
    const p = rotate(c, spin, TILT)
    if (p.z <= 0.05) continue
    const s = Math.max(1.4, r / 150) * (0.6 + 0.4 * p.z)
    ctx.moveTo(cx + p.x * r + s, cy - p.y * r)
    ctx.arc(cx + p.x * r, cy - p.y * r, s, 0, Math.PI * 2)
  }
  ctx.fill()
}

/** The globe on a full-window canvas: animated, or one still frame with reduced motion. Decorative (aria-hidden). */
export default function GlobeCanvas({ still }: { still: boolean }) {
  const ref = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const canvas = ref.current
    const ctx = canvas?.getContext('2d')
    if (!canvas || !ctx) return
    let frame = 0
    const start = performance.now()
    const size = () => {
      const dpr = Math.min(window.devicePixelRatio || 1, 2)
      canvas.width = Math.round(canvas.clientWidth * dpr)
      canvas.height = Math.round(canvas.clientHeight * dpr)
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    }
    const paint = (now: number) => drawGlobe(ctx, canvas.clientWidth, canvas.clientHeight, still ? 1500 : now - start)
    const loop = (now: number) => {
      paint(now)
      frame = requestAnimationFrame(loop)
    }
    size()
    if (still) paint(start)
    else frame = requestAnimationFrame(loop)
    const onResize = () => {
      size()
      if (still) paint(start)
    }
    window.addEventListener('resize', onResize)
    return () => {
      cancelAnimationFrame(frame)
      window.removeEventListener('resize', onResize)
    }
  }, [still])
  return <canvas ref={ref} aria-hidden className="absolute inset-0 size-full" data-testid="globe-canvas" />
}
