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
    if (url.includes('/fx/board')) {
      // Reference rates per one EUR; any other base is derived from them (CHF has no recent rate).
      const perEur: Record<string, number | null> = { EUR: 1, USD: 1.1, GBP: 0.85, JPY: 160, CHF: null, TRY: 45, ZAR: 20 }
      const base = new URL(url, 'http://x').searchParams.get('base')!
      const rows = Object.keys(perEur).filter((q) => q !== base).map((q) => {
        const rate = perEur[q] === null || perEur[base] === null ? null : perEur[q]! / perEur[base]!
        return rate === null
          ? { quote: q, rate: null, observationDate: null, change1D: null, change1M: null, problem: 'No recent rate.' }
          : { quote: q, rate, observationDate: '2026-10-15', change1D: 0.001, change1M: 0.01, problem: null }
      })
      return Promise.resolve(ok({ base, source: 'ECB', isSimulated: false, note: 'Reference rates, units per one EUR; published once per business day (not live).', rows }))
    }
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
    // The rate used and its date are in the result's tooltip, not on a separate line.
    expect(screen.getByLabelText('Converted amount')).toHaveAttribute('title', expect.stringMatching(/^1 USD = 0\.909091 EUR · reference rate of/))
    expect(screen.queryByText(/reference rate of/)).toBeNull()
    // The rate basis is in the title's tip, not on the card.
    expect(screen.queryByText(/not live/)).toBeNull()
    await userEvent.hover(screen.getByRole('button', { name: 'Currency converter' }))
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

  it('lists the rates against the From currency and charts From → To; a row becomes the To currency', async () => {
    const user = userEvent.setup()
    renderCard()
    // Default pair USD → EUR: the list is against USD, the chart shows EUR per USD.
    const list = await screen.findByRole('list', { name: 'Exchange rates against USD' })
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=USD&to=EUR'))).toBe(true))
    expect(screen.queryByRole('combobox', { name: 'Base currency of the rates' })).toBeNull()
    expect(screen.queryByRole('combobox', { name: 'Currency shown in the chart' })).toBeNull()
    await user.click(within(list).getByRole('button', { name: /JPY/ }))
    expect(screen.getByRole('combobox', { name: 'Convert to' })).toHaveTextContent('JPY')
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=USD&to=JPY'))).toBe(true))
  })

  it('follows the From box: list and chart switch to the new currency', async () => {
    const user = userEvent.setup()
    renderCard()
    await screen.findByRole('list', { name: 'Exchange rates against USD' })
    await user.click(screen.getByRole('combobox', { name: 'Convert from' }))
    await user.click(await screen.findByRole('option', { name: /^GBP/ }))
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/board?base=GBP'))).toBe(true))
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=GBP&to=EUR'))).toBe(true))
    const list = await screen.findByRole('list', { name: 'Exchange rates against GBP' })
    expect(within(list).queryByRole('button', { name: /^GBP/ })).toBeNull()
  })

  it('never offers the From currency as To; choosing it as From moves To to the first listed currency and charts the pair', async () => {
    const user = userEvent.setup()
    renderCard()
    await waitFor(() => expect(screen.getByLabelText('Converted amount')).toHaveTextContent('€909.09'))
    // USD is From, so the To list leaves it out.
    await user.click(screen.getByRole('combobox', { name: 'Convert to' }))
    await screen.findAllByRole('option', { name: /^GBP/ })
    expect(screen.queryAllByRole('option', { name: /^USD/ })).toHaveLength(0)
    await user.keyboard('{Escape}')
    // From = EUR (the current To): To becomes the first currency of the list against EUR, and its chart loads by itself.
    await user.click(screen.getByRole('combobox', { name: 'Convert from' }))
    await user.click(await screen.findByRole('option', { name: /^EUR/ }))
    const list = await screen.findByRole('list', { name: 'Exchange rates against EUR' })
    const first = within(list).getAllByRole('button')[0].textContent!.slice(0, 3)
    expect(screen.getByRole('combobox', { name: 'Convert to' })).toHaveTextContent(first)
    await waitFor(() => expect(calls.some((c) => c.includes(`/fx/history?from=EUR&to=${first}`))).toBe(true))
  })

  it('loads every currency in one board request; adding, removing and changing To need no further board request', async () => {
    const user = userEvent.setup()
    renderCard()
    await waitFor(() => expect(screen.getByLabelText('Converted amount')).toHaveTextContent('€909.09'))
    const boards = () => calls.filter((c) => c.includes('/fx/board'))
    expect(boards()).toEqual(['/api/v1/fx/board?base=USD&quotes=CHF,EUR,GBP,JPY,TRY,ZAR'])
    const list = screen.getByRole('list', { name: 'Exchange rates against USD' })
    expect(within(list).getByRole('button', { name: /^TRY/ })).toHaveTextContent('40.9091')       // TRY is shown by default
    await user.click(screen.getByRole('combobox', { name: 'Add a currency to the rates' }))
    await user.click(await screen.findByRole('option', { name: /^ZAR.*South African Rand/ }))
    expect(within(list).getByRole('button', { name: /^ZAR/ })).toHaveTextContent('18.1818')       // immediately, from the loaded board
    await waitFor(() => expect(calls.some((c) => c.includes('/fx/history?from=USD&to=ZAR'))).toBe(true))
    await user.click(screen.getByRole('button', { name: 'Remove ZAR' }))
    expect(within(list).queryByRole('button', { name: /^ZAR/ })).toBeNull()
    // Changing the To currency converts from the same board.
    await user.click(screen.getByRole('combobox', { name: 'Convert to' }))
    expect(await screen.findByRole('option', { name: /TRY.*Turkish Lira/ })).toBeInTheDocument()
    await user.click(screen.getByRole('option', { name: /^TRY/ }))
    expect(screen.getByLabelText('Converted amount')).toHaveTextContent(/40,909\.09/)
    expect(boards()).toHaveLength(1)
  })

  it('names BGN as no longer published and never offers it for conversion', async () => {
    const user = userEvent.setup()
    renderCard()
    await screen.findByRole('list', { name: 'Exchange rates against USD' })
    await user.hover(screen.getByRole('button', { name: 'Currency converter' }))
    expect(await screen.findByText(/No longer published: BGN \(until/)).toBeInTheDocument()
    await user.unhover(screen.getByRole('button', { name: 'Currency converter' }))
    expect(within(screen.getByRole('list', { name: 'Exchange rates against USD' })).queryByRole('button', { name: /^BGN/ })).toBeNull()
    await user.click(screen.getByRole('combobox', { name: 'Convert from' }))
    await screen.findAllByRole('option', { name: /^USD/ })
    expect(screen.queryAllByRole('option', { name: /^BGN/ })).toHaveLength(0)
  })

  it('keeps the list when the chart request fails and offers a retry', async () => {
    const user = userEvent.setup()
    const original = globalThis.fetch
    vi.stubGlobal('fetch', vi.fn((url: string) => (String(url).includes('/fx/history') ? Promise.resolve(new Response('{"title":"down"}', { status: 503 })) : original(url))))
    renderCard()
    const list = await screen.findByRole('list', { name: 'Exchange rates against USD' })
    expect(await screen.findByRole('button', { name: /retry/i })).toBeInTheDocument()
    expect(within(list).getByRole('button', { name: /^GBP/ })).toHaveTextContent(/\d/)
    await user.click(screen.getByRole('button', { name: /retry/i }))
  })
})
