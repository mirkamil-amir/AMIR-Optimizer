import { useState } from 'react'
import { Link } from 'react-router'
import { AlertTriangle, ExternalLink, FlaskConical, Landmark, LineChart, Newspaper } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState, ErrorState, InfoTip, LoadingBlock, SectionTitle } from '@/components/common'
import { ChangeText } from '@/components/watchlist'
import { date, dateTime, money } from '@/lib/format'
import { categoryLabels, publishedLabel, safeExternalUrl, sourceLine } from '@/lib/news'
import { useEventComparison, useNews } from '@/lib/queries'
import type { NewsArticle, NewsSourceStatus } from '@/lib/types'
import { cn } from '@/lib/utils'
import { useAuth } from '@/lib/auth'

const relevanceStyles = {
  High: 'border-foreground/40 text-foreground',
  Medium: 'border-border text-foreground',
  Low: 'border-border text-muted-foreground',
} as const

/** One story: headline linking to the original, source and time, rule-based labels with their basis, linked stocks. */
export function NewsCard({ article, imagesEnabled, compact = false }: { article: NewsArticle; imagesEnabled: boolean; compact?: boolean }) {
  const [compare, setCompare] = useState<string | null>(null)
  const href = safeExternalUrl(article.url)
  const published = new Date(article.publishedAtUtc)
  return (
    <article aria-label={article.headline}>
    <Card className="gap-0 py-3">
      <CardContent className="flex gap-3 px-4">
        {!compact && <Thumbnail article={article} imagesEnabled={imagesEnabled} />}
        <div className="min-w-0 flex-1 space-y-1.5">
          <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
            <Link to={`/news?publisher=${encodeURIComponent(article.publisher)}`} className="font-medium text-foreground hover:underline"
              title={`Show news from ${article.publisher}`}>
              {sourceLine(article)}
            </Link>
            <span aria-hidden>·</span>
            <time dateTime={article.publishedAtUtc} title={`Published ${published.toISOString().replace('T', ' ').slice(0, 16)} UTC · retrieved ${dateTime(article.retrievedAtUtc)}`}>
              {publishedLabel(article.publishedAtUtc)}
            </time>
            {article.isOfficialSource && <Badge variant="outline" className="gap-1"><Landmark className="size-3" />Official source</Badge>}
            {article.isSimulated && <Badge variant="outline" className="gap-1 border-series-2/60 bg-series-2/10"><FlaskConical className="size-3" />SIMULATED</Badge>}
          </div>
          <h3 className="text-sm leading-snug font-semibold">
            {href ? (
              <a href={href} target="_blank" rel="noopener noreferrer nofollow" className="hover:underline">
                {article.headline}
                <ExternalLink className="ml-1 inline size-3 align-baseline text-muted-foreground" aria-label="(opens the original article in a new tab)" />
              </a>
            ) : article.headline}
          </h3>
          {article.summary && !compact && <p className="text-sm text-muted-foreground">{article.summary}</p>}
          <div className="flex flex-wrap items-center gap-1.5 text-xs">
            <Badge variant="outline" className="gap-1">
              {categoryLabels[article.category]}
              <InfoTip label="Why this category">Rule-based: {article.categoryBasis}.</InfoTip>
            </Badge>
            <Badge variant="outline" className={cn('gap-1', relevanceStyles[article.relevance.level])}>
              Potential relevance: {article.relevance.level}
              <InfoTip label="How relevance is assessed">
                <span className="block font-medium">{article.relevance.points} points</span>
                <ul className="list-disc pl-4">{article.relevance.reasons.map((r) => <li key={r}>{r}</li>)}</ul>
                <span className="mt-1 block">{article.relevance.note}</span>
              </InfoTip>
            </Badge>
            {article.assets.map((a) => (
              <span key={a.symbol} className="inline-flex items-center gap-0.5">
                <Link to={`/stocks/${encodeURIComponent(a.symbol)}`} className={cn('rounded border px-1.5 py-0.5 font-medium hover:bg-muted', a.onWatchlist && 'border-foreground/40')}
                  title={`${a.name ?? a.symbol} – linked via ${a.matchMethod}${a.onWatchlist ? ' · on your watchlist' : ''}`}>
                  {a.symbol}
                </Link>
                <Button size="icon-xs" variant="ghost" onClick={() => setCompare(a.symbol)} aria-label={`Compare ${a.symbol} price movement around this article`}
                  title="Event and price-movement comparison">
                  <LineChart />
                </Button>
              </span>
            ))}
          </div>
        </div>
      </CardContent>
      {compare && <EventComparisonDialog article={article} symbol={compare} onClose={() => setCompare(null)} />}
    </Card>
    </article>
  )
}

/** Provider image only when the operator enabled images; otherwise a neutral placeholder (never a stand-in photo). */
function Thumbnail({ article, imagesEnabled }: { article: NewsArticle; imagesEnabled: boolean }) {
  const [broken, setBroken] = useState(false)
  if (imagesEnabled && article.imageUrl && !broken) {
    return (
      <figure className="hidden w-28 shrink-0 sm:block">
        <img src={article.imageUrl} alt={`Image supplied with the article “${article.headline}”`} loading="lazy" referrerPolicy="no-referrer"
          className="h-20 w-28 rounded-md object-cover" onError={() => setBroken(true)} />
        {article.imageCredit && <figcaption className="mt-0.5 text-[10px] leading-tight text-muted-foreground">{article.imageCredit}</figcaption>}
      </figure>
    )
  }
  return (
    <div className="hidden h-20 w-28 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground sm:flex" aria-hidden>
      {article.isOfficialSource ? <Landmark className="size-6" /> : <Newspaper className="size-6" />}
    </div>
  )
}

