import { useDeferredValue, type ReactNode } from 'react'
import { useSearchParams } from 'react-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { EmptyState, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { MatrixLoader } from '@/components/matrix-loader'
import { AdminSourceTable, BenchmarksPanel, CompanyBlock, MarketSection, SourcesPanel, StoryList } from '@/components/wire'
import { useAuth } from '@/lib/auth'
import { regionLabels } from '@/lib/news'
import { useWireFeed } from '@/lib/queries'
import type { AttentionLevel, MarketArea, WireFeedName, WireQuery, WireSourceStatus, WireStory } from '@/lib/types'
import { cn } from '@/lib/utils'
import { areaLabels, attentionOptions, defaultWindowHours, freshnessLine, windowLabel, windowOptions, wireTabs } from '@/lib/wire'

const DEFAULT = '__default'

/** Old links (`?view=Company`, `?symbol=SAP.DE`) keep working. */
function viewFrom(params: URLSearchParams): WireFeedName {
  const v = params.get('view')
  const legacy: Record<string, WireFeedName> = { Company: 'companies', Market: 'markets', Macro: 'macro' }
  if (v && legacy[v]) return legacy[v]
  return wireTabs.some((t) => t.value === v) ? (v as WireFeedName) : 'latest'
}

/**
 * Wire: five feeds with their own server-side rules (docs/financial-news.md › Wire feeds) – Latest, My watchlist, Company
 * news, Market-wide, Macroeconomic. Every filter lives in the URL; paging continues with the server's cursor.
 */
export function NewsPage() {
  const [params, setParams] = useSearchParams()
  const view = viewFrom(params)
  const hours = Number(params.get('hours')) || undefined
  const region = params.get('region') ?? undefined
  const attention = (params.get('attention') as AttentionLevel | null) ?? undefined
  const searchInput = params.get('q') ?? params.get('symbol') ?? ''
  const search = useDeferredValue(searchInput.trim())
  const cursor = params.get('cursor') ?? undefined
  const area = (params.get('area') as MarketArea | null) ?? undefined
  const entity = params.get('entity') ?? undefined

  const set = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    next.delete('symbol')
    for (const [k, v] of Object.entries(changes)) {
      if (v === null || v === '' || v === DEFAULT) next.delete(k)
      else next.set(k, v)
    }
    if (!('cursor' in changes)) next.delete('cursor')
    setParams(next, { replace: !('cursor' in changes) })
  }

  const query: WireQuery = {
    hours, region, attention, search: search || undefined, cursor, area: view === 'markets' ? area : undefined,
    entity: view === 'markets' || view === 'macro' ? entity : undefined,
  }
  const tab = wireTabs.find((t) => t.value === view)!

  return (
    <div>
      <PageHeader
        title="Financial news"
        info="Five feeds, each with its own rules: what happened recently, what concerns what you follow, company, market and economy news. Stories group reports of the same event; attention labels are rule-based, never price predictions."
      />

      <div className="mb-3">
        <Input value={searchInput} onChange={(e) => set({ q: e.target.value })} placeholder="Search headlines, tickers or companies" aria-label="Search news" />
      </div>

      <div role="tablist" aria-label="News feed" className="mb-3 flex flex-wrap gap-1 border-b">
        {wireTabs.map((t) => (
          <button key={t.value} role="tab" type="button" aria-selected={view === t.value} title={t.question}
            onClick={() => setParams(t.value === 'latest' ? {} : { view: t.value }, { replace: true })}
            className={cn('-mb-px border-b-2 px-3 py-2 text-sm text-muted-foreground hover:text-foreground',
              view === t.value ? 'border-foreground font-medium text-foreground' : 'border-transparent')}>
            {t.label}
          </button>
        ))}
      </div>

      <div className="mb-4 flex flex-wrap items-end gap-2">
        <FilterSelect label="Time window" value={hours ? String(hours) : DEFAULT} onChange={(v) => set({ hours: v })}
          items={[{ value: DEFAULT, label: `Default: ${windowLabel(defaultWindowHours[view]).toLowerCase()}` }, ...windowOptions.map((o) => ({ value: String(o.hours), label: o.label }))]} />
        <FilterSelect label="Market or region" value={region ?? DEFAULT} onChange={(v) => set({ region: v })}
          items={[{ value: DEFAULT, label: 'All markets' }, ...Object.entries(regionLabels).map(([value, label]) => ({ value, label }))]} />
        {view !== 'companies' && (
          <FilterSelect label="Attention" value={attention ?? DEFAULT} onChange={(v) => set({ attention: v })}
            items={attentionOptions(view).map((o) => ({ value: o.value || DEFAULT, label: o.label }))} />
        )}
        {(hours || region || attention || searchInput || area || entity) && (
          <Button size="sm" variant="ghost" onClick={() => setParams(view === 'latest' ? {} : { view }, { replace: true })}>Clear filters</Button>
        )}
      </div>

      <p className="mb-3 text-sm text-muted-foreground">{tab.question}</p>

      {view === 'latest' && <StoriesView feed="latest" query={query} set={set} empty="No Medium or High attention stories in this window. Try a longer time window." />}
      {view === 'watchlist' && <StoriesView feed="watchlist" query={query} set={set} showWhy empty="No news about your watched stocks yet" />}
      {view === 'macro' && <StoriesView feed="macro" query={query} set={set} empty="No macroeconomic stories in this window. Try a longer time window." />}
      {view === 'companies' && <CompaniesView query={query} set={set} />}
      {view === 'markets' && <MarketsView query={query} set={set} />}
    </div>
  )
}

