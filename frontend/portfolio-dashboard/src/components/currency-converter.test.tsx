import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { CurrencyConverterCard } from './currency-converter'

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
const calls: string[] = []

beforeEach(() => {
  calls.length = 0
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    calls.push(String(url))
    if (url.includes('/fx/currencies')) return Promise.resolve(ok({ source: 'ECB', description: '', isSimulated: false, currencies: ['EUR', 'USD', 'GBP', 'JPY', 'CHF'], convention: '', maxCarryForwardDays: 4 }))
    if (url.includes('/fx/board')) return Promise.resolve(ok({
      base: 'EUR', source: 'ECB', isSimulated: false, note: 'ECB reference rates, units per one EUR; published once per business day (not live).',
      rows: [
        { quote: 'USD', rate: 1.1, observationDate: '2026-10-15', change1D: 0.002, change1M: -0.01, problem: null },
        { quote: 'GBP', rate: 0.85, observationDate: '2026-10-15', change1D: -0.001, change1M: 0.004, problem: null },
        { quote: 'JPY', rate: 160, observationDate: '2026-10-15', change1D: 0, change1M: 0.02, problem: null },
        { quote: 'CHF', rate: null, observationDate: null, change1D: null, change1M: null, problem: 'No recent rate.' },
      ],
    }))
    if (url.includes('/fx/history')) {
      const to = new URL(url, 'http://x').searchParams.get('to')
      return Promise.resolve(ok({ from: 'EUR', to, source: 'ECB', isSimulated: false, note: '', points: [{ date: '2026-10-14', rate: 1.09 }, { date: '2026-10-15', rate: 1.1 }] }))
    }
    return Promise.resolve(new Response('{}', { status: 404 }))
  }))
})

function renderCard() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={qc}><CurrencyConverterCard base="EUR" /></QueryClientProvider>)
}

describe('CurrencyConverterCard', () => {
  it('converts with the published reference rate and states its date', async () => {
    renderCard()
    // Default: 1,000 USD → EUR = 1000 / 1.1.
    await waitFor(() => expect(screen.getByLabelText('Converted amount')).toHaveTextContent('€909.09'))
    expect(screen.getByText(/1 USD = 0\.909091 EUR · reference rate of/)).toBeInTheDocument()
    expect(screen.getByText(/not live/)).toBeInTheDocument()
  })

  it('swaps the direction and recomputes', async () => {
    const user = userEvent.setup()
    renderCard()
    await waitFor(() => expect(screen.getByLabelText('Converted amount')).toHaveTextContent('€909.09'))
    await user.click(screen.getByRole('button', { name: 'Swap currencies' }))
    expect(screen.getByLabelText('Converted amount')).toHaveTextContent('$1,100.00')
  })

  it('lists the rates against the base and charts the chosen pair', async () => {
    const user = userEvent.setup()
    renderCard()
    const list = await screen.findByRole('list', { name: 'Exchange rates against EUR' })
    expect(within(list).getByRole('button', { name: /USD/ })).toHaveTextContent('1.1')
    expect(within(list).getByRole('button', { name: /CHF/ })).toHaveTextContent('—')     // no rate: no number
    await user.click(within(list).getByRole('button', { name: /JPY/ }))
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=EUR&to=JPY'))).toBe(true))
  })
})
