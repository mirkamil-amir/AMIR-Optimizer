import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { AppearanceCard, ThemeQuickToggle } from '@/components/theme-controls'
import { ThemeProvider, STORAGE_KEY, applyTheme, readTheme } from './theme'

beforeEach(() => {
  localStorage.clear()
  document.documentElement.className = ''
  delete document.documentElement.dataset.skin
})
afterEach(() => localStorage.clear())

describe('theme', () => {
  it('falls back to safe defaults for missing or invalid saved values', () => {
    expect(readTheme()).toEqual({ mode: 'system', skin: 'graphite' })
    localStorage.setItem(STORAGE_KEY, '{"mode":"neon","skin":"<script>"}')
    expect(readTheme()).toEqual({ mode: 'system', skin: 'graphite' })
    localStorage.setItem(STORAGE_KEY, 'not json')
    expect(readTheme()).toEqual({ mode: 'system', skin: 'graphite' })
  })

  it('applies mode and skin to the document', () => {
    applyTheme('dark', 'ocean')
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(document.documentElement.dataset.skin).toBe('ocean')
    applyTheme('light', 'forest')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    expect(document.documentElement.dataset.skin).toBe('forest')
  })

  it('switches mode and skin from the controls and remembers the choice', async () => {
    const user = userEvent.setup()
    render(<ThemeProvider><ThemeQuickToggle /><AppearanceCard /></ThemeProvider>)
    await user.click(screen.getByRole('radio', { name: /Dark/ }))
    await user.click(screen.getByRole('radio', { name: /Violet/ }))
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    expect(document.documentElement.dataset.skin).toBe('violet')
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY)!)).toEqual({ mode: 'dark', skin: 'violet' })
    // The header toggle cycles light → dark → system.
    await act(async () => { await user.click(screen.getByRole('button', { name: /Theme: Dark/ })) })
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY)!).mode).toBe('system')
  })
})
