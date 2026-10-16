import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

export type ThemeMode = 'light' | 'dark' | 'system'
export type Skin = 'graphite' | 'ocean' | 'forest' | 'amber' | 'violet'

export const skins: { value: Skin; label: string; swatch: string }[] = [
  { value: 'graphite', label: 'Graphite', swatch: '#262626' },
  { value: 'ocean', label: 'Ocean', swatch: '#1f5fbf' },
  { value: 'forest', label: 'Forest', swatch: '#1d7a4a' },
  { value: 'amber', label: 'Amber', swatch: '#b35c00' },
  { value: 'violet', label: 'Violet', swatch: '#6a3fd1' },
]

export const STORAGE_KEY = 'amir-theme'
const defaults = { mode: 'system' as ThemeMode, skin: 'graphite' as Skin }

/** Reads the saved choice (per browser; a convenience only – falls back to the defaults when storage is unavailable). */
export function readTheme(): { mode: ThemeMode; skin: Skin } {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return defaults
    const v = JSON.parse(raw) as Partial<{ mode: ThemeMode; skin: Skin }>
    return {
      mode: v.mode === 'light' || v.mode === 'dark' || v.mode === 'system' ? v.mode : defaults.mode,
      skin: skins.some((s) => s.value === v.skin) ? v.skin! : defaults.skin,
    }
  } catch {
    return defaults
  }
}

function prefersDark(): boolean {
  try {
    return window.matchMedia('(prefers-color-scheme: dark)').matches
  } catch {
    return false
  }
}

/** Applies mode and skin to <html> (class "dark", data-skin). Pure DOM; also used by the pre-render script's logic. */
export function applyTheme(mode: ThemeMode, skin: Skin, root: HTMLElement = document.documentElement) {
  const dark = mode === 'dark' || (mode === 'system' && prefersDark())
  root.classList.toggle('dark', dark)
  root.dataset.skin = skin
  root.style.colorScheme = dark ? 'dark' : 'light'
}

interface ThemeContextValue {
  mode: ThemeMode
  skin: Skin
  setMode: (m: ThemeMode) => void
  setSkin: (s: Skin) => void
}

const ThemeContext = createContext<ThemeContextValue | null>(null)

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState(readTheme)

  useEffect(() => {
    applyTheme(state.mode, state.skin)
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(state)) } catch { /* storage unavailable: the choice lasts for this page */ }
    if (state.mode !== 'system') return
    let media: MediaQueryList | null = null
    try { media = window.matchMedia('(prefers-color-scheme: dark)') } catch { return }
    const onChange = () => applyTheme('system', state.skin)
    media.addEventListener('change', onChange)
    return () => media?.removeEventListener('change', onChange)
  }, [state])

  const setMode = useCallback((mode: ThemeMode) => setState((s) => ({ ...s, mode })), [])
  const setSkin = useCallback((skin: Skin) => setState((s) => ({ ...s, skin })), [])
  const value = useMemo(() => ({ ...state, setMode, setSkin }), [state, setMode, setSkin])
  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext)
  if (!ctx) throw new Error('useTheme must be used inside <ThemeProvider>')
  return ctx
}
