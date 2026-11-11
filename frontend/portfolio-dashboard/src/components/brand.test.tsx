import { act, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BRAND, MatrixMark } from './brand'
import { MatrixLoader, WorkingPanel } from './matrix-loader'

/** Where the mark's dots are and how large they are. */
const shape = (svg: Element) => [...svg.querySelectorAll('circle')].map((c) => `${c.getAttribute('cx')},${c.getAttribute('cy')},${c.getAttribute('r')}`)
/** Lets the mark's one-time arrival finish. */
const arrive = () => act(() => { vi.advanceTimersByTime(4000) })

describe('Matrix mark', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('is named Matrix', () => {
    expect(BRAND.name).toBe('Matrix')
    render(<MatrixMark />)
    expect(screen.getByRole('img', { name: 'Matrix' })).toBeInTheDocument()
  })

  it('is the 3 × 3 dot grid without a frame; its dots fly in once, then stay', () => {
    render(<MatrixMark title="Grid" intro={false} />)
    expect(screen.getByRole('img', { name: 'Grid' }).querySelectorAll('circle')).toHaveLength(9)
  })

  it('has no frame, and its dots fly in once, then stay', () => {
    render(<MatrixMark title="Arriving" />)
    const logo = screen.getByRole('img', { name: 'Arriving' })
    expect(logo.querySelector('rect')).toBeNull() // no tile: the dots stand on the app's background
    const home = [...logo.querySelectorAll('circle')].map((c) => `${Number(c.dataset.x).toFixed(2)},${Number(c.dataset.y).toFixed(2)},${Number(c.dataset.r).toFixed(2)}`)
    expect(shape(logo).filter((s, i) => s !== home[i]).length).toBeGreaterThanOrEqual(5) // on their way in
    arrive()
    expect(shape(logo).map((s) => s.split(',').map(Number).map((v) => v.toFixed(2)).join(','))).toEqual(home)
    const after = shape(logo)
    act(() => { vi.advanceTimersByTime(5000) })
    expect(shape(logo)).toEqual(after) // only once
  })

  it('stays still while the app works – the dot wave shows that instead', () => {
    render(<><MatrixMark title="Shell logo" /><MatrixLoader label="Solving" /><WorkingPanel title="Downloading prices" percent={40} /></>)
    const logo = screen.getByRole('img', { name: 'Shell logo' })
    arrive()
    const still = shape(logo)
    act(() => { vi.advanceTimersByTime(3000) })
    expect(shape(logo)).toEqual(still)
    expect(screen.getAllByTestId('dot-wave')).toHaveLength(2) // the button's loader and the panel's
  })
})

describe('Dot wave', () => {
  it('is a 3 × 3 grid of dots whose light sweeps diagonally', () => {
    render(<MatrixLoader label="Solving" />)
    const wave = screen.getByRole('img', { name: 'Solving' })
    expect(wave).toHaveClass('dot-wave')
    const delays = [...wave.querySelectorAll('circle')].map((c) => parseFloat((c as SVGCircleElement).style.animationDelay))
    expect(delays).toHaveLength(9)
    // Top-left first, bottom-right last; dots on the same diagonal light together.
    expect(delays[0]).toBe(0)
    expect(delays[8]).toBeCloseTo(0.56, 5)
    expect(delays[1]).toBe(delays[3])
    expect(delays[2]).toBe(delays[4])
    expect(delays[4]).toBe(delays[6])
  })

  it('is hidden from assistive technology without a label', () => {
    const { container } = render(<MatrixLoader />)
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true')
  })
})
