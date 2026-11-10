/**
 * When the start-up globe plays (D-56): once per browser session (sessionStorage), unless switched off for this browser in
 * Settings › Appearance (localStorage). Browser automation (navigator.webdriver, e.g. Playwright) skips it so tests start
 * in the app; `?intro=1` shows it anyway (for the e2e journey and demos). Storage may be blocked: then it is not shown.
 */
export const INTRO_SETTING_KEY = 'matrix-intro'
export const INTRO_SEEN_KEY = 'matrix-intro-seen'

/** How long the exit into the app takes (the intro itself plays until the user chooses Enter). */
export const INTRO_TIMING = { exitMs: 700, reducedExitMs: 200 } as const

export function introEnabled(storage: Pick<Storage, 'getItem'> | null = safeLocal()): boolean {
  try {
    return storage?.getItem(INTRO_SETTING_KEY) !== 'off'
  } catch {
    return true
  }
}

export function setIntroEnabled(on: boolean, storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeLocal()) {
  try {
    if (on) storage?.removeItem(INTRO_SETTING_KEY)
    else storage?.setItem(INTRO_SETTING_KEY, 'off')
  } catch {
    // storage blocked: the choice cannot be remembered
  }
}

export interface IntroContext {
  search: string
  webdriver: boolean
  session: Pick<Storage, 'getItem'> | null
  local: Pick<Storage, 'getItem'> | null
}

export function shouldShowIntro(ctx: IntroContext = browserContext()): boolean {
  if (new URLSearchParams(ctx.search).get('intro') === '1') return true
  if (ctx.webdriver || !ctx.session) return false
  try {
    if (ctx.session.getItem(INTRO_SEEN_KEY)) return false
  } catch {
    return false
  }
  return introEnabled(ctx.local)
}

/** Remembers for this session that the intro was shown (a reload in the same tab goes straight to the app). */
export function markIntroSeen() {
  try {
    window.sessionStorage.setItem(INTRO_SEEN_KEY, '1')
  } catch {
    // storage blocked
  }
}

export const prefersReducedMotion = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

function safeLocal(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage
  } catch {
    return null
  }
}

function browserContext(): IntroContext {
  if (typeof window === 'undefined') return { search: '', webdriver: true, session: null, local: null }
  let session: Storage | null = null
  try {
    session = window.sessionStorage
  } catch {
    session = null
  }
  return { search: window.location.search, webdriver: !!navigator.webdriver, session, local: safeLocal() }
}
