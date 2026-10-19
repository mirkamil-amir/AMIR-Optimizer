import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ReportingCurrencySelect } from './currency'

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(ok({ source: 'ECB', description: '', isSimulated: false,
    currencies: ['BRL', 'EUR', 'TRY', 'USD', 'ZAR'], convention: '', maxCarryForwardDays: 4 }))))
})

describe('ReportingCurrencySelect', () => {
  it('lists every supported currency with its name, the portfolio currency first and marked', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={qc}><ReportingCurrencySelect value={undefined} accountCurrency="EUR" onChange={onChange} /></QueryClientProvider>)
    const trigger = screen.getByRole('combobox', { name: 'Show values in currency' })
    expect(trigger).toHaveTextContent('EUR (portfolio)')
    await user.click(trigger)
    await waitFor(() => expect(screen.getAllByRole('option').map((o) => o.textContent))
      .toEqual(['EUREuro (portfolio)', 'USDUS Dollar', 'TRYTurkish Lira', 'BRLBrazilian Real', 'ZARSouth African Rand']))
    await user.click(screen.getByRole('option', { name: /^TRY/ }))
    expect(onChange).toHaveBeenCalledWith('TRY')
  })
})
