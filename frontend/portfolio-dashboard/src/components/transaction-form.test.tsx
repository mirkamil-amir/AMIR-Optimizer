import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { TransactionDialog } from './transaction-form'

const record = vi.hoisted(() => ({ mutateAsync: vi.fn(), isPending: false }))
vi.mock('@/lib/queries', () => ({ useRecordTransaction: () => record, useStocks: () => ({ data: undefined }) }))

function open() {
  render(<TransactionDialog open onOpenChange={() => {}} portfolioId="p1" portfolioName="Core" currency="USD" symbols={[]} initialType="Deposit" />)
}

describe('record transaction form', () => {
  beforeEach(() => {
    record.mutateAsync.mockReset().mockResolvedValue({ cashAmount: 500, cashBalanceAfter: 1500, idempotentReplay: false })
  })

  it('leaves the date to the server unless the user picks one', async () => {
    open()
    await userEvent.type(screen.getByLabelText(/^Amount/), '500')
    await userEvent.click(screen.getByRole('button', { name: 'Record' }))
    const { body } = record.mutateAsync.mock.calls[0][0]
    expect(body).toMatchObject({ type: 'Deposit', amount: 500 })
    expect(body.date).toBeUndefined()
  })

  it('sends a date the user chose, with one idempotency key per form', async () => {
    open()
    await userEvent.type(screen.getByLabelText(/^Amount/), '500')
    const date = screen.getByLabelText('Date')
    await userEvent.clear(date)
    await userEvent.type(date, '2026-10-01')
    await userEvent.click(screen.getByRole('button', { name: 'Record' }))
    const call = record.mutateAsync.mock.calls[0][0]
    expect(call.body.date).toBe('2026-10-01')
    expect(call.idempotencyKey).toMatch(/.{8,}/)
  })
})
