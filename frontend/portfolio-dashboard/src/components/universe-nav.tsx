import { Badge } from '@/components/ui/badge'
import { LoadingBlock } from '@/components/common'
import type { Market, Sector } from '@/lib/types'
import { cn } from '@/lib/utils'

export interface UniverseScope {
  country?: string
  index?: string
  sector?: string
}

/**
 * Market → index → sector navigation for a large stock universe. Choosing a market or index narrows the sector list to
 * that scope; every level shows how many stocks it contains.
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
  return (
    <nav aria-label="Markets, indices and sectors" className="space-y-4">
      <div className="space-y-1">
        <h2 className="px-2.5 text-[11px] font-semibold tracking-wide text-muted-foreground uppercase">Market &amp; index</h2>
        <NavButton active={!scope.country && !scope.index} onClick={() => onChange({})} label="All markets" />
        {loading && <LoadingBlock height="h-48" />}
        {markets?.map((m) => {
          const marketActive = scope.country === m.country && !scope.index
          const open = scope.country === m.country || m.indices.some((i) => i.code === scope.index)
          return (
            <div key={m.country}>
              <NavButton
                active={marketActive}
                onClick={() => onChange({ country: m.country })}
                label={m.name}
                count={m.indices.filter((i) => !i.isHistorical).reduce((n, i) => n + i.members, 0)}
                ariaExpanded={open}
              />
              {open && (
                <div className="mt-0.5 ml-4 space-y-0.5 border-l pl-2">
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

function NavButton({ active, onClick, label, count, icon, title, ariaExpanded }: {
  active: boolean
  onClick: () => void
  label: string
  count?: number
  icon?: React.ReactNode
  title?: string
  ariaExpanded?: boolean
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      aria-current={active ? 'true' : undefined}
      aria-expanded={ariaExpanded}
      className={cn(
        'flex w-full items-center gap-1.5 rounded-md px-2.5 py-1.5 text-left text-sm transition-colors hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none',
        active ? 'bg-muted font-medium text-foreground' : 'text-muted-foreground',
      )}
    >
      {icon}
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {count !== undefined && <Badge variant="secondary" className="h-5 shrink-0 px-1.5 text-[10px] tabular">{count}</Badge>}
    </button>
  )
}
