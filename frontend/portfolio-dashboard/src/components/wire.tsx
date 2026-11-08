import { useState } from 'react'
import { Link } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, InfoTip, LoadingBlock, ResultSection, Warnings } from '@/components/common'
import { MatrixLoader } from '@/components/matrix-loader'
import { EventComparisonDialog } from '@/components/news'
import { QuoteCell } from '@/components/stock-selection'
import { dateTime } from '@/lib/format'
import { publishedLabel, safeExternalUrl } from '@/lib/news'
import { useNewsSources, usePortfolioNews, useStocksBySymbols } from '@/lib/queries'
import type { WireCompany, WireMarketSection, WireSourceStatus, WireStory } from '@/lib/types'
import { cn } from '@/lib/utils'
import { regionLabel, sourcesLabel, utcTime, wireBenchmarks } from '@/lib/wire'

const attentionStyles = {
  High: 'border-foreground/50 font-semibold text-foreground',
  Medium: 'border-border text-foreground',
  Low: 'border-border text-muted-foreground',
} as const

/** Attention as a text label; the label itself shows every reason (no icon, no colour-only meaning). */
export function AttentionLabel({ story }: { story: WireStory }) {
  const a = story.attention
  return (
    <Badge variant="outline" className={cn('shrink-0', attentionStyles[a.level])}>
      <InfoTip of={`${a.level} attention`}>
        <ul className="list-disc pl-4">{a.reasons.map((r) => <li key={r}>{r}</li>)}</ul>
        <span className="mt-1 block">{a.note}</span>
      </InfoTip>
    </Badge>
  )
}

/**
 * One story as a dense row: time (relative and UTC), headline linking to the earliest report, what it concerns, why a
 * personal feed shows it, and every report on demand. Linked listings open their stock page; Compare opens the event
 * and price-movement comparison for the lead report.
 */
export function StoryRow({ story, showWhy = false }: { story: WireStory; showWhy?: boolean }) {
  const [open, setOpen] = useState(false)
  const [compare, setCompare] = useState<string | null>(null)
  const href = safeExternalUrl(story.url)
  const region = regionLabel(story.region)
  const leadId = story.sources[0]?.articleId
  const sourcesId = `sources-${story.id}`
  return (
    <article aria-label={story.headline} className="grid grid-cols-[4.5rem_minmax(0,1fr)] gap-x-3 border-b px-3 py-2.5 last:border-b-0 sm:grid-cols-[5.5rem_minmax(0,1fr)_auto]">
      <div className="text-xs text-muted-foreground tabular">
        <time dateTime={story.lastPublishedAtUtc} title={`Latest report ${dateTime(story.lastPublishedAtUtc)} · first report ${dateTime(story.firstPublishedAtUtc)}`}>
          <span className="block font-medium text-foreground">{utcTime(story.lastPublishedAtUtc)}</span>
          {publishedLabel(story.lastPublishedAtUtc)}
        </time>
      </div>
      <div className="min-w-0 space-y-1">
        <h3 className="text-sm leading-snug font-semibold">
          {href ? <a href={href} target="_blank" rel="noopener noreferrer nofollow" className="hover:underline">{story.headline}</a> : story.headline}
        </h3>
        <div className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-xs text-muted-foreground">
          {story.assets.map((a) => (
            <Link key={a.symbol} to={`/stocks/${encodeURIComponent(a.symbol)}`}
              className={cn('rounded border px-1.5 py-0.5 font-medium text-foreground hover:bg-muted', (a.onWatchlist || a.held) && 'border-foreground/40')}
              title={`${a.name ?? a.symbol}${a.onWatchlist ? ' · on your watchlist' : ''}${a.held ? ' · in one of your portfolios' : ''}`}>
              {a.symbol}
            </Link>
          ))}
          <InfoTip of={<span className="text-foreground">{story.eventTypeLabel}</span>}>
            {story.eventTypeBasis ? `Event type from ${story.eventTypeBasis}.` : 'Event type from the classification rules.'} Rules version {story.ruleVersion}.
          </InfoTip>
          {story.marketArea && <span>· {story.marketArea === 'Fx' ? 'Currencies' : story.marketArea}</span>}
          {region && <span>· {region}</span>}
          <span>·</span>
          <button type="button" className="hover:text-foreground hover:underline" aria-expanded={open} aria-controls={sourcesId} onClick={() => setOpen(!open)}>
            {sourcesLabel(story)}
          </button>
          {story.isOfficial && <Badge variant="outline">Official source</Badge>}
          {story.isSimulated && <Badge variant="outline" className="border-series-2/60 bg-series-2/10">SIMULATED</Badge>}
        </div>
        {showWhy && story.why.length > 0 && <p className="text-xs text-muted-foreground">Why you see this: {story.why.join('; ')}</p>}
        {story.summary && <p className="line-clamp-2 text-xs text-muted-foreground">{story.summary}</p>}
        {open && (
          <ul id={sourcesId} className="space-y-0.5 border-l pl-2 text-xs">
            {story.sources.map((s) => {
              const url = safeExternalUrl(s.url)
              return (
                <li key={s.articleId} className="flex flex-wrap gap-x-2">
                  <span className="text-muted-foreground tabular">{dateTime(s.publishedAtUtc)}</span>
                  {url ? <a href={url} target="_blank" rel="noopener noreferrer nofollow" className="hover:underline">{s.publisher}</a> : s.publisher}
                  {s.isOfficial && <span className="text-muted-foreground">official</span>}
                </li>
              )
            })}
            {story.articleCount > story.sources.length && <li className="text-muted-foreground">{story.articleCount - story.sources.length} more reports not listed</li>}
          </ul>
        )}
      </div>
      <div className="col-start-2 flex flex-wrap items-start gap-1 pt-1 sm:col-start-3 sm:flex-col sm:items-end sm:pt-0">
        <AttentionLabel story={story} />
        {leadId && story.assets.filter((a) => a.assetType !== 'Index').slice(0, 2).map((a) => (
          <Button key={a.symbol} size="xs" variant="ghost" onClick={() => setCompare(a.symbol)} title="Event and price-movement comparison"
            aria-label={`Compare ${a.symbol} price movement around this story`}>
            Compare {a.symbol}
          </Button>
        ))}
      </div>
      {compare && leadId && <EventComparisonDialog article={{ id: leadId, headline: story.headline }} symbol={compare} onClose={() => setCompare(null)} />}
    </article>
  )
}

