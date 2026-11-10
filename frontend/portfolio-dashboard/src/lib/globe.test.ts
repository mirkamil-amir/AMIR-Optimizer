import { describe, expect, it } from 'vitest'
import { COUNTRY_PATHS, MAP_HEIGHT, MAP_TOP } from '@/components/world-map-geo'
import { angularDistance, arcPairs, arcPoints, landDots, parseRings, rotate, toVec } from './globe'
import { venues } from './markets'

const close = (a: number, b: number, d = 1e-9) => expect(Math.abs(a - b)).toBeLessThan(d)

describe('globe geometry', () => {
  it('parses closed rings of M/L/Z paths', () => {
    expect(parseRings('M0 0L10 0L10 10Z M20 20L30 20L30 30Z')).toEqual([[[0, 0], [10, 0], [10, 10]], [[20, 20], [30, 20], [30, 30]]])
  })

  it('samples dots inside a square only, evenly spaced', () => {
    // A square from longitude -10 to 10, latitude 0 to 20 (map top 84: y = 84 - latitude).
    const dots = landDots(['M170 64L190 64L190 84L170 84Z'], 84, 142, 2)
    expect(dots.length).toBeGreaterThan(50)
    for (const d of dots) {
      expect(d.lon).toBeGreaterThanOrEqual(-10)
      expect(d.lon).toBeLessThan(10)
      expect(d.lat).toBeGreaterThanOrEqual(0)
      expect(d.lat).toBeLessThanOrEqual(20)
    }
  })

  it('puts the exchange cities of the catalogue on land dots of the real outlines', () => {
    const dots = landDots(Object.values(COUNTRY_PATHS), MAP_TOP, MAP_HEIGHT)
    expect(dots.length).toBeGreaterThan(3000)
    expect(dots.length).toBeLessThan(12000)
    // Every exchange city has a land dot within three degrees (the grid is 1.6°; coastal cities sit at the edge).
    for (const v of venues) expect(Math.min(...dots.map((d) => angularDistance(d, v))), v.city).toBeLessThan(3)
    // The middle of the Atlantic and of the Pacific has none.
    for (const sea of [{ lat: 30, lon: -40 }, { lat: 0, lon: -150 }]) expect(Math.min(...dots.map((d) => angularDistance(d, sea)))).toBeGreaterThan(5)
  })

  it('turns the chosen longitude towards the viewer and tilts the north pole into view', () => {
    const front = rotate(toVec({ lat: 0, lon: 40 }), 40, 0)
    close(front.x, 0)
    close(front.y, 0)
    close(front.z, 1)
    expect(rotate(toVec({ lat: 0, lon: 40 + 180 }), 40, 0).z).toBeLessThan(0) // the far side is hidden
    const pole = rotate(toVec({ lat: 90, lon: 0 }), 0, 20)
    close(pole.z, Math.sin(20 * Math.PI / 180))
    expect(rotate(toVec({ lat: 0, lon: 130 }), 40, 0).x).toBeGreaterThan(0.99) // east of the centre is on the right
  })

  it('draws arcs from city to city, lifted off the surface in the middle', () => {
    const ny = { lat: 40.71, lon: -74.01 }
    const london = { lat: 51.51, lon: -0.13 }
    const pts = arcPoints(ny, london, 20)
    expect(pts).toHaveLength(21)
    const start = toVec(ny)
    const end = toVec(london)
    close(pts[0].x, start.x)
    close(pts[20].z, end.z)
    const mid = Math.hypot(pts[10].x, pts[10].y, pts[10].z)
    expect(mid).toBeGreaterThan(1.05)
    close(angularDistance(ny, london), 50, 1.5) // about 5,570 km
  })

  it('pairs distant cities deterministically', () => {
    const pairs = arcPairs(venues, 12)
    expect(pairs).toHaveLength(12)
    for (const [a, b] of pairs) expect(angularDistance(a, b)).toBeGreaterThanOrEqual(25)
    expect(arcPairs(venues, 12).map(([a, b]) => a.city + b.city)).toEqual(pairs.map(([a, b]) => a.city + b.city))
  })
})
