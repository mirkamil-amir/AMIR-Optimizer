import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { INTRO_SEEN_KEY, INTRO_TIMING } from '@/lib/intro'
import { IntroGate } from './globe-intro'

const auth = { isLoading: false }
vi.mock('@/lib/auth', () => ({ useAuth: () => auth }))

function renderGate() {
  return render(<IntroGate><button type="button">Sign in</button></IntroGate>)
}

describe('IntroGate', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    window.sessionStorage.clear()
    window.localStorage.clear()
    auth.isLoading = false
  })
  afterEach(() => {
    vi.useRealTimers()
    window.history.replaceState(null, '', '/')
  })

  it('plays over the inert app, then enters it after its minimum time', () => {
    renderGate()
    const intro = screen.getByRole('dialog', { name: 'Matrix is starting' })
    expect(screen.getByRole('button', { name: 'Enter' })).toHaveFocus()
    expect(screen.getByText('Sign in').closest('[inert]')).not.toBeNull()
    expect(window.sessionStorage.getItem(INTRO_SEEN_KEY)).toBe('1')

    act(() => void vi.advanceTimersByTime(INTRO_TIMING.minMs))
    expect(intro).toHaveAttribute('data-phase', 'leaving')
    act(() => void vi.advanceTimersByTime(INTRO_TIMING.exitMs))
    expect(screen.queryByTestId('globe-intro')).toBeNull()
    expect(screen.getByText('Sign in').closest('[inert]')).toBeNull()
  })

  it('waits for the sign-in check, but not beyond its maximum', () => {
    auth.isLoading = true
    renderGate()
    act(() => void vi.advanceTimersByTime(INTRO_TIMING.minMs + 100))
    expect(screen.getByTestId('globe-intro')).toHaveAttribute('data-phase', 'show')
    act(() => void vi.advanceTimersByTime(INTRO_TIMING.maxMs - INTRO_TIMING.minMs - 100))
    expect(screen.getByTestId('globe-intro')).toHaveAttribute('data-phase', 'leaving')
  })

  it('is skipped with the Enter button or the keyboard', () => {
    const first = renderGate()
    fireEvent.click(screen.getByRole('button', { name: 'Enter' }))
    expect(screen.getByTestId('globe-intro')).toHaveAttribute('data-phase', 'leaving')
    first.unmount()

    window.history.replaceState(null, '', '/?intro=1')
    renderGate()
    fireEvent.keyDown(window, { key: 'Escape' })
    expect(screen.getByTestId('globe-intro')).toHaveAttribute('data-phase', 'leaving')
  })

  it('does not play again in the same session', () => {
    window.sessionStorage.setItem(INTRO_SEEN_KEY, '1')
    renderGate()
    expect(screen.queryByTestId('globe-intro')).toBeNull()
    expect(screen.getByText('Sign in').closest('[inert]')).toBeNull()
  })
})
