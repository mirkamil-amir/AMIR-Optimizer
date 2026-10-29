import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CorrelationHeatmap } from './charts'
import { cellSize, MATRIX } from '@/lib/matrix-window'

const symbols = ['AAPL', 'MSFT', 'SAP.DE']
const matrix = [[1, 0.5, -0.2], [0.5, 1, 0.1], [-0.2, 0.1, 1]]
const cells = (root: HTMLElement) => root.querySelectorAll('[data-cell]')

describe('CorrelationHeatmap', () => {
  it('draws a small matrix completely in a pannable box and reads out the hovered cell', () => {
    render(<CorrelationHeatmap symbols={symbols} matrix={matrix} />)
    const box = screen.getByRole('region', { name: 'Correlation matrix, scrollable' })
    expect(cells(box)).toHaveLength(9)
    // jsdom has no layout: the box counts as the fallback size, the cell area starts at 0,0.
    const step = cellSize(3, MATRIX.fallback.width) + MATRIX.gap
    const area = box.querySelector('[data-cell]')!.parentElement!
    fireEvent.mouseMove(area, { clientX: 2 * step + 5, clientY: 5 })
    expect(screen.getByText('AAPL · SAP.DE: -0.200')).toBeInTheDocument()
    fireEvent.mouseLeave(area)
    expect(screen.getByText(/3 × 3 · hover a cell/)).toBeInTheDocument()
  })

  it('draws only the visible window of a large matrix', () => {
    const n = 228
    const big = Array.from({ length: n }, (_, i) => Array.from({ length: n }, (_, j) => (i === j ? 1 : 0.3)))
    render(<CorrelationHeatmap symbols={Array.from({ length: n }, (_, i) => `S${i}`)} matrix={big} />)
    const drawn = cells(screen.getByRole('region', { name: 'Correlation matrix, scrollable' })).length
    expect(drawn).toBeGreaterThan(0)
    expect(drawn).toBeLessThan(2000)
  })

  it('opens the same matrix in full screen and closes it again', async () => {
    render(<CorrelationHeatmap symbols={symbols} matrix={matrix} />)
    await userEvent.click(screen.getByRole('button', { name: 'Full screen' }))
    const dialog = await screen.findByRole('dialog', { name: 'Correlation of daily returns' })
    expect(cells(within(dialog).getByRole('region', { name: 'Correlation matrix, full screen' }))).toHaveLength(9)
    await userEvent.keyboard('{Escape}')
    expect(await screen.findByRole('region', { name: 'Correlation matrix, scrollable' })).toBeInTheDocument()
  })
})