/** A dense list of stories on the map-safe surface. */
export function StoryList({ stories, label, showWhy = false }: { stories: WireStory[]; label: string; showWhy?: boolean }) {
  return (
    <div className="map-surface overflow-hidden rounded-xl border" role="feed" aria-label={label}>
      {stories.map((s) => <StoryRow key={s.id} story={s} showWhy={showWhy} />)}
    </div>
  )
}

/** Company News: one block per company with its newest stories. */
export function CompanyBlock({ company }: { company: WireCompany }) {
  const relation = [company.held && 'in your portfolio', company.onWatchlist && 'on your watchlist', company.curated && 'curated list'].filter(Boolean).join(' · ')
  return (
    <section aria-label={`${company.symbol} news`} className="map-surface overflow-hidden rounded-xl border">
      <header className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-0.5 border-b px-3 py-2">
        <h3 className="text-sm font-semibold">
          <Link to={`/stocks/${encodeURIComponent(company.symbol)}`} className="hover:underline">{company.name ?? company.symbol}</Link>
          <span className="ml-1.5 font-normal text-muted-foreground">{company.symbol}{company.sector ? ` · ${company.sector}` : ''}</span>
        </h3>
        <p className="text-xs text-muted-foreground">
          {company.storyCount} {company.storyCount === 1 ? 'story' : 'stories'} · newest {publishedLabel(company.newestAtUtc)}{relation && ` · ${relation}`}
        </p>
      </header>
      {company.stories.map((s) => <StoryRow key={s.id} story={s} />)}
      {company.storyCount > company.stories.length && (
        <div className="px-3 py-1.5 text-xs">
          <Link to={`/stocks/${encodeURIComponent(company.symbol)}`} className="text-muted-foreground hover:text-foreground hover:underline">
            All {company.symbol} news on its stock page
          </Link>
        </div>
      )}
    </section>
  )
}

/** Market-wide: one area with its newest stories and a link to the paged list of the area. */
export function MarketSection({ section, onMore }: { section: WireMarketSection; onMore?: () => void }) {
  return (
    <section aria-label={section.label} className="space-y-1.5">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="heading-display text-base">{section.label}</h2>
        <span className="text-xs text-muted-foreground">
          {section.storyCount} {section.storyCount === 1 ? 'story' : 'stories'}
          {onMore && section.storyCount > section.stories.length && (
            <Button size="xs" variant="ghost" className="ml-1" onClick={onMore}>All {section.label.toLowerCase()} stories</Button>
          )}
        </span>
      </div>
      {section.stories.length === 0
        ? <p className="map-surface rounded-xl border px-3 py-2 text-xs text-muted-foreground">No {section.label.toLowerCase()} stories in this window.</p>
        : <StoryList stories={section.stories} label={`${section.label} stories`} />}
    </section>
  )
}

/**
 * Overview: news about this portfolio's holdings and currencies. "Why you see this" states each holding's weight (or the
 * currency's share) from the current valuation – a fact, never a predicted effect.
 */
