import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { INTRO_SEEN_KEY, INTRO_TIMING } from '@/lib/intro'
import { IntroGate } from './globe-intro'

const auth: { user: object | null; isLoading: boolean } = { user: { status: 'Approved' }, isLoading: false }
vi.mock('@/lib/auth', () => ({ useAuth: () => auth }))

function renderGate() {
  return render(<IntroGate><button type="button">App</button></IntroGate>)
}

describe('IntroGate', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    window.sessionStorage.clear()
    window.localStorage.clear()
    auth.user = { status: 'Approved' }
    auth.isLoading = false
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('plays for a signed-in user over the inert app until Enter is chosen, then enters it', () => {
    renderGate()
    const intro = screen.getByRole('dialog', { name: 'Matrix is starting' })
    const enter = screen.getByRole('button', { name: 'Enter' })
    expect(enter).toHaveFocus()
    expect(screen.getByText('App').closest('[inert]')).not.toBeNull()
    expect(window.sessionStorage.getItem(INTRO_SEEN_KEY)).toBe('1')

    // No time limit, and clicks elsewhere or other keys do not end it.
    act(() => void vi.advanceTimersByTime(60_000))
    fireEvent.click(intro)
    fireEvent.keyDown(window, { key: 'Escape' })
    expect(intro).toHaveAttribute('data-phase', 'show')

    fireEvent.click(enter)
    expect(intro).toHaveAttribute('data-phase', 'leaving')
    act(() => void vi.advanceTimersByTime(INTRO_TIMING.exitMs))
    expect(screen.queryByTestId('globe-intro')).toBeNull()
    expect(screen.getByText('App').closest('[inert]')).toBeNull()
  })

  it('does not play for signed-out users: the sign-in page is the globe', () => {
    auth.user = null
    renderGate()
    expect(screen.queryByTestId('globe-intro')).toBeNull()
    expect(window.sessionStorage.getItem(INTRO_SEEN_KEY)).toBeNull()
  })

  it('waits for the sign-in check before deciding', () => {
    auth.isLoading = true
    const view = renderGate()
    expect(screen.queryByTestId('globe-intro')).toBeNull()
    auth.isLoading = false
    view.rerender(<IntroGate><button type="button">App</button></IntroGate>)
    expect(screen.getByTestId('globe-intro')).toBeInTheDocument()
  })

  it('does not play again in the same session', () => {
    window.sessionStorage.setItem(INTRO_SEEN_KEY, '1')
    renderGate()
    expect(screen.queryByTestId('globe-intro')).toBeNull()
    expect(screen.getByText('App').closest('[inert]')).toBeNull()
  })
})
