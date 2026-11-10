import { describe, expect, it } from 'vitest'
import { INTRO_SEEN_KEY, INTRO_SETTING_KEY, introEnabled, setIntroEnabled, shouldShowIntro } from './intro'

const store = (entries: Record<string, string> = {}) => {
  const m = new Map(Object.entries(entries))
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v), removeItem: (k: string) => void m.delete(k) }
}
const ctx = (over: Partial<Parameters<typeof shouldShowIntro>[0]> = {}) => ({ search: '', webdriver: false, session: store(), local: store(), ...over })

describe('when the globe intro plays', () => {
  it('plays once per session', () => {
    expect(shouldShowIntro(ctx())).toBe(true)
    expect(shouldShowIntro(ctx({ session: store({ [INTRO_SEEN_KEY]: '1' }) }))).toBe(false)
  })

  it('respects the setting of this browser', () => {
    const local = store()
    setIntroEnabled(false, local)
    expect(local.getItem(INTRO_SETTING_KEY)).toBe('off')
    expect(introEnabled(local)).toBe(false)
    expect(shouldShowIntro(ctx({ local }))).toBe(false)
    setIntroEnabled(true, local)
    expect(shouldShowIntro(ctx({ local }))).toBe(true)
  })

  it('is skipped under browser automation and without session storage, unless ?intro=1 asks for it', () => {
    expect(shouldShowIntro(ctx({ webdriver: true }))).toBe(false)
    expect(shouldShowIntro(ctx({ session: null }))).toBe(false)
    expect(shouldShowIntro(ctx({ webdriver: true, search: '?intro=1', session: store({ [INTRO_SEEN_KEY]: '1' }) }))).toBe(true)
  })

  it('is not shown when storage throws', () => {
    const throwing = { getItem: () => { throw new Error('blocked') } }
    expect(shouldShowIntro(ctx({ session: throwing }))).toBe(false)
    expect(introEnabled(throwing)).toBe(true)
  })
})