export function PortfolioNewsSection({ portfolioId }: { portfolioId: string }) {
  const [hours, setHours] = useState(168)
  const [cursors, setCursors] = useState<string[]>([])
  const cursor = cursors.at(-1)
  const news = usePortfolioNews(portfolioId, hours, cursor)
  const data = news.data
  const choose = (h: number) => { setHours(h); setCursors([]) }
  return (
    <ResultSection title="News about your holdings"
      hint="Stories linked to the stocks this portfolio holds, and rate decisions for the currencies it holds. The weight next to each is from the current valuation; Compare shows the price movement around a story, which is not evidence that the news caused it.">
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-1 rounded-lg border bg-background p-0.5" role="group" aria-label="News window">
          {[[168, '7 days'], [720, '30 days'], [2160, '90 days']].map(([h, label]) => (
            <Button key={h} size="sm" variant={hours === h ? 'secondary' : 'ghost'} aria-pressed={hours === h} onClick={() => choose(h as number)}>{label}</Button>
          ))}
        </div>
        <Button variant="link" size="sm" render={<Link to="/news?view=watchlist" />}>Open Wire</Button>
      </div>
      {news.error ? <ErrorState error={news.error} onRetry={() => news.refetch()} /> : !data ? <LoadingBlock height="h-32" /> : (
        <div className="space-y-2">
          {data.refreshing && (
            <p role="status" className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <MatrixLoader className="size-3.5" aria-hidden /> Updating news sources… stored stories are shown meanwhile.
            </p>
          )}
          {data.problems.length > 0 && <Warnings items={data.problems} title="Weights not available for every holding" />}
          {data.items.length === 0
            ? <EmptyState title="No news about your holdings in this window" description={data.notes[0]} />
            : <StoryList stories={data.items.map((i) => i.story)} label="News about your holdings" showWhy />}
          {(cursors.length > 0 || data.nextCursor) && (
            <div className="flex justify-between pt-1">
              <Button size="sm" variant="outline" disabled={cursors.length === 0} onClick={() => setCursors([])}>Newest stories</Button>
              <Button size="sm" variant="outline" disabled={!data.nextCursor} onClick={() => data.nextCursor && setCursors([...cursors, data.nextCursor])}>Older stories</Button>
            </div>
          )}
        </div>
      )}
    </ResultSection>
  )
}

/** Freshness per group of sources (no provider names – administrators see those below the feed). */
export function SourcesPanel({ sources }: { sources: WireSourceStatus[] }) {
  return (
    <section aria-label="Sources" className="map-surface rounded-xl border p-3">
      <h2 className="mb-1.5 text-sm font-semibold">
        <InfoTip of="Sources">Fresh: fetched successfully within twice the group's refresh interval. Delayed: older than that. Unavailable: never fetched successfully. Stored stories are shown either way, with their own times.</InfoTip>
      </h2>
      {sources.length === 0 ? <p className="text-xs text-muted-foreground">No news source is configured.</p> : (
        <dl className="space-y-1 text-xs">
          {sources.map((s) => (
            <div key={s.group} className="flex items-baseline justify-between gap-2">
              <dt>{s.group}</dt>
              <dd className={cn('text-right', s.status === 'Fresh' ? 'text-muted-foreground' : 'font-medium text-warn')}
                title={[s.lastSuccessUtc ? `Last success ${dateTime(s.lastSuccessUtc)}` : 'No successful fetch yet', s.problem].filter(Boolean).join(' · ')}>
                {s.status}
              </dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  )
}

/** Benchmarks with their stored last close and change, kind and date (never called live). */
export function BenchmarksPanel() {
  const stocks = useStocksBySymbols(wireBenchmarks)
  const rows = wireBenchmarks.map((s) => stocks.items.find((i) => i.symbol === s)).filter((i) => !!i)
  return (
    <section aria-label="Benchmarks" className="map-surface rounded-xl border p-3">
      <h2 className="mb-1.5 text-sm font-semibold">
        <InfoTip of="Benchmarks">Stored prices of benchmark indexes and ETFs with their kind (delayed quote or last close) and date. Not real-time.</InfoTip>
      </h2>
      {stocks.isLoading ? <p className="text-xs text-muted-foreground">Loading benchmarks…</p> : rows.length === 0 ? (
        <p className="text-xs text-muted-foreground">No benchmark prices stored.</p>
      ) : (
        <ul className="space-y-1.5">
          {rows.map((r) => (
            <li key={r.symbol} className="flex items-start justify-between gap-2 text-xs">
              <Link to={`/stocks/${encodeURIComponent(r.symbol)}`} className="min-w-0 truncate hover:underline" title={r.name ?? r.symbol}>
                {r.name ?? r.symbol}
              </Link>
              <QuoteCell quote={r.quote} />
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

/** Administrators: provider names, coverage, terms and the last fetch of every source. */
export function AdminSourceTable() {
  const sources = useNewsSources(true)
  if (!sources.data) return null
  return (
    <table className="mt-3 w-full text-xs">
      <caption className="sr-only">News sources and their last fetch</caption>
      <thead><tr className="border-b text-left"><th className="py-1 font-medium">Source</th><th className="py-1 font-medium">Coverage</th><th className="py-1 font-medium">Last fetched</th><th className="py-1 font-medium">Status</th></tr></thead>
      <tbody>
        {sources.data.map((s) => (
          <tr key={s.key} className="border-b align-top last:border-0">
            <td className="py-1 pr-2">{s.displayName}<div className="text-[10px]">{s.terms}</div></td>
            <td className="py-1 pr-2">{s.coverage}</td>
            <td className="py-1 pr-2">{s.lastAttemptUtc ? dateTime(s.lastAttemptUtc) : 'never'}</td>
            <td className="py-1">{!s.isConfigured ? 'Disabled' : s.lastError ? `Error: ${s.lastError}` : s.lastSuccessUtc ? `OK (${s.lastArticleCount} items)` : 'Not fetched yet'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
