import { useDeferredValue } from 'react'
import { useSearchParams } from 'react-router'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { EmptyState, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { NewsCard, SourceProblems } from '@/components/news'
import { dateTime } from '@/lib/format'
import { categoryLabels, regionLabels } from '@/lib/news'
import { useAuth } from '@/lib/auth'
import { useNews } from '@/lib/queries'
import type { NewsCategory, NewsFilters, NewsScope } from '@/lib/types'
import { cn } from '@/lib/utils'

type View = 'latest' | 'watchlist' | 'Company' | 'Market' | 'Macro'

const views: { value: View; label: string }[] = [
  { value: 'latest', label: 'Latest' },
  { value: 'watchlist', label: 'My watchlist' },
  { value: 'Company', label: 'Company news' },
  { value: 'Market', label: 'Market-wide' },
  { value: 'Macro', label: 'Macroeconomic' },
]

const ALL = '__all'
const PAGE_SIZE = 20

export function NewsPage() {
  // News source names, terms and fetch status are shown to administrators only; article publishers stay visible.
  const { isAdministrator } = useAuth()
  const [params, setParams] = useSearchParams()
  const view = (params.get('view') as View | null) ?? 'latest'
  const category = params.get('category') as NewsCategory | null
  const region = params.get('region')
  const symbol = params.get('symbol')
  const from = params.get('from') ?? ''
  const to = params.get('to') ?? ''
  const publisher = params.get('publisher') ?? ''
  const searchInput = params.get('q') ?? ''
  const search = useDeferredValue(searchInput.trim())
  const page = Number(params.get('page') ?? '1') || 1

  const set = (changes: Record<string, string | null>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) {
      if (v === null || v === '' || v === ALL) next.delete(k)
      else next.set(k, v)
    }
    if (!('page' in changes)) next.delete('page')
    setParams(next, { replace: true })
  }

  const filters: NewsFilters = {
    category: category ?? undefined,
    scope: view === 'Company' || view === 'Market' || view === 'Macro' ? (view as NewsScope) : undefined,
    watchlist: view === 'watchlist',
    region: region ?? undefined,
    symbol: symbol ?? undefined,
    from: from || undefined,
    to: to || undefined,
    publisher: publisher || undefined,
    search: search || undefined,
    page,
    pageSize: PAGE_SIZE,
  }
  const news = useNews(filters)
  const data = news.data
  const failing = data?.sources.filter((s) => s.isConfigured && s.lastError) ?? []
  const configured = data?.sources.filter((s) => s.isConfigured) ?? []
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1
  const hasFilters = !!(category || region || symbol || from || to || publisher || searchInput)

  return (
    <div>
      <PageHeader
        title="Financial news"
        info="Recent developments from the configured sources, with publisher, publication time and a link to the original. Categories and relevance are rule-based labels with their basis shown – not predictions of price moves."
      />

      <div role="tablist" aria-label="News view" className="mb-4 flex flex-wrap gap-1 border-b">
        {views.map((v) => (
          <button key={v.value} role="tab" type="button" aria-selected={view === v.value}
            onClick={() => set({ view: v.value === 'latest' ? null : v.value })}
            className={cn('-mb-px border-b-2 px-3 py-2 text-sm text-muted-foreground hover:text-foreground',
              view === v.value ? 'border-foreground font-medium text-foreground' : 'border-transparent')}>
            {v.label}
          </button>
        ))}
      </div>

      <div className="mb-4 grid grid-cols-2 items-end gap-2 lg:grid-cols-6">
        <div className="relative col-span-2">
          <Input value={searchInput} onChange={(e) => set({ q: e.target.value })} placeholder="Headline, ticker or company" aria-label="Search news" />
        </div>
        <Select value={category ?? ALL} onValueChange={(v) => set({ category: v as string })}
          items={[{ value: ALL, label: 'All categories' }, ...Object.entries(categoryLabels).map(([value, label]) => ({ value, label }))]}>
          <SelectTrigger aria-label="Category"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All categories</SelectItem>
            {Object.entries(categoryLabels).map(([value, label]) => <SelectItem key={value} value={value}>{label}</SelectItem>)}
          </SelectContent>
        </Select>
        <Select value={region ?? ALL} onValueChange={(v) => set({ region: v as string })}
          items={[{ value: ALL, label: 'All markets' }, ...Object.entries(regionLabels).map(([value, label]) => ({ value, label }))]}>
          <SelectTrigger aria-label="Market or region"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>All markets</SelectItem>
            {Object.entries(regionLabels).map(([value, label]) => <SelectItem key={value} value={value}>{label}</SelectItem>)}
          </SelectContent>
        </Select>
        <label className="text-xs text-muted-foreground">Published from
          <Input type="date" value={from} onChange={(e) => set({ from: e.target.value })} className="mt-0.5" />
        </label>
        <label className="text-xs text-muted-foreground">Published until
          <Input type="date" value={to} onChange={(e) => set({ to: e.target.value })} className="mt-0.5" />
        </label>
      </div>
      {(hasFilters || publisher) && (
        <div className="mb-3 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
          {symbol && <span>Stock: <strong className="text-foreground">{symbol}</strong></span>}
          {publisher && <span>Publisher: <strong className="text-foreground">{publisher}</strong></span>}
          <Button size="sm" variant="ghost" onClick={() => setParams(view === 'latest' ? {} : { view }, { replace: true })}>Clear filters</Button>
        </div>
      )}

      {news.error ? <ErrorState error={news.error} onRetry={() => news.refetch()} /> : !data ? <LoadingBlock /> : (
        <div className="space-y-2" aria-live="polite" aria-busy={news.isFetching}>
          {data?.refreshing && (
            <p role="status" className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <Loader2 className="size-3.5 animate-spin" aria-hidden /> Updating news sources… The articles below were stored earlier; their publication times are shown on each.
            </p>
          )}
          {failing.length > 0 && <SourceProblems sources={failing} />}
          {configured.length === 0 && (
            <EmptyState title="No news source is configured"
              description={isAdministrator ? 'Enable the company-news and official central-bank sources (News:Yahoo:Enabled, News:OfficialFeeds:Enabled). See docs/financial-news.md.' : 'An administrator can enable news sources.'} />
          )}
          {configured.length > 0 && data.items.length === 0 && (
            <EmptyState
              title={view === 'watchlist' ? 'No news about your watched stocks yet' : 'No articles match these filters'}
              description={view === 'watchlist'
                ? 'Add stocks to your watchlist, or check back later – company news is fetched from the providers on demand and outside the US coverage is sparse.'
                : 'Try a wider date range or fewer filters.'} />
          )}
          {data.items.map((a) => <NewsCard key={a.id} article={a} imagesEnabled={data.imagesEnabled} />)}
          {pages > 1 && (
            <div className="flex items-center justify-between pt-2 text-sm">
              <Button size="sm" variant="outline" disabled={page <= 1} onClick={() => set({ page: String(page - 1) })}>Newer</Button>
              <span className="text-muted-foreground">Page {page} of {pages} · {data.totalCount} stories</span>
              <Button size="sm" variant="outline" disabled={page >= pages} onClick={() => set({ page: String(page + 1) })}>Older</Button>
            </div>
          )}
          <details className="pt-4 text-xs text-muted-foreground">
            <summary className="cursor-pointer font-medium text-foreground">{isAdministrator ? 'Sources, attribution and limitations' : 'Attribution and limitations'}</summary>
            <ul className="mt-2 list-disc space-y-0.5 pl-4">{data.notes.map((n) => <li key={n}>{n}</li>)}</ul>
            {isAdministrator && <table className="mt-3 w-full">
              <thead><tr className="border-b text-left"><th className="py-1 font-medium">Source</th><th className="py-1 font-medium">Coverage</th><th className="py-1 font-medium">Last fetched</th><th className="py-1 font-medium">Status</th></tr></thead>
              <tbody>
                {data.sources.map((s) => (
                  <tr key={s.key} className="border-b align-top last:border-0">
                    <td className="py-1 pr-2">{s.displayName}<div className="text-[10px]">{s.terms}</div></td>
                    <td className="py-1 pr-2">{s.coverage}</td>
                    <td className="py-1 pr-2">{s.lastAttemptUtc ? dateTime(s.lastAttemptUtc) : 'never'}</td>
                    <td className="py-1">{!s.isConfigured ? 'Disabled' : s.lastError ? `Error: ${s.lastError}` : s.lastSuccessUtc ? `OK (${s.lastArticleCount} items)` : 'Not fetched yet'}</td>
                  </tr>
                ))}
              </tbody>
            </table>}
          </details>
        </div>
      )}
    </div>
  )
}
