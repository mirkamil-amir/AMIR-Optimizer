import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

export type ThemeMode = 'light' | 'dark' | 'system'
export type Skin = 'graphite' | 'navy' | 'midnight' | 'slate' | 'ocean' | 'teal' | 'emerald' | 'burgundy' | 'amber' | 'violet'

/** Colour skins; `sidebar` tells whether the skin uses a dark sidebar in light mode. */
export const skins: { value: Skin; label: string; description: string; swatch: string; accent: string; sidebar: 'light' | 'dark' }[] = [
  { value: 'navy', label: 'Navy & Gold', description: 'Private-bank classic', swatch: '#1c2f5e', accent: '#d9b25a', sidebar: 'dark' },
  { value: 'midnight', label: 'Midnight', description: 'Institutional terminal', swatch: '#23262d', accent: '#e0b94e', sidebar: 'dark' },
  { value: 'graphite', label: 'Graphite', description: 'Neutral and minimal', swatch: '#2b2d33', accent: '#d9b25a', sidebar: 'light' },
  { value: 'slate', label: 'Slate', description: 'Calm blue-grey', swatch: '#4a5d7a', accent: '#a9c6e8', sidebar: 'light' },
  { value: 'ocean', label: 'Ocean', description: 'Corporate blue', swatch: '#1f5fbf', accent: '#e8c45c', sidebar: 'light' },
  { value: 'teal', label: 'Teal', description: 'Modern and clear', swatch: '#1d6f73', accent: '#e9cf6a', sidebar: 'light' },
  { value: 'emerald', label: 'Emerald', description: 'Wealth green', swatch: '#1f6b4e', accent: '#d9b25a', sidebar: 'light' },
  { value: 'burgundy', label: 'Burgundy', description: 'Heritage private office', swatch: '#6e1f2b', accent: '#d6ad5b', sidebar: 'light' },
  { value: 'amber', label: 'Bronze', description: 'Warm and confident', swatch: '#a55a12', accent: '#f1d88a', sidebar: 'light' },
  { value: 'violet', label: 'Violet', description: 'Contemporary', swatch: '#5b3cc4', accent: '#e8c45c', sidebar: 'light' },
]

/** Skins renamed in earlier versions keep working. */
const aliases: Record<string, Skin> = { forest: 'emerald' }

export const STORAGE_KEY = 'amir-theme'
const defaults = { mode: 'system' as ThemeMode, skin: 'navy' as Skin }

/** Reads the saved choice (per browser; a convenience only – falls back to the defaults when storage is unavailable). */
export function readTheme(): { mode: ThemeMode; skin: Skin } {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return defaults
    const v = JSON.parse(raw) as Partial<{ mode: ThemeMode; skin: Skin }>
    return {
      mode: v.mode === 'light' || v.mode === 'dark' || v.mode === 'system' ? v.mode : defaults.mode,
      skin: skins.some((s) => s.value === v.skin) ? v.skin! : (v.skin && aliases[v.skin]) || defaults.skin,
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
