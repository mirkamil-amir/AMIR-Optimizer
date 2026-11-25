import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SplitRow } from './split-row'

describe('SplitRow', () => {
  afterEach(() => window.localStorage.clear())

  const show = () => render(<SplitRow label="Resize cards" left={<div>Price</div>} right={<div>Accounts</div>} />)

  it('moves the edge with the keyboard, resets it, and remembers it', () => {
    const onParentKey = vi.fn()
    const { container } = render(
      <div onKeyDown={onParentKey}><SplitRow label="Resize cards" left={<div>Price</div>} right={<div>Accounts</div>} /></div>)
    const handle = screen.getByRole('separator', { name: 'Resize cards' })
    expect(handle).toHaveAttribute('aria-valuenow', '50')
    fireEvent.keyDown(handle, { key: 'ArrowRight' })
    fireEvent.keyDown(handle, { key: 'ArrowRight' })
    expect(handle).toHaveAttribute('aria-valuenow', '60')
    expect(onParentKey).not.toHaveBeenCalled() // ←/→ of the panel (switch stock) do not fire
    expect((container.firstChild!.firstChild as HTMLElement).style.getPropertyValue('--split-left')).toBe('0.6fr')
    expect(window.localStorage.getItem('matrix.stockDetail.split')).toBe('0.6')
    fireEvent.doubleClick(handle)
    expect(handle).toHaveAttribute('aria-valuenow', '50')
  })

  it('starts from the remembered split, within the limits', () => {
    window.localStorage.setItem('matrix.stockDetail.split', '0.9')
    show()
    expect(screen.getByRole('separator')).toHaveAttribute('aria-valuenow', '75')
  })

  it('follows a drag across the row', () => {
    show()
    const handle = screen.getByRole('separator')
    const row = handle.parentElement!
    row.getBoundingClientRect = () => ({ left: 100, width: 1000, top: 0, height: 100, right: 1100, bottom: 100, x: 100, y: 0, toJSON: () => ({}) })
    handle.setPointerCapture = vi.fn()
    handle.releasePointerCapture = vi.fn()
    fireEvent.pointerDown(handle, { button: 0, pointerId: 1, clientX: 600 })
    fireEvent.pointerMove(handle, { pointerId: 1, clientX: 400 })
    expect(row.style.getPropertyValue('--split-left')).toBe('0.3fr')
    fireEvent.pointerUp(handle, { pointerId: 1, clientX: 400 })
    expect(handle).toHaveAttribute('aria-valuenow', '30')
  })
})
