import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { PannableArea } from './pannable-area'

const area = () => {
  render(<PannableArea label="Matrix"><div style={{ width: 4000, height: 4000 }} /></PannableArea>)
  const el = screen.getByRole('region', { name: 'Matrix' })
  el.scrollLeft = 500
  el.scrollTop = 300
  return el
}

describe('PannableArea', () => {
  it('pulls the content with the mouse: dragging left and up scrolls right and down', () => {
    const el = area()
    fireEvent.pointerDown(el, { pointerType: 'mouse', button: 0, clientX: 400, clientY: 400, pointerId: 1 })
    fireEvent.pointerMove(el, { pointerType: 'mouse', clientX: 300, clientY: 350, pointerId: 1 })
    expect(el.scrollLeft).toBe(600)
    expect(el.scrollTop).toBe(350)
    expect(el).toHaveAttribute('data-dragging')
    fireEvent.pointerUp(el, { pointerType: 'mouse', pointerId: 1 })
    expect(el).not.toHaveAttribute('data-dragging')
    fireEvent.pointerMove(el, { pointerType: 'mouse', clientX: 0, clientY: 0, pointerId: 1 })
    expect(el.scrollLeft).toBe(600)
  })

  it('keeps a click a click (tiny moves) and leaves touch scrolling to the browser', () => {
    const el = area()
    fireEvent.pointerDown(el, { pointerType: 'mouse', button: 0, clientX: 400, clientY: 400, pointerId: 1 })
    fireEvent.pointerMove(el, { pointerType: 'mouse', clientX: 402, clientY: 401, pointerId: 1 })
    expect(el.scrollLeft).toBe(500)
    fireEvent.pointerUp(el, { pointerType: 'mouse', pointerId: 1 })
    fireEvent.pointerDown(el, { pointerType: 'touch', button: 0, clientX: 400, clientY: 400, pointerId: 2 })
    fireEvent.pointerMove(el, { pointerType: 'touch', clientX: 100, clientY: 100, pointerId: 2 })
    expect(el.scrollLeft).toBe(500)
  })

  it('is a focusable, named region so keyboard users can scroll it', () => {
    expect(area()).toHaveAttribute('tabindex', '0')
  })
})
