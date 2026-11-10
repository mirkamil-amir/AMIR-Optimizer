import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { WireClassificationHistory, WireStory } from '@/lib/types'
import { StoryRow } from './wire'

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
const calls: string[] = []

const story: WireStory = {
  id: 'story-1', headline: 'Microsoft quarterly results top forecasts', url: 'https://news.example.test/msft', publisher: 'Wire One',
  firstPublishedAtUtc: '2026-10-21T10:00:00Z', lastPublishedAtUtc: '2026-10-21T10:00:00Z', scope: 'Company', eventType: 'Results',
  eventTypeLabel: 'results', eventTypeBasis: 'headline mentions “quarterly results”', marketArea: null, region: 'US', isOfficial: false,
  isSimulated: false, summary: null, articleCount: 1,
  sources: [{ articleId: 'a-1', publisher: 'Wire One', url: 'https://news.example.test/msft', publishedAtUtc: '2026-10-21T10:00:00Z', isOfficial: false }],
  assets: [], attention: { level: 'Medium', points: 2, reasons: ['results is a material event type (+2)'], note: 'Not a prediction.' }, why: [], ruleVersion: 2, entities: [],
}

const history: WireClassificationHistory = {
  storyId: 'story-1', currentRuleVersion: 2,
  reports: [{
    articleId: 'a-1', publisher: 'Wire One', publishedAtUtc: '2026-10-21T10:00:00Z',
    classifications: [
      { ruleVersion: 2, category: 'Earnings', categoryLabel: 'earnings', categoryBasis: 'headline mentions “quarterly results”', scope: 'Company',
        eventType: 'Results', eventTypeLabel: 'results', eventTypeBasis: 'headline mentions “quarterly results”', marketArea: null,
        processedAtUtc: '2026-11-10T21:00:00Z', trigger: 'Reclassification', note: 'Provider tags were not stored before 2026-11-08.', isCurrent: true },
      { ruleVersion: 1, category: 'Earnings', categoryLabel: 'earnings', categoryBasis: 'headline mentions “quarterly results”', scope: 'Company',
        eventType: null, eventTypeLabel: null, eventTypeBasis: null, marketArea: null,
        processedAtUtc: '2026-10-21T10:05:00Z', trigger: 'Backfill', note: null, isCurrent: false },
    ],
  }],
  notes: ['Each rules version is kept.'],
}

beforeEach(() => {
  calls.length = 0
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  vi.stubGlobal('fetch', vi.fn((url: string) => {
    calls.push(String(url))
    if (url.includes('/news/stories/story-1/classifications')) return Promise.resolve(ok(history))
    return Promise.resolve(new Response('{}', { status: 404 }))
  }))
})

function renderRow() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={qc}><MemoryRouter><StoryRow story={story} /></MemoryRouter></QueryClientProvider>)
}

describe('StoryRow classification history', () => {
  it('loads the history only on request and lists every rules version, current first', async () => {
    const user = userEvent.setup()
    renderRow()
    await user.click(screen.getByRole('button', { name: 'Wire One' }))
    expect(calls.filter((c) => c.includes('/classifications'))).toHaveLength(0)

    await user.click(screen.getByRole('button', { name: 'Classification history' }))
    expect(await screen.findByText('Rules v2 (current):')).toBeInTheDocument()
    expect(screen.getByText('Rules v1:')).toBeInTheDocument()
    expect(screen.getByText(/reclassified/)).toBeInTheDocument()
    expect(screen.getByText(/recorded when the history began/)).toBeInTheDocument()
    expect(screen.getByText('Provider tags were not stored before 2026-11-08.')).toBeInTheDocument()
    expect(screen.getByText('Each rules version is kept.')).toBeInTheDocument()
    expect(calls.filter((c) => c.includes('/classifications'))).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Hide classification history' })).toHaveAttribute('aria-expanded', 'true')
  })
})

describe('StoryRow entities', () => {
  it('shows each linked entity by name and kind, linking to the feed that lists its stories', () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(
      <QueryClientProvider client={qc}><MemoryRouter>
        <StoryRow story={{ ...story, entities: [
          { key: '^GSPC', name: 'S&P 500', kind: 'Index', kindLabel: 'index', region: 'US', currency: 'USD' },
          { key: 'ECB', name: 'European Central Bank', kind: 'CentralBank', kindLabel: 'central bank', region: 'EU', currency: 'EUR' },
        ] }} />
      </MemoryRouter></QueryClientProvider>,
    )
    expect(screen.getByRole('link', { name: 'S&P 500 · index' })).toHaveAttribute('href', '/news?view=markets&entity=%5EGSPC')
    expect(screen.getByRole('link', { name: 'European Central Bank · central bank' })).toHaveAttribute('href', '/news?view=macro&entity=ECB')
  })
})