type Setter = (changes: Record<string, string | null>) => void

function FilterSelect({ label, value, onChange, items }: { label: string; value: string; onChange: (v: string) => void; items: { value: string; label: string }[] }) {
  return (
    <label className="text-xs text-muted-foreground">{label}
      <Select value={value} onValueChange={(v) => onChange(v as string)} items={items}>
        <SelectTrigger aria-label={label} className="mt-0.5 min-w-40"><SelectValue /></SelectTrigger>
        <SelectContent>{items.map((i) => <SelectItem key={i.value} value={i.value}>{i.label}</SelectItem>)}</SelectContent>
      </Select>
    </label>
  )
}

/** Feed body with the side panels; the notes and (for administrators) the provider table below. */
function Layout({ sources, notes, refreshing, fetching, children }: {
  sources: WireSourceStatus[]; notes: string[]; refreshing: boolean; fetching: boolean; children: ReactNode
}) {
  const { isAdministrator } = useAuth()
  return (
    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_17rem]">
      <div className="min-w-0 space-y-3" aria-busy={fetching}>
        <p role="status" className="flex items-center gap-1.5 text-xs text-muted-foreground">
          {refreshing && <MatrixLoader className="size-3.5" aria-hidden />}
          {refreshing ? 'Updating news sources… Stories already stored are shown with their own publication times.' : freshnessLine(sources)}
        </p>
        {children}
        <details className="pt-2 text-xs text-muted-foreground">
          <summary className="cursor-pointer font-medium text-foreground">{isAdministrator ? 'How this feed works, sources and limitations' : 'How this feed works and limitations'}</summary>
          <ul className="mt-2 list-disc space-y-0.5 pl-4">{notes.map((n) => <li key={n}>{n}</li>)}</ul>
          {isAdministrator && <AdminSourceTable />}
        </details>
      </div>
      <aside className="space-y-3" aria-label="Market context">
        <SourcesPanel sources={sources} />
        <BenchmarksPanel />
      </aside>
    </div>
  )
}

function Pager({ cursor, next, set, noun = 'stories' }: { cursor: string | undefined; next: string | null; set: Setter; noun?: string }) {
  if (!cursor && !next) return null
  return (
    <div className="flex items-center justify-between pt-1 text-sm">
      <Button size="sm" variant="outline" disabled={!cursor} onClick={() => set({ cursor: null })}>Newest {noun}</Button>
      <Button size="sm" variant="outline" disabled={!next} onClick={() => next && set({ cursor: next })}>Older {noun}</Button>
    </div>
  )
}

