import { describe, expect, it } from 'vitest'
import { cameraFor, frameFor, MAX_ZOOM, project, sameCamera, targetCountries, toMapBox, transitionMs, WORLD } from './map-camera'
import { venueByCountry } from './markets'

const W = 1200
const H = 600

/** Pixel box of the framed region under the camera, to check it is fully visible. */
function visible(countries: string[]) {
  const box = frameFor(countries)!
  const camera = cameraFor(box, W, H)
  const [x0, y0] = project(box.x0 - 180, 84 - box.y0, camera, W, H)
  const [x1, y1] = project(box.x1 - 180, 84 - box.y1, camera, W, H)
  return { camera, x0, y0, x1, y1 }
}

describe('map camera', () => {
  it('frames one market by its region: a German listing brings Europe into view', () => {
    const de = frameFor(['DE'])!
    expect(de).toEqual(toMapBox(venueByCountry.get('DE')!.frame))
    const { camera, x0, x1, y0, y1 } = visible(['DE'])
    expect(camera.k).toBeGreaterThan(2)
    for (const [x, y] of [[x0, y0], [x1, y1]]) {
      expect(x).toBeGreaterThanOrEqual(0); expect(x).toBeLessThanOrEqual(W)
      expect(y).toBeGreaterThanOrEqual(0); expect(y).toBeLessThanOrEqual(H)
    }
    // Frankfurt is near the middle of the view.
    const [fx] = project(8.68, 50.11, camera, W, H)
    expect(Math.abs(fx - W / 2)).toBeLessThan(W * 0.15)
  })

  it('frames several stocks of one country like one (duplicates do not change the frame)', () => {
    expect(frameFor(['US', 'US', 'US'])).toEqual(frameFor(['US']))
    expect(cameraFor(frameFor(['JP', 'JP']), W, H)).toEqual(cameraFor(frameFor(['JP']), W, H))
  })

  it('frames several countries of one region together (Germany, France, UK = Europe)', () => {
    expect(frameFor(['DE', 'FR', 'GB'])).toEqual(frameFor(['DE']))
  })

  it('zooms out for markets on several continents, keeping all of them in view', () => {
    const europe = cameraFor(frameFor(['DE']), W, H)
    const global = visible(['US', 'DE', 'JP'])
    expect(global.camera.k).toBeLessThan(europe.k)
    expect(global.x0).toBeGreaterThanOrEqual(0)
    expect(global.x1).toBeLessThanOrEqual(W)
    for (const c of ['US', 'DE', 'JP']) {
      const v = venueByCountry.get(c)!
      const [x, y] = project(v.lon, v.lat, global.camera, W, H)
      expect(x).toBeGreaterThan(0); expect(x).toBeLessThan(W)
      expect(y).toBeGreaterThan(0); expect(y).toBeLessThan(H)
    }
  })

  it('shows the world for no markets or unknown ones, and never zooms beyond the limits', () => {
    expect(frameFor([])).toBeNull()
    expect(frameFor(['XX', 'ZZ'])).toBeNull()
    expect(frameFor(['XX', 'JP'])).toEqual(frameFor(['JP']))
    expect(cameraFor(null, W, H)).toEqual(WORLD)
    expect(cameraFor(frameFor(['DE']), 0, 0)).toEqual(WORLD)
    for (const c of venueByCountry.keys()) {
      const k = cameraFor(frameFor([c]), W, H).k
      expect(k).toBeGreaterThanOrEqual(1)
      expect(k).toBeLessThanOrEqual(MAX_ZOOM)
    }
    expect(cameraFor(frameFor([...venueByCountry.keys()]), W, H).k).toBeLessThan(1.6)
  })

  it('adapts to the viewport: a narrow phone screen still contains the framed region', () => {
    for (const [w, h] of [[390, 700], [820, 1000], [1600, 700]]) {
      const box = frameFor(['GB'])!
      const camera = cameraFor(box, w, h)
      const [x0] = project(box.x0 - 180, 0, camera, w, h)
      const [x1] = project(box.x1 - 180, 0, camera, w, h)
      expect(x0).toBeGreaterThanOrEqual(-1)
      expect(x1).toBeLessThanOrEqual(w + 1)
    }
  })

  it('times moves by distance: local adjustments are quick, intercontinental moves calm, within 450–1400 ms', () => {
    const europe = cameraFor(frameFor(['DE']), W, H)
    const europePlus = cameraFor(frameFor(['DE', 'IT']), W, H)
    const japan = cameraFor(frameFor(['JP']), W, H)
    expect(transitionMs(europe, europePlus, W, H)).toBe(450)
    const far = transitionMs(europe, japan, W, H)
    expect(far).toBeGreaterThan(900)
    expect(far).toBeLessThanOrEqual(1400)
    expect(transitionMs(WORLD, europe, W, H)).toBeGreaterThan(450)
    expect(sameCamera(europe, { ...europe, tx: europe.tx + 0.1 })).toBe(true)
  })

  it('chooses the target from the viewing mode', () => {
    expect(targetCountries('auto', ['US', 'DE'], null)).toEqual(['US', 'DE'])
    expect(targetCountries('auto', ['US'], 'JP')).toEqual(['US', 'JP'])
    expect(targetCountries('auto', ['US', 'JP'], 'JP')).toEqual(['US', 'JP'])
    expect(targetCountries('world', ['US', 'DE'], 'DE')).toEqual([])
    expect(targetCountries('IN', ['US', 'DE'], null)).toEqual(['IN'])
  })
})
