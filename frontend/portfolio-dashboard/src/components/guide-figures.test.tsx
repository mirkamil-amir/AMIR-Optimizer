import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { GuideFigure, TwoStockFrontier } from './guide-figures'
import { strategyGuides } from '@/lib/strategy-guide'

describe('guide figures', () => {
  it('gives every guide at least one figure, each marked as sample data', () => {
    for (const g of strategyGuides) {
      const { container, unmount } = render(<><GuideFigure guide={g.id} section="theory" /><GuideFigure guide={g.id} section="example" /></>)
      const figures = container.querySelectorAll('figure')
      expect(figures.length, g.id).toBeGreaterThanOrEqual(1)
      figures.forEach((f) => expect(f.textContent).toContain('Sample data – not market data'))
      unmount()
    }
  })

  it('updates the result when a slider moves and says when no portfolio meets the target', () => {
    render(<TwoStockFrontier mode="target" />)
    expect(screen.getByText(/A 50\.0% · B 50\.0%/, { selector: 'strong' })).toBeInTheDocument()
    fireEvent.change(screen.getByRole('slider', { name: /Required return/ }), { target: { value: '0.125' } })
    expect(screen.getByText(/no long-only mix of the two stocks meets this/)).toBeInTheDocument()
  })
})
