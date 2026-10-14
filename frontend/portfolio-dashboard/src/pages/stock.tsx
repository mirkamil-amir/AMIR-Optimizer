import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { ArrowLeft, Minus, Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { QuoteProvenance, RefreshQuoteButton } from '@/components/stock-selection'
import { ChangeText, PriceHistoryCard, WatchToggle } from '@/components/watchlist'
import { CompanyNews } from '@/components/news'
import { date, dateTime, money, num } from '@/lib/format'
import { useStock } from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import type { HistoryRange } from '@/lib/types'

/** Security page: latest price with provenance, price history by range, metadata and company news. */
export function StockPage() {
  const { symbol = '' } = useParams()
  const detail = useStock(symbol)
  const selection = useSelection()
  const [range, setRange] = useState<HistoryRange>('1Y')
  const s = detail.data?.stock

  if (detail.error) return <ErrorState error={detail.error} onRetry={() => detail.refetch()} />
  if (!s || !detail.data) return <LoadingBlock />

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" render={<Link to="/watchlist" />} className="-ml-2"><ArrowLeft /> Watchlist</Button>
      <PageHeader
        title={`${s.symbol} · ${s.name ?? ''}`}
        description={[s.sector ?? 'Unclassified', s.industry, s.exchange, s.currency].filter(Boolean).join(' · ')}
        actions={
          <>
            <RefreshQuoteButton symbol={s.symbol} size="sm" />
            <WatchToggle symbol={s.symbol} />
            <Button size="sm" variant={selection.has(s.symbol) ? 'outline' : 'default'}
              onClick={() => selection.toggle({ symbol: s.symbol, name: s.name, sector: s.sector, currency: s.currency })}>
              {selection.has(s.symbol) ? <Minus /> : <Plus />}
              {selection.has(s.symbol) ? 'Remove from selection' : 'Add to selection'}
            </Button>
          </>
        }
      />

      <Card className="py-4">
        <CardContent className="flex flex-wrap items-end justify-between gap-4 px-4">
          <div>
            <div className="text-3xl font-semibold tabular">{s.quote.price !== null ? money(s.quote.price, s.quote.currency) : 'No price available'}</div>
            {s.quote.changePercent !== null && (
              <div className="text-sm">
                <ChangeText value={s.quote.changePercent} /> <span className="text-muted-foreground">vs previous close
                  {s.quote.previousClose !== null && ` ${money(s.quote.previousClose, s.quote.currency)}`}
                  {s.quote.previousCloseDate && ` (${date(s.quote.previousCloseDate)})`}</span>
              </div>
            )}
            <div className="mt-1"><QuoteProvenance quote={s.quote} /></div>
          </div>
          <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:grid-cols-3">
            <Item label="Price applies to" value={s.quote.asOfUtc ? dateTime(s.quote.asOfUtc) : date(s.quote.priceDate)} />
            <Item label="Retrieved" value={s.quote.retrievedAtUtc ? dateTime(s.quote.retrievedAtUtc) : '—'} />
            <Item label="Currency" value={s.currency} />
            <Item label="Listing (MIC)" value={[s.exchange, detail.data.mic].filter(Boolean).join(' · ') || '—'} />
            <Item label="ISIN" value={detail.data.isin ?? 'Not provided by the source'} />
            <Item label="Stored history" value={s.observations > 0 ? `${num(s.observations, 0)} days from ${date(s.firstDate)}` : 'None yet'} />
          </dl>
        </CardContent>
      </Card>

      <PriceHistoryCard symbol={s.symbol} range={range} onRangeChange={setRange} />

      <CompanyNews symbol={s.symbol} />

      <section className="space-y-1.5">
        <h2 className="text-sm font-semibold">About</h2>
        {detail.data.description && <p className="text-sm leading-relaxed">{detail.data.description}</p>}
        <p className="text-xs text-muted-foreground">{detail.data.descriptionNote}</p>
        {detail.data.metadataSource && (
          <p className="text-xs text-muted-foreground">Classification and membership source: {detail.data.metadataSource}.</p>
        )}
      </section>
    </div>
  )
}

function Item({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="tabular">{value}</dd>
    </div>
  )
}
