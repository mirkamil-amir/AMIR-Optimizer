import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { DataTable, type Column } from './data-table'

type Row = { symbol: string; price: number }
const rows: Row[] = Array.from({ length: 23 }, (_, i) => ({ symbol: `S${String(i + 1).padStart(2, '0')}`, price: 100 - i }))
const columns: Column<Row>[] = [
  { key: 'symbol', header: 'Symbol', sortValue: (r) => r.symbol, cell: (r) => r.symbol },
  { key: 'price', header: 'Price', sortValue: (r) => r.price, cell: (r) => r.price },
]
const bodySymbols = () => within(screen.getAllByRole('rowgroup')[1]).getAllByRole('row').map((r) => r.firstChild?.textContent)

function Table({ data = rows, reset = '' }: { data?: Row[]; reset?: string }) {
  return <DataTable rows={data} columns={columns} rowKey={(r) => r.symbol} initialSort={{ key: 'symbol', dir: 'asc' }} pageSize={10} pageResetKey={reset} />
}

describe('DataTable paging', () => {
  it('pages the sorted rows and says which rows are shown', async () => {
    render(<Table />)
    expect(bodySymbols()).toHaveLength(10)
    expect(screen.getByText('1–10 of 23')).toBeInTheDocument()
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    expect(bodySymbols()).toEqual(['S21', 'S22', 'S23'])
    expect(screen.getByText('21–23 of 23')).toBeInTheDocument()
  })

  it('jumps to the last and back to the first page, with a rows-per-page choice', async () => {
    render(<Table />)
    await userEvent.click(screen.getByRole('button', { name: 'Last page' }))
    expect(screen.getByText('Page 3 of 3')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Next page' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'First page' }))
    expect(bodySymbols()[0]).toBe('S01')
    expect(screen.getByRole('combobox', { name: 'Rows per page' })).toBeInTheDocument()
  })

  it('sorts across all rows, not only the current page, and starts again at page 1', async () => {
    render(<Table />)
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    await userEvent.click(screen.getByRole('button', { name: /Price/ }))   // highest price first
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument()
    expect(bodySymbols()[0]).toBe('S01')
    await userEvent.click(screen.getByRole('button', { name: /Price/ }))   // lowest price first
    expect(bodySymbols()[0]).toBe('S23')
  })

  it('returns to page 1 for a new search', async () => {
    const { rerender } = render(<Table />)
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument()
    rerender(<Table data={rows.slice(0, 15)} reset="S" />)
    expect(screen.getByText('Page 1 of 2')).toBeInTheDocument()
    expect(bodySymbols()[0]).toBe('S01')
  })
})
