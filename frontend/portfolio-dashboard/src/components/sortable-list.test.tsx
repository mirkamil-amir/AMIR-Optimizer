import { useState } from 'react'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { SortableList } from './sortable-list'
import { moveItem } from '@/lib/reorder'

function Demo({ onOrder }: { onOrder?: (o: string[]) => void }) {
  const [items, setItems] = useState(['AAPL', 'MSFT', 'SAP.DE'])
  return (
    <SortableList items={items} getKey={(i) => i} getLabel={(i) => i}
      onMove={(f, t) => { const next = moveItem(items, f, t); setItems(next); onOrder?.(next) }}
      renderItem={(i, handle) => <div>{handle}<span>{i}</span></div>} />
  )
}

const order = () => screen.getAllByRole('listitem').map((li) => li.textContent)

describe('SortableList', () => {
  it('moves the focused row with the arrow keys, keeps the focus on it and announces the new place', async () => {
    const saved: string[][] = []
    render(<Demo onOrder={(o) => saved.push(o)} />)
    const handle = screen.getByRole('button', { name: /Move AAPL \(position 1 of 3\)/ })
    handle.focus()
    await userEvent.keyboard('{ArrowDown}')
    expect(order()).toEqual(['MSFT', 'AAPL', 'SAP.DE'])
    expect(screen.getByRole('button', { name: /Move AAPL \(position 2 of 3\)/ })).toHaveFocus()
    expect(screen.getByText('AAPL moved to position 2 of 3.')).toBeInTheDocument()
    await userEvent.keyboard('{End}')
    expect(order()).toEqual(['MSFT', 'SAP.DE', 'AAPL'])
    await userEvent.keyboard('{ArrowDown}') // already last: nothing happens
    await userEvent.keyboard('{Home}')
    expect(order()).toEqual(['AAPL', 'MSFT', 'SAP.DE'])
    expect(saved).toHaveLength(3)
  })
})
