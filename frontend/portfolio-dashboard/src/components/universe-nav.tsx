import { useState } from 'react'
import { ChevronDown } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { LoadingBlock } from '@/components/common'
import { regions, type Region } from '@/lib/markets'
import type { Market, Sector } from '@/lib/types'
import { marketCount, marketsOf, type UniverseScope } from '@/lib/universe-scope'
import { cn } from '@/lib/utils'

/**
 * Region → market → index checkboxes, then sector checkboxes, for a large stock universe. Any number can be ticked: a
 * region ticks all its markets (a dash shows a region that is partly chosen), a market includes all its indices, and an
 * index adds only its members. Regions fold so 37 markets stay one short list; "Find a market" narrows the list. Every
 * entry shows how many stocks it contains; the sector counts follow the chosen markets.
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
  const chosen = new Set(scope.countries)
  const chosenIndices = new Set(scope.indices)
  const partly = (r: Region) => marketsOf(markets ?? [], r).some((m) => chosen.has(m.country) || m.indices.some((i) => chosenIndices.has(i.code)))
  // Regions with a choice start open; the others fold until clicked.
  const [open, setOpen] = useState<Set<Region>>(() => new Set(regions.filter(partly)))
  const [openMarkets, setOpenMarkets] = useState<Set<string>>(() => new Set(markets?.filter((m) => m.indices.some((i) => chosenIndices.has(i.code))).map((m) => m.country) ?? []))
  const [find, setFind] = useState('')
  const term = find.trim().toLowerCase()
  const flip = <T,>(set: Set<T>, v: T) => { const n = new Set(set); if (n.has(v)) n.delete(v); else n.add(v); return n }

  const matches = (m: Market) => !term || m.name.toLowerCase().includes(term) || m.country.toLowerCase() === term
    || m.indices.some((i) => i.name.toLowerCase().includes(term) || i.code.toLowerCase().includes(term))

  const toggleRegion = (inRegion: Market[], all: boolean) => {
    const codes = new Set(inRegion.map((m) => m.country))
    const indexCodes = new Set(inRegion.flatMap((m) => m.indices.map((i) => i.code)))
    onChange({
      ...scope,
      countries: all ? scope.countries.filter((c) => !codes.has(c)) : [...new Set([...scope.countries, ...codes])],
      // A whole market covers its indices, so they leave the list either way.
      indices: scope.indices.filter((i) => !indexCodes.has(i)),
    })
  }
  const toggleMarket = (m: Market) => {
    const own = new Set(m.indices.map((i) => i.code))
    onChange({
      ...scope,
      countries: chosen.has(m.country) ? scope.countries.filter((c) => c !== m.country) : [...scope.countries, m.country],
      indices: scope.indices.filter((i) => !own.has(i)),
    })
  }
  const toggleIndex = (code: string) =>
    onChange({ ...scope, indices: chosenIndices.has(code) ? scope.indices.filter((i) => i !== code) : [...scope.indices, code] })
  const toggleSector = (name: string) =>
    onChange({ ...scope, sectors: scope.sectors.includes(name) ? scope.sectors.filter((s) => s !== name) : [...scope.sectors, name] })

  const placesChosen = scope.countries.length + scope.indices.length

  return (
    <nav aria-label="Markets, indices and sectors" className="space-y-4">
      <div className="space-y-1">
        <div className="flex items-center justify-between gap-2 px-2.5">
          <h2 className="text-[11px] font-semibold tracking-wide text-muted-foreground uppercase">Market &amp; index</h2>
          {placesChosen > 0 && (
            <Button variant="ghost" size="xs" onClick={() => onChange({ ...scope, countries: [], indices: [] })}>Clear</Button>
          )}
        </div>
        <p className="px-2.5 text-[11px] leading-snug text-muted-foreground">
          {placesChosen === 0
            ? `All markets · ${markets?.reduce((n, m) => n + marketCount(m), 0) ?? '…'} stocks. Tick any regions, markets or indices.`
            : 'Stocks in any ticked market or index.'}
        </p>
        <div className="px-1 pt-1">
          <Input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find a market or index" aria-label="Find a market or index" className="h-8 text-sm" />
        </div>
        {loading && <LoadingBlock height="h-48" />}
        {markets && regions.map((region) => {
          const inRegion = marketsOf(markets, region)
          const visible = inRegion.filter(matches)
          if (visible.length === 0) return null
          const all = inRegion.every((m) => chosen.has(m.country))
          const some = !all && partly(region)
          const isOpen = !!term || open.has(region)
          return (
            <div key={region}>
              <div className="flex items-center">
                <FoldButton open={isOpen} label={region} onClick={() => setOpen((o) => flip(o, region))} />
                <CheckRow label={region} checked={all} indeterminate={some} onToggle={() => toggleRegion(inRegion, all)}
                  count={inRegion.reduce((n, m) => n + marketCount(m), 0)} strong />
              </div>
              {isOpen && (
                <div className="mt-0.5 ml-3 space-y-0.5 border-l pl-1.5">
                  {visible.map((m) => {
                    const marketChosen = chosen.has(m.country)
                    const hasLevel = m.indices.length > 1
                    const indexChosen = m.indices.some((i) => chosenIndices.has(i.code))
                    const showIndices = hasLevel && (!!term || openMarkets.has(m.country))
                    return (
                      <div key={m.country}>
                        <div className="flex items-center">
                          {hasLevel
                            ? <FoldButton open={showIndices} label={`${m.name} indices`} onClick={() => setOpenMarkets((o) => flip(o, m.country))} />
                            : <span className="w-[22px] shrink-0" aria-hidden />}
                          <CheckRow label={m.name} checked={marketChosen} indeterminate={!marketChosen && indexChosen} onToggle={() => toggleMarket(m)} count={marketCount(m)} />
                        </div>
                        {/* A market with one index needs no second level. */}
                        {showIndices && (
                          <div className="mt-0.5 ml-6 space-y-0.5 border-l pl-1.5">
                            {m.indices.map((i) => (
                              <CheckRow
                                key={i.code}
                                label={i.name}
                                checked={marketChosen || chosenIndices.has(i.code)}
                                disabled={marketChosen}
                                title={marketChosen ? `Included: all of ${m.name} is ticked` : i.description ?? undefined}
                                onToggle={() => toggleIndex(i.code)}
                                count={i.members}
                                note={i.isHistorical ? 'historical' : undefined}
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
        {markets && term && !markets.some(matches) && <p className="px-2.5 text-xs text-muted-foreground">No market or index matches “{find.trim()}”.</p>}
      </div>

      <div className="space-y-1">
        <div className="flex items-center justify-between gap-2 px-2.5">
          <h2 className="text-[11px] font-semibold tracking-wide text-muted-foreground uppercase">Sector</h2>
          {scope.sectors.length > 0 && <Button variant="ghost" size="xs" onClick={() => onChange({ ...scope, sectors: [] })}>Clear</Button>}
        </div>
        <p className="px-2.5 text-[11px] leading-snug text-muted-foreground">
          {scope.sectors.length === 0 ? 'All sectors. Tick one or more to narrow the list.' : 'Stocks of any ticked sector.'}
        </p>
        {sectors?.map((s) => (
          <CheckRow
            key={s.name}
            label={s.name}
            checked={scope.sectors.includes(s.name)}
            onToggle={() => toggleSector(s.name)}
            count={s.stockCount}
            title={`${s.stockCount} stocks, ${s.withPriceData} with stored daily history`}
          />
        ))}
        {/* A ticked sector with no stocks in the chosen markets stays visible, so it can be unticked. */}
        {scope.sectors.filter((n) => !sectors?.some((s) => s.name === n)).map((n) => (
          <CheckRow key={n} label={n} checked onToggle={() => toggleSector(n)} count={0} />
        ))}
        <p className="px-2.5 pt-1 text-[11px] leading-snug text-muted-foreground">
          Sectors are GICS-style groups mapped from each index&apos;s own classification; the original industry is shown per stock.
        </p>
      </div>
    </nav>
  )
}

function FoldButton({ open, label, onClick }: { open: boolean; label: string; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} aria-expanded={open} aria-label={`${open ? 'Fold' : 'Show'} ${label}`}
      className="shrink-0 rounded-md p-1 text-muted-foreground hover:bg-muted focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none">
      <ChevronDown className={cn('size-3.5 transition-transform', !open && '-rotate-90')} aria-hidden />
    </button>
  )
}

function CheckRow({ label, checked, indeterminate, disabled, onToggle, count, title, note, strong }: {
  label: string
  checked: boolean
  indeterminate?: boolean
  disabled?: boolean
  onToggle: () => void
  count?: number
  title?: string
  note?: string
  strong?: boolean
}) {
  return (
    <label
      title={title}
      className={cn(
        'flex w-full min-w-0 cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition-colors hover:bg-muted',
        disabled && 'cursor-default opacity-70 hover:bg-transparent',
        checked || indeterminate ? 'font-medium text-foreground' : strong ? 'font-medium text-foreground' : 'text-muted-foreground',
      )}
    >
      <Checkbox checked={checked} indeterminate={indeterminate} disabled={disabled} onCheckedChange={onToggle} aria-label={label} />
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {note && <span className="text-[10px] font-normal text-muted-foreground">{note}</span>}
      {count !== undefined && <Badge variant="secondary" className="h-5 shrink-0 px-1.5 text-[10px] tabular">{count}</Badge>}
    </label>
  )
}
