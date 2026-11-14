import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cubeBalls, GRID_POSE } from '@/lib/cube'
import { ARRIVAL_MS, BRAND, MatrixMark } from './brand'
import { MatrixLoader, WorkingPanel } from './matrix-loader'

/** Where the mark's balls are and how large they are. */
const shape = (svg: Element) => [...svg.querySelectorAll('circle')].map((c) => `${c.getAttribute('cx')},${c.getAttribute('cy')},${c.getAttribute('r')}`).sort()
const grid = cubeBalls(GRID_POSE).map((b) => `${b.x},${b.y},${b.r}`).sort()
const wait = (ms: number) => act(() => { vi.advanceTimersByTime(ms) })

describe('Matrix mark', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => { cleanup(); vi.useRealTimers() })

  it('is named Matrix and is the 3 × 3 grid of 27 balls without a frame', () => {
    expect(BRAND.name).toBe('Matrix')
    render(<MatrixMark intro={false} />)
    const logo = screen.getByRole('img', { name: 'Matrix' })
    expect(logo.querySelectorAll('circle')).toHaveLength(27)
    expect(logo.querySelector('rect')).toBeNull()
    expect(shape(logo)).toEqual(grid)
  })

  it('thinks briefly once on arrival, then stays the grid', () => {
    render(<MatrixMark title="Arriving" />)
    const logo = screen.getByRole('img', { name: 'Arriving' })
    wait(ARRIVAL_MS / 2)
    expect(shape(logo)).not.toEqual(grid)
    wait(4000)
    expect(shape(logo)).toEqual(grid)
    wait(3000)
    expect(shape(logo)).toEqual(grid) // only once
  })

  it('thinks while work is in progress and turns back into the grid when it ends', () => {
    const { rerender } = render(<><MatrixMark title="Shell logo" intro={false} /></>)
    const logo = screen.getByRole('img', { name: 'Shell logo' })
    wait(500)
    expect(shape(logo)).toEqual(grid) // nothing to do: still
    rerender(<><MatrixMark title="Shell logo" intro={false} /><MatrixLoader label="Solving" /><WorkingPanel title="Downloading prices" percent={40} /></>)
    wait(800)
    expect(shape(logo)).not.toEqual(grid) // the app thinks: the logo tumbles
    expect(screen.getAllByTestId('thinking-ball')).toHaveLength(2) // the button's indicator and the panel's
    rerender(<><MatrixMark title="Shell logo" intro={false} /><MatrixLoader label="Solving" /></>)
    wait(800)
    expect(shape(logo)).not.toEqual(grid) // one piece of work is still running
    rerender(<><MatrixMark title="Shell logo" intro={false} /></>)
    wait(3000)
    expect(shape(logo)).toEqual(grid)
  })
})

describe('Thinking ball', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => { cleanup(); vi.useRealTimers() })

  it('opens from the grid into the tumbling ball', () => {
    render(<MatrixLoader label="Solving" />)
    const ball = screen.getByRole('img', { name: 'Solving' })
    wait(1000)
    const a = shape(ball)
    expect(a).not.toEqual(grid)
    wait(300)
    expect(shape(ball)).not.toEqual(a)
  })

  it('is hidden from assistive technology without a label', () => {
    const { container } = render(<MatrixLoader />)
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true')
  })
})