/** The entity filter in words: its name from the stories shown, otherwise the key from the link. */
function EntityFilter({ entityKey, stories, set }: { entityKey: string | undefined; stories: WireStory[]; set: Setter }) {
  if (!entityKey) return null
  const name = stories.flatMap((s) => s.entities).find((e) => e.key.toLowerCase() === entityKey.toLowerCase())
  return (
    <p className="mb-3 flex flex-wrap items-center gap-2 text-sm">
      Only stories linked to <span className="font-medium">{name ? `${name.name} (${name.kindLabel})` : entityKey}</span>
      <Button size="sm" variant="ghost" onClick={() => set({ entity: null })}>Show all</Button>
    </p>
  )
}

function StoriesView({ feed, query, set, empty, showWhy = false }: {
  feed: 'latest' | 'watchlist' | 'macro'; query: WireQuery; set: Setter; empty: string; showWhy?: boolean
}) {
  const result = useWireFeed(feed, query)
  if (result.error) return <ErrorState error={result.error} onRetry={() => result.refetch()} />
  const data = result.data
  if (!data) return <LoadingBlock />
  return (
    <Layout sources={data.sources} notes={data.notes} refreshing={data.refreshing} fetching={result.isFetching}>
      <EntityFilter entityKey={query.entity} stories={data.stories} set={set} />
      {data.stories.length === 0
        ? <EmptyState title={empty} description={data.notes[0]} />
        : <StoryList stories={data.stories} label={`${wireTabs.find((t) => t.value === feed)!.label} stories`} showWhy={showWhy} />}
      <Pager cursor={query.cursor} next={data.nextCursor} set={set} />
    </Layout>
  )
}

function CompaniesView({ query, set }: { query: WireQuery; set: Setter }) {
  const result = useWireFeed('companies', query)
  if (result.error) return <ErrorState error={result.error} onRetry={() => result.refetch()} />
  const data = result.data
  if (!data) return <LoadingBlock />
  return (
    <Layout sources={data.sources} notes={data.notes} refreshing={data.refreshing} fetching={result.isFetching}>
      {data.companies.length === 0
        ? <EmptyState title="No company stories in this window" description={data.notes[0]} />
        : <div className="space-y-3">{data.companies.map((c) => <CompanyBlock key={c.symbol} company={c} />)}</div>}
      <Pager cursor={query.cursor} next={data.nextCursor} set={set} noun="companies" />
    </Layout>
  )
}

function MarketsView({ query, set }: { query: WireQuery; set: Setter }) {
  const result = useWireFeed('markets', query)
  if (result.error) return <ErrorState error={result.error} onRetry={() => result.refetch()} />
  const data = result.data
  if (!data) return <LoadingBlock />
  const single = query.area ? data.sections[0] : undefined
  return (
    <Layout sources={data.sources} notes={data.notes} refreshing={data.refreshing} fetching={result.isFetching}>
      <EntityFilter entityKey={query.entity} stories={data.sections.flatMap((s) => s.stories)} set={set} />
      {single ? (
        <>
          <Button size="sm" variant="ghost" onClick={() => set({ area: null })}>All market areas</Button>
          <MarketSection section={single} />
          <Pager cursor={query.cursor} next={single.nextCursor} set={set} />
        </>
      ) : (
        <div className="space-y-4">
          {data.sections.map((s) => <MarketSection key={s.area} section={s} onMore={() => set({ area: s.area })} />)}
        </div>
      )}
      {!single && data.sections.every((s) => s.storyCount === 0) && (
        <p className="text-xs text-muted-foreground">No market-level stories in this window across {Object.values(areaLabels).join(', ').toLowerCase()}.</p>
      )}
    </Layout>
  )
}
