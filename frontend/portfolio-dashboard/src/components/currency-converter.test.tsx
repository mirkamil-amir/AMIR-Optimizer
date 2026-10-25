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
    if (url.includes('/fx/currencies')) return Promise.resolve(ok({ source: 'ECB', description: '', isSimulated: false, currencies: ['EUR', 'USD', 'GBP', 'JPY', 'CHF', 'TRY', 'ZAR'], convention: '', maxCarryForwardDays: 4,
      discontinued: [{ currency: 'BGN', lastRateDate: '2025-12-31', note: 'The Bulgarian lev was replaced by the euro on 1 Jan 2026 (fixed rate 1 EUR = 1.95583 BGN).' }] }))
    if (url.includes('/fx/board')) return Promise.resolve(ok({
      base: new URL(url, 'http://x').searchParams.get('base'), source: 'ECB', isSimulated: false, note: 'Reference rates, units per one EUR; published once per business day (not live).',
      rows: [
        { quote: 'USD', rate: 1.1, observationDate: '2026-10-15', change1D: 0.002, change1M: -0.01, problem: null },
        { quote: 'GBP', rate: 0.85, observationDate: '2026-10-15', change1D: -0.001, change1M: 0.004, problem: null },
        { quote: 'JPY', rate: 160, observationDate: '2026-10-15', change1D: 0, change1M: 0.02, problem: null },
        { quote: 'CHF', rate: null, observationDate: null, change1D: null, change1M: null, problem: 'No recent rate.' },
        { quote: 'TRY', rate: 45, observationDate: '2026-10-15', change1D: 0.001, change1M: 0.03, problem: null },
        { quote: 'ZAR', rate: 20, observationDate: '2026-10-15', change1D: 0, change1M: 0, problem: null },
      ],
    }))
    if (url.includes('/fx/history')) {
      const q = new URL(url, 'http://x').searchParams
      if (q.get('to') === 'BGN') return Promise.resolve(ok({ from: q.get('from'), to: 'BGN', source: 'ECB', isSimulated: false, note: '', points: [] }))
      return Promise.resolve(ok({ from: q.get('from'), to: q.get('to'), source: 'ECB', isSimulated: false, note: '', points: [{ date: '2026-10-14', rate: 1.09 }, { date: '2026-10-15', rate: 1.1 }] }))
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
    // The rate basis is in the title's tip, not on the card.
    expect(screen.queryByText(/not live/)).toBeNull()
    await userEvent.hover(screen.getByRole('button', { name: 'About these rates' }))
    expect(await screen.findByText(/not live/)).toBeInTheDocument()
    // The data-source name (here ECB) is not shown to users.
    expect(screen.queryByText(/ECB/)).toBeNull()
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

  it('lets the user choose the base of the rates; board and chart follow it', async () => {
    const user = userEvent.setup()
    renderCard()
    await screen.findByRole('list', { name: 'Exchange rates against EUR' })
    await user.click(screen.getByRole('combobox', { name: 'Base currency of the rates' }))
    await user.click(await screen.findByRole('option', { name: /^GBP/ }))
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/board?base=GBP'))).toBe(true))
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=GBP&to='))).toBe(true))
    expect(screen.getByText(/per GBP/)).toBeInTheDocument()
    // The calculator now converts from the chosen base into the portfolio currency.
    expect(screen.getByRole('combobox', { name: 'Convert from' })).toHaveTextContent('GBP')
    expect(screen.getByRole('combobox', { name: 'Convert to' })).toHaveTextContent('EUR')
  })

  it('loads every currency in one board request; adding, removing and converting need no further board request', async () => {
    const user = userEvent.setup()
    renderCard()
    await waitFor(() => expect(screen.getByLabelText('Converted amount')).toHaveTextContent('€909.09'))
    const boards = () => calls.filter((c) => c.includes('/fx/board'))
    expect(boards()).toEqual(['/api/v1/fx/board?base=EUR&quotes=CHF,GBP,JPY,TRY,USD,ZAR'])
    const list = screen.getByRole('list', { name: 'Exchange rates against EUR' })
    expect(within(list).getByRole('button', { name: /^TRY/ })).toHaveTextContent('45')       // TRY is shown by default
    await user.click(screen.getByRole('combobox', { name: 'Add a currency to the rates' }))
    await user.click(await screen.findByRole('option', { name: /^ZAR.*South African Rand/ }))
    expect(within(list).getByRole('button', { name: /^ZAR/ })).toHaveTextContent('20')       // immediately, from the loaded board
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=EUR&to=ZAR'))).toBe(true))
    await user.click(screen.getByRole('button', { name: 'Remove ZAR' }))
    expect(within(list).queryByRole('button', { name: /^ZAR/ })).toBeNull()
    // Any pair converts from the same board: 1,000 ZAR → EUR = 1000 / 20.
    await user.click(screen.getByRole('combobox', { name: 'Convert from' }))
    expect(await screen.findByRole('option', { name: /TRY.*Turkish Lira/ })).toBeInTheDocument()
    await user.click(screen.getByRole('option', { name: /^ZAR/ }))
    expect(screen.getByLabelText('Converted amount')).toHaveTextContent('€50.00')
    expect(boards()).toHaveLength(1)
  })

  it('offers BGN for its history only and explains why there is no current rate', async () => {
    const user = userEvent.setup()
    renderCard()
    await screen.findByRole('list', { name: 'Exchange rates against EUR' })
    await user.hover(screen.getByRole('button', { name: 'About these rates' }))
    expect(await screen.findByText(/No longer published: BGN \(until/)).toBeInTheDocument()
    await user.unhover(screen.getByRole('button', { name: 'About these rates' }))
    // Its published history can be charted, with the reason when the period has none …
    await user.click(screen.getByRole('combobox', { name: 'Currency shown in the chart' }))
    expect(await screen.findByText('History only (no current rate)')).toBeInTheDocument()
    await user.click(await screen.findByRole('option', { name: /^BGN.*Bulgarian Lev/ }))
    expect(await screen.findByText(/No BGN rates in this period\. The Bulgarian lev was replaced by the euro/)).toBeInTheDocument()
    // … but it is neither convertible today nor on the board.
    expect(within(screen.getByRole('list', { name: 'Exchange rates against EUR' })).queryByRole('button', { name: /^BGN/ })).toBeNull()
    await user.click(screen.getByRole('combobox', { name: 'Convert from' }))
    await screen.findAllByRole('option', { name: /^USD/ })
    expect(screen.queryAllByRole('option', { name: /^BGN/ })).toHaveLength(0)
  })

  it('keeps the list when the chart request fails and offers a retry', async () => {
    const user = userEvent.setup()
    const original = globalThis.fetch
    vi.stubGlobal('fetch', vi.fn((url: string) => (String(url).includes('/fx/history') ? Promise.resolve(new Response('{"title":"down"}', { status: 503 })) : original(url))))
    renderCard()
    const list = await screen.findByRole('list', { name: 'Exchange rates against EUR' })
    expect(await screen.findByRole('button', { name: /retry/i })).toBeInTheDocument()
    expect(within(list).getByRole('button', { name: /^USD/ })).toHaveTextContent('1.1')
    await user.click(screen.getByRole('button', { name: /retry/i }))
  })
})
