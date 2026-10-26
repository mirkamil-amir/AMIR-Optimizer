import { useState } from 'react'
import { ChevronDown } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { LoadingBlock } from '@/components/common'
import { regionOf, regions, type Region } from '@/lib/markets'
import type { Market, Sector } from '@/lib/types'
import { cn } from '@/lib/utils'

/**
 * What the stock list is narrowed to. `country` is one market or – for a region – several, comma-separated (the API
 * accepts both); `region` names the region when a whole region is chosen.
 */
export interface UniverseScope {
  region?: Region
  country?: string
  index?: string
  sector?: string
}

/** Stocks of a market (members of its current indices; the historical DAX 30 is not counted twice). */
/** Stocks and ETFs of a market, each once – the rows the chooser lists for it (index members summed only as a fallback). */
export function marketCount(m: Market) {
  return m.instruments || m.indices.filter((i) => !i.isHistorical).reduce((n, i) => n + i.members, 0)
}

/**
 * Region → market → index navigation, then sectors, for a large stock universe. Regions fold, so 36 markets stay one
 * short list; a region, a market or an index can each be chosen, and the sector list narrows to that choice. Every level
 * shows how many stocks it contains.
 */
export function UniverseNav({
  markets, sectors, scope, onChange, loading,
}: {
  markets: Market[] | undefined
  sectors: Sector[] | undefined
  scope: UniverseScope
  onChange: (scope: UniverseScope) => void
  loading: boolean
}) {
  const selectedCountry = scope.country && !scope.country.includes(',') ? scope.country : undefined
  const activeRegion = scope.region ?? (selectedCountry ? regionOf(selectedCountry) : undefined)
  // The region of the current choice is open; the others fold until clicked.
  const [open, setOpen] = useState<Set<Region>>(() => new Set(activeRegion ? [activeRegion] : []))
  const toggle = (r: Region) => setOpen((o) => { const n = new Set(o); if (n.has(r)) n.delete(r); else n.add(r); return n })

  return (
    <nav aria-label="Markets, indices and sectors" className="space-y-4">
      <div className="space-y-1">
        <h2 className="px-2.5 text-[11px] font-semibold tracking-wide text-muted-foreground uppercase">Market &amp; index</h2>
        <NavButton active={!scope.country && !scope.index} onClick={() => onChange({})} label="All markets"
          count={markets?.reduce((n, m) => n + marketCount(m), 0)} />
        {loading && <LoadingBlock height="h-48" />}
        {markets && regions.map((region) => {
          const inRegion = markets.filter((m) => regionOf(m.country) === region)
          if (inRegion.length === 0) return null
          const isOpen = open.has(region) || activeRegion === region
          return (
            <div key={region}>
              <div className="flex items-center">
                <button type="button" onClick={() => toggle(region)} aria-expanded={isOpen} aria-label={`${isOpen ? 'Fold' : 'Show'} ${region}`}
                  className="rounded-md p-1 text-muted-foreground hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none">
                  <ChevronDown className={cn('size-3.5 transition-transform', !isOpen && '-rotate-90')} aria-hidden />
                </button>
                <NavButton
                  active={scope.region === region && !scope.index}
                  onClick={() => { onChange({ region, country: inRegion.map((m) => m.country).join(',') }); setOpen((o) => new Set(o).add(region)) }}
                  label={region}
                  count={inRegion.reduce((n, m) => n + marketCount(m), 0)}
                  strong
                />
              </div>
              {isOpen && (
                <div className="mt-0.5 ml-3 space-y-0.5 border-l pl-1.5">
                  {inRegion.map((m) => {
                    const marketActive = selectedCountry === m.country && !scope.index
                    const expanded = selectedCountry === m.country || m.indices.some((i) => i.code === scope.index)
                    return (
                      <div key={m.country}>
                        <NavButton active={marketActive} onClick={() => onChange({ country: m.country })} label={m.name}
                          count={marketCount(m)} ariaExpanded={m.indices.length > 1 ? expanded : undefined} />
                        {/* A market with one index needs no second level. */}
                        {expanded && m.indices.length > 1 && (
                          <div className="mt-0.5 ml-3 space-y-0.5 border-l pl-1.5">
                            {m.indices.map((i) => (
                              <NavButton
                                key={i.code}
                                active={scope.index === i.code}
                                onClick={() => onChange({ country: m.country, index: i.code })}
                                label={i.name}
                                title={i.description ?? undefined}
                                count={i.members}
                                icon={i.isHistorical ? <span className="text-[10px] text-muted-foreground">historical</span> : undefined}
                              />
                            ))}
                          </div>
                        )}
                      </div>
                    )
                  })}
                </div>
              )}
            </div>
          )
        })}
      </div>

      <div className="space-y-1">
        <h2 className="px-2.5 text-[11px] font-semibold tracking-wide text-muted-foreground uppercase">Sector</h2>
        <NavButton active={!scope.sector} onClick={() => onChange({ ...scope, sector: undefined })} label="All sectors" count={sectors?.reduce((n, s) => n + s.stockCount, 0)} />
        {sectors?.map((s) => (
          <NavButton
            key={s.name}
            active={scope.sector === s.name}
            onClick={() => onChange({ ...scope, sector: s.name })}
            label={s.name}
            count={s.stockCount}
            title={`${s.stockCount} stocks, ${s.withPriceData} with stored daily history`}
          />
        ))}
        <p className="px-2.5 pt-1 text-[11px] leading-snug text-muted-foreground">
          Sectors are GICS-style groups mapped from each index&apos;s own classification; the original industry is shown per stock.
        </p>
      </div>
    </nav>
  )
}

function NavButton({ active, onClick, label, count, icon, title, ariaExpanded, strong }: {
  active: boolean
  onClick: () => void
  label: string
  count?: number
  icon?: React.ReactNode
  title?: string
  ariaExpanded?: boolean
  strong?: boolean
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      aria-current={active ? 'true' : undefined}
      aria-expanded={ariaExpanded}
      className={cn(
        'flex w-full min-w-0 items-center gap-1.5 rounded-md px-2.5 py-1.5 text-left text-sm transition-colors hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none',
        active ? 'bg-muted font-medium text-foreground' : strong ? 'font-medium text-foreground' : 'text-muted-foreground',
      )}
    >
      {icon}
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {count !== undefined && <Badge variant="secondary" className="h-5 shrink-0 px-1.5 text-[10px] tabular">{count}</Badge>}
    </button>
  )
}
