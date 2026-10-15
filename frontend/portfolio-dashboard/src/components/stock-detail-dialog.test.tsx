import { useState } from 'react'
import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { SelectionProvider } from '@/lib/selection'
import { stockPosition } from '@/lib/stock-navigation'
import type { CompanyProfile, PriceHistory, StockListItem, StockQuote } from '@/lib/types'
import { StockDetailDialog } from './stock-detail-dialog'

// ------------------------------------------------------------------ fixtures

const quote = (symbol: string, price: number): StockQuote => ({
  symbol, price, currency: 'USD', kind: 'LastClose', priceDate: '2026-10-14', asOfUtc: null, previousClose: price - 1,
  previousCloseDate: '2026-10-13', change: 1, changePercent: 1 / (price - 1), provider: 'Mock', retrievedAtUtc: '2026-10-15T00:00:00Z',
  isSimulated: true, isStale: false, isCached: false, description: 'SIMULATED daily close',
})

const item = (symbol: string, name: string, price: number): StockListItem => ({
  id: price, symbol, name, sector: 'Information Technology', industry: 'Software', exchange: 'NASDAQ', country: 'US', indices: ['SP500'],
  currency: 'USD', assetType: 'Equity', isTracked: true, observations: 300, firstDate: '2025-08-01', lastDate: '2026-10-14', quote: quote(symbol, price),
})

const ITEMS = [item('AAPL', 'Apple Inc.', 200), item('MSFT', 'Microsoft', 400), item('NVDA', 'Nvidia', 150)]

const profile = (symbol: string, overrides: Partial<CompanyProfile> = {}): CompanyProfile => ({
  symbol, status: 'Available', description: `${symbol} description text from the source.`, sourceName: 'Wikipedia',
  sourceUrl: `https://en.wikipedia.org/wiki/${symbol}`, licence: 'CC BY-SA 4.0', language: 'en', retrievedAtUtc: '2026-10-15T00:00:00Z',
  isStale: false, isSimulated: false, message: null, ...overrides,
})

const history = (symbol: string): PriceHistory => ({
  symbol, name: symbol, currency: 'USD', exchange: 'NASDAQ', range: '1Y', supported: true, basis: 'Adjusted close',
  points: [{ date: '2025-10-14', price: 100 }, { date: '2026-10-14', price: 110 }], baseDate: '2025-10-14', basePrice: 100,
  endDate: '2026-10-14', endPrice: 110, change: 0.1, availableFrom: '2025-08-01', limitation: null, source: 'Mock', isSimulated: true,
  lastRetrievedAtUtc: '2026-10-15T00:00:00Z', supportedRanges: ['1M', '3M', '6M', '1Y', '5Y', 'MAX'],
})

// ------------------------------------------------------------------ fetch stub routed by URL

type Handler = (url: string) => Promise<Response> | Response
let route: Handler
const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
const fail = (status: number) => new Response(JSON.stringify({ title: 'Provider failed', detail: 'Simulated failure' }), { status })

function defaultRoute(url: string): Response {
  const symbol = decodeURIComponent(url.split('/stocks/')[1]?.split(/[/?]/)[0] ?? '')
  if (url.includes('/profile')) return ok(profile(symbol))
  if (url.includes('/history')) return ok(history(symbol))
  if (url.includes('/watchlist')) return ok({ items: [], range: '1M', maxEntries: 100, note: '' })
  const i = ITEMS.find((x) => x.symbol === symbol)
  if (i) return ok({ stock: i, metadataSource: 'test', metadataUpdatedAtUtc: null, hasAdjustedCloses: true, dataSource: 'Mock', isin: null, mic: 'XNAS' })
  return fail(404)
}

beforeEach(() => {
  route = defaultRoute
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(route(String(url)))))
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
})
afterEach(() => vi.unstubAllGlobals())

// ------------------------------------------------------------------ harness: the list page's state

function Harness({ items = ITEMS, initial = 'AAPL', onClose }: { items?: StockListItem[]; initial?: string | null; onClose?: () => void }) {
  const [symbol, setSymbol] = useState<string | null>(initial)
  return (
    <>
      <p data-testid="list">{items.map((i) => i.symbol).join(',')}</p>
      <StockDetailDialog symbol={symbol} items={items} markets={undefined} onNavigate={setSymbol} onClose={() => { setSymbol(null); onClose?.() }} />
    </>
  )
}

function renderPanel(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <TooltipProvider>
        <SelectionProvider>
          <MemoryRouter>{ui}</MemoryRouter>
        </SelectionProvider>
      </TooltipProvider>
    </QueryClientProvider>,
  )
}

const dialog = () => screen.getByRole('dialog')
const button = (name: RegExp) => within(dialog()).getByRole('button', { name })

// ------------------------------------------------------------------ tests

