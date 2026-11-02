import { act, fireEvent, render, screen } from '@testing-library/react'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BRAND, MatrixMark } from './brand'
import { MatrixLoader } from './matrix-loader'

/** The shell's mark next to a button whose loader shows while work runs. */
function Page() {
  const [working, setWorking] = useState(false)
  return (
    <>
      <MatrixMark title="Shell logo" />
      <button type="button" onClick={() => setWorking(!working)}>{working && <MatrixLoader label="Solving" />}Solve</button>
    </>
  )
}
const toggleWork = () => fireEvent.click(screen.getByRole('button', { name: /Solve/ }))

const opacities = (svg: Element) => [...svg.querySelectorAll('circle')].map((c) => c.getAttribute('fill-opacity'))

describe('Matrix mark', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('is named Matrix', () => {
    expect(BRAND.name).toBe('Matrix')
    render(<MatrixMark />)
    expect(screen.getByRole('img', { name: 'Matrix' })).toBeInTheDocument()
  })

  it('stays still until the app calculates, moves while it does, and is still again afterwards', () => {
    render(<Page />)
    const logo = screen.getByRole('img', { name: 'Shell logo' })
    const still = opacities(logo)
    act(() => { vi.advanceTimersByTime(2000) })
    expect(opacities(logo)).toEqual(still)
    expect(logo).not.toHaveClass('matrix-working')

    toggleWork()
    act(() => { vi.advanceTimersByTime(2000) })
    expect(logo).toHaveClass('matrix-working')
    expect(opacities(logo)).not.toEqual(still)
    expect(opacities(screen.getByRole('img', { name: 'Solving' }))).not.toEqual(still)

    toggleWork()
    expect(logo).not.toHaveClass('matrix-working')
    expect(opacities(logo)).toEqual(still)
  })

  it('never moves when asked to stay still', () => {
    render(<><MatrixMark motion="still" title="Preview" /><MatrixLoader /></>)
    const preview = screen.getByRole('img', { name: 'Preview' })
    const still = opacities(preview)
    act(() => { vi.advanceTimersByTime(2000) })
    expect(opacities(preview)).toEqual(still)
  })
})