/** Company news on a stock page: articles the provider tagged with this listing. */
export function CompanyNews({ symbol }: { symbol: string }) {
  const news = useNews({ symbol, pageSize: 10 })
  const failing = news.data?.sources.filter((s) => s.lastError && s.isConfigured) ?? []
  return (
    <section>
      <SectionTitle
        hint="Articles that the news provider itself tagged with this listing. Untagged articles about the company may exist elsewhere; the full text is only at the original publisher."
        right={<Button size="sm" variant="ghost" render={<Link to={`/news?symbol=${encodeURIComponent(symbol)}`} />}>All {symbol} news</Button>}
      >
        Company news
      </SectionTitle>
      {news.error ? <ErrorState error={news.error} onRetry={() => news.refetch()} /> : !news.data ? <LoadingBlock height="h-32" /> : (
        <div className="space-y-2">
          {failing.length > 0 && <SourceProblems sources={failing} />}
          {news.data.items.length === 0 ? (
            <EmptyState icon={<Newspaper className="size-8" />} title="No tagged articles found"
              description="The configured providers returned no articles tagged with this listing. Coverage outside the US is sparse." />
          ) : news.data.items.map((a) => <NewsCard key={a.id} article={a} imagesEnabled={news.data!.imagesEnabled} compact />)}
        </div>
      )}
    </section>
  )
}

export function SourceProblems({ sources }: { sources: NewsSourceStatus[] }) {
  // Which source failed is an operational detail for administrators.
  const { isAdministrator } = useAuth()
  return (
    <p className="flex items-start gap-1.5 rounded-md border border-warn/40 p-2 text-xs text-muted-foreground">
      <AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-warn" aria-hidden />
      <span>
        Some sources could not be reached; stored articles are shown.{' '}
        {isAdministrator && sources.map((s) => `${s.displayName}: ${s.lastError}`).join(' · ')}
      </span>
    </p>
  )
}

/** Event and price-movement comparison (descriptive; not evidence of causation). */
export function EventComparisonDialog({ article, symbol, onClose }: { article: NewsArticle; symbol: string; onClose: () => void }) {
  const cmp = useEventComparison(article.id, symbol)
  const c = cmp.data
  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Event and price-movement comparison · {symbol}</DialogTitle>
          <DialogDescription>{article.headline}</DialogDescription>
        </DialogHeader>
        {cmp.error ? <ErrorState error={cmp.error} onRetry={() => cmp.refetch()} /> : !c ? <LoadingBlock height="h-40" /> : (
          <div className="space-y-4 text-sm">
            <dl className="grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-2">
              <Row label="Published" value={`${dateTime(c.publishedAtUtc)} (${c.publishedLocal})`} />
              <Row label="Timing" value={c.timing} />
              <Row label="Reference close (before publication)" value={c.referenceDate ? `${money(c.referencePrice, c.currency)} on ${date(c.referenceDate)}` : 'none'} />
              <Row label="Benchmark" value={c.benchmarkSymbol ?? 'none'} />
              <Row label="Price basis" value={`${c.basis} · ${c.currency}${c.isSimulated ? ' · SIMULATED' : ''}`} />
            </dl>
            <table className="w-full text-sm tabular">
              <caption className="sr-only">Changes after publication</caption>
              <thead className="text-xs text-muted-foreground">
                <tr className="border-b">
                  <th className="py-1 text-left font-medium">After</th>
                  <th className="py-1 text-left font-medium">Close date</th>
                  <th className="py-1 text-right font-medium">{c.symbol}</th>
                  <th className="py-1 text-right font-medium">{c.benchmarkSymbol ?? 'Benchmark'}</th>
                  <th className="py-1 text-right font-medium">Difference</th>
                </tr>
              </thead>
              <tbody>
                {c.horizons.map((h) => (
                  <tr key={h.tradingDays} className="border-b last:border-0">
                    <td className="py-1.5">{h.tradingDays} trading day{h.tradingDays === 1 ? '' : 's'}</td>
                    <td className="py-1.5">{h.date ? date(h.date) : <span className="text-muted-foreground" title={h.limitation ?? undefined}>not yet</span>}</td>
                    <td className="py-1.5 text-right"><ChangeText value={h.stockChange} /></td>
                    <td className="py-1.5 text-right"><ChangeText value={h.benchmarkChange} /></td>
                    <td className="py-1.5 text-right" title={h.limitation ?? undefined}><ChangeText value={h.difference} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="text-xs text-muted-foreground">
              Difference = stock change − benchmark change over the same dates (a simple market adjustment, not a model-based abnormal return).
            </p>
            {c.overlappingArticles.length > 0 && (
              <div>
                <h4 className="mb-1 text-xs font-semibold">Other {c.symbol} news within three days</h4>
                <ul className="list-disc space-y-0.5 pl-4 text-xs text-muted-foreground">
                  {c.overlappingArticles.map((o) => <li key={o.id}>{dateTime(o.publishedAtUtc)} · {o.publisher}: {o.headline}</li>)}
                </ul>
              </div>
            )}
            <ul className="list-disc space-y-0.5 pl-4 text-xs text-muted-foreground">
              {c.limitations.map((l) => <li key={l}>{l}</li>)}
            </ul>
            <p className="rounded-md border p-2 text-xs">{c.disclaimer}</p>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd>{value}</dd>
    </div>
  )
}
