import { AlertTriangle, ExternalLink, FileQuestion } from 'lucide-react'
import { DataBadge, ErrorState, InfoTip } from '@/components/common'
import { Skeleton } from '@/components/ui/skeleton'
import { date } from '@/lib/format'
import { useCompanyProfile } from '@/lib/queries'
import { cn } from '@/lib/utils'

/**
 * Company description from its source (with attribution, licence and retrieval date), or an explicit explanation of why
 * none is available. Independent of the price chart: a failure here never affects the chart and vice versa.
 */
export function CompanyOverview({ symbol, className }: { symbol: string; className?: string }) {
  const profile = useCompanyProfile(symbol)
  // Only render the profile that belongs to the stock currently shown.
  const p = profile.data?.symbol === symbol ? profile.data : undefined

  return (
    <section aria-labelledby={`overview-${symbol}`} className={cn('space-y-2', className)}>
      <h3 id={`overview-${symbol}`} className="flex items-center gap-1.5 text-sm font-semibold">
        Company overview
        <InfoTip label="About the company overview">
          The lead section of the company's encyclopedia article linked from the index constituent list. Text is shown as published by the
          source, never generated.
        </InfoTip>
      </h3>
      {profile.error ? (
        <ErrorState error={profile.error} onRetry={() => profile.refetch()} />
      ) : !p ? (
        <div className="space-y-2" aria-busy="true" aria-label={`Loading the company overview of ${symbol}`}>
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-11/12" />
          <Skeleton className="h-4 w-4/5" />
        </div>
      ) : p.status === 'Available' && p.description ? (
        <div className="space-y-2">
          <p className="max-w-prose text-sm leading-relaxed">{p.description}</p>
          <p className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-xs text-muted-foreground">
            {p.isSimulated && <DataBadge kind="simulated" />}
            <span>Source:</span>
            {p.sourceUrl ? (
              <a href={p.sourceUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-0.5 underline underline-offset-2 hover:text-foreground">
                {p.sourceName ?? 'source'} article <ExternalLink className="size-3" aria-label="(opens in a new tab)" />
              </a>
            ) : <span>{p.sourceName}</span>}
            {p.licence && <span>· {p.licence}</span>}
            {p.retrievedAtUtc && <span>· retrieved {date(p.retrievedAtUtc)}</span>}
            {p.language && p.language !== 'en' && <span>· language: {p.language}</span>}
          </p>
          {p.isStale && p.message && (
            <p className="flex items-start gap-1.5 text-xs text-warn"><AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden />{p.message}</p>
          )}
        </div>
      ) : (
        <div className={cn('flex items-start gap-2 rounded-md border border-dashed p-3 text-sm', p.status === 'Error' && 'border-warn/50')} role="status">
          {p.status === 'Error' ? <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warn" aria-hidden /> : <FileQuestion className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden />}
          <div>
            <p className="font-medium">{p.status === 'Error' ? 'Company description temporarily unavailable' : 'Company description is currently unavailable.'}</p>
            {p.message && <p className="text-xs text-muted-foreground">{p.message}</p>}
          </div>
        </div>
      )}
    </section>
  )
}