describe('stockPosition', () => {
  it('follows the list order without wraparound', () => {
    expect(stockPosition(['A', 'B', 'C'], 'A')).toEqual({ index: 0, total: 3, previous: null, next: 'B' })
    expect(stockPosition(['A', 'B', 'C'], 'C')).toEqual({ index: 2, total: 3, previous: 'B', next: null })
    expect(stockPosition(['A', 'B', 'C'], 'X')).toEqual({ index: -1, total: 3, previous: null, next: null })
  })
})

describe('StockDetailDialog', () => {
  it('opens as a large panel with the selected company, ticker and position', async () => {
    renderPanel(<Harness />)
    expect(within(dialog()).getByRole('heading', { name: /Apple Inc\. · AAPL/ })).toBeInTheDocument()
    expect(dialog().className).toContain('lg:w-[min(68vw,1280px)]')
    expect(dialog().className).toContain('lg:h-[min(88vh,960px)]')
    expect(within(dialog()).getByText('1 of 3 in the current list')).toBeInTheDocument()
    expect(await within(dialog()).findByText('AAPL description text from the source.')).toBeInTheDocument()
  })

  it('Previous is disabled on the first stock; Next moves through the list and updates every section', async () => {
    const user = userEvent.setup()
    renderPanel(<Harness />)
    expect(button(/Previous/)).toBeDisabled()
    await user.click(button(/Next/))
    expect(within(dialog()).getByRole('heading', { name: /Microsoft · MSFT/ })).toBeInTheDocument()
    expect(await within(dialog()).findByText('MSFT description text from the source.')).toBeInTheDocument()
    expect(within(dialog()).queryByText('AAPL description text from the source.')).not.toBeInTheDocument()
    expect(within(dialog()).getByText('$400.00')).toBeInTheDocument()
    await user.click(button(/Next/))
    expect(within(dialog()).getByText('3 of 3 in the current list')).toBeInTheDocument()
    expect(button(/Next/)).toBeDisabled()
    await user.click(button(/Previous/))
    expect(within(dialog()).getByRole('heading', { name: /Microsoft · MSFT/ })).toBeInTheDocument()
  })

  it('Close and Escape close the panel without touching the list', async () => {
    const user = userEvent.setup()
    const onClose = vi.fn()
    renderPanel(<Harness onClose={onClose} />)
    await user.click(button(/Close/))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(onClose).toHaveBeenCalledTimes(1)
    expect(screen.getByTestId('list')).toHaveTextContent('AAPL,MSFT,NVDA')

    renderPanel(<Harness initial="MSFT" onClose={onClose} />)
    await user.keyboard('{Escape}')
    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(2))
  })

  it('follows the filtered list and disables navigation for a stock that left it', async () => {
    renderPanel(<Harness items={[ITEMS[0], ITEMS[2]]} initial="MSFT" />)
    expect(within(dialog()).getByText('No longer in the current list')).toBeInTheDocument()
    expect(button(/Previous/)).toBeDisabled()
    expect(button(/Next/)).toBeDisabled()
  })

  it('shows an explicit fallback when no description is available', async () => {
    route = (url) => url.includes('/profile')
      ? ok(profile('AAPL', { status: 'NotFound', description: null, message: 'Company description is currently unavailable: no article text.' }))
      : defaultRoute(url)
    renderPanel(<Harness />)
    expect(await within(dialog()).findByText('Company description is currently unavailable.')).toBeInTheDocument()
    expect(within(dialog()).getByText(/no article text/)).toBeInTheDocument()
  })

  it('a failed description request does not break the chart, and a failed chart does not hide the description', async () => {
    route = (url) => (url.includes('/profile') ? fail(503) : defaultRoute(url))
    const first = renderPanel(<Harness />)
    expect(await within(dialog()).findByText(/Change 1Y:/)).toBeInTheDocument()
    expect(within(dialog()).getByText('Something went wrong')).toBeInTheDocument()
    first.unmount()

    route = (url) => (url.includes('/history') ? fail(500) : defaultRoute(url))
    renderPanel(<Harness />)
    expect(await within(dialog()).findByText('AAPL description text from the source.')).toBeInTheDocument()
    expect(within(dialog()).getByText('Something went wrong')).toBeInTheDocument()
  })

  it('a slow response for the previous stock never replaces the current stock\'s data', async () => {
    const user = userEvent.setup()
    let releaseApple!: () => void
    const appleGate = new Promise<void>((r) => { releaseApple = r })
    route = async (url) => {
      if (url.includes('/stocks/AAPL/profile')) { await appleGate; return ok(profile('AAPL')) }
      return defaultRoute(url)
    }
    renderPanel(<Harness />)
    await user.click(button(/Next/))
    expect(await within(dialog()).findByText('MSFT description text from the source.')).toBeInTheDocument()
    await act(async () => { releaseApple(); await appleGate })
    expect(within(dialog()).getByText('MSFT description text from the source.')).toBeInTheDocument()
    expect(within(dialog()).queryByText('AAPL description text from the source.')).not.toBeInTheDocument()
    expect(within(dialog()).getByRole('heading', { name: /Microsoft · MSFT/ })).toBeInTheDocument()
  })
})
