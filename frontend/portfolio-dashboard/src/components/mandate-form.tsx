import { useEffect, useMemo, useRef, useState } from 'react'
import { X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { InfoTip } from '@/components/common'
import { StockChooser } from '@/components/stock-chooser'
import { useMarkets, useSectors } from '@/lib/queries'
import { SelectionProvider, useSelection } from '@/lib/selection'
import { MAX_INDEXES, MAX_SYMBOLS, DEFAULT_LIMIT, type MandateFormState } from '@/lib/mandate'
import type { Market, PilotFrequency } from '@/lib/types'
import { cn } from '@/lib/utils'

// The Matrix Pilot mandate form (set-up and "Change mandate", docs/matrix-pilot.md › Mandate). Everything that names
// something the platform knows – indexes, stocks, sectors, countries, currencies, benchmarks – is chosen from the catalogue
// (checkboxes, the Forge stock chooser, lists); only numbers are typed. The server validates the mandate again and
// rejects an impossible one instead of adjusting it.

export function MandateForm({ form, onChange }: { form: MandateFormState; onChange: (f: MandateFormState) => void }) {
  const markets = useMarkets()
  const sectors = useSectors()
  const patch = (p: Partial<MandateFormState>) => onChange({ ...form, ...p })
  const number = (k: keyof MandateFormState, label: string, hint?: string, suffix = '%') => (
    <div className="space-y-1">
      <Label htmlFor={`m-${k}`}><InfoTip of={label}>{hint}</InfoTip></Label>
      <div className="relative">
        <Input id={`m-${k}`} value={String(form[k])} onChange={(e) => patch({ [k]: e.target.value })} inputMode="decimal" className={suffix ? 'pr-8' : undefined} />
        {suffix && <span className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-xs text-muted-foreground" aria-hidden>{suffix}</span>}
      </div>
    </div>
  )
  const marketList = markets.data ?? []
  const countryItems = marketList.map((m) => ({ key: m.country, label: m.name })).sort((a, b) => a.label.localeCompare(b.label))
  const currencyItems = [...new Set(marketList.flatMap((m) => m.indices.map((i) => i.currency)))].sort().map((c) => ({ key: c, label: c }))
  const sectorItems = (sectors.data ?? []).map((s) => ({ key: s.name, label: s.name }))

  return (
    <div className="space-y-6">
      <FormGroup title="Where the AI may invest" hint="The AI may hold members of the chosen indexes and the chosen stocks – never an excluded stock.">
        <IndexChecklist markets={marketList} loading={markets.isLoading} value={form.indexes} onChange={(indexes) => patch({ indexes })} />
        <div className="grid gap-4 md:grid-cols-2">
          <SymbolPicker label="Allowed stocks" hint="Stocks the AI may hold in addition to the index members." value={form.universe}
            onChange={(universe) => patch({ universe, excluded: form.excluded.filter((s) => !universe.includes(s)) })} />
          <SymbolPicker label="Excluded stocks" hint="Never held, whatever the AI proposes." value={form.excluded}
            onChange={(excluded) => patch({ excluded, universe: form.universe.filter((s) => !excluded.includes(s)) })} />
        </div>
      </FormGroup>

      <FormGroup title="Positions, cash and trading">
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          {number('maxPos', 'Max position %')}
          {number('minPos', 'Min position %')}
          {number('minHoldings', 'Min holdings', undefined, '')}
          {number('maxHoldings', 'Max holdings', undefined, '')}
          {number('minCash', 'Min cash %')}
          {number('maxCash', 'Max cash %')}
          {number('maxTurnover', 'Max turnover %', 'One-way turnover per decision, once the portfolio holds positions.')}
          {number('minTrade', 'Min trade value', 'Smallest order, in the portfolio currency.', '')}
        </div>
      </FormGroup>

      <FormGroup title="Risk and benchmark">
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          {number('maxVol', 'Max volatility %', 'Ceiling for the proposal\'s ex-ante annualized volatility (empty = none).')}
          {number('drawdown', 'Drawdown pause %', 'Matrix Pilot pauses itself when its live record falls this far from its peak.')}
          <div className="col-span-2">
            <BenchmarkSelect markets={marketList} value={form.benchmark} onChange={(benchmark) => patch({ benchmark })} />
          </div>
        </div>
      </FormGroup>

      <FormGroup title="Exposure limits" hint="Tick what to limit and set its largest share of the portfolio. Unticked = no limit.">
        <div className="grid gap-4 md:grid-cols-3">
          <LimitChecklist title="Sectors" items={sectorItems} loading={sectors.isLoading} value={form.sectors} onChange={(s) => patch({ sectors: s })} />
          <LimitChecklist title="Countries" items={countryItems} loading={markets.isLoading} value={form.countries} onChange={(c) => patch({ countries: c })} />
          <LimitChecklist title="Currencies" items={currencyItems} loading={markets.isLoading} value={form.currencies} onChange={(c) => patch({ currencies: c })} />
        </div>
      </FormGroup>

      <FormGroup title="Decisions">
        <div className="flex flex-wrap gap-6">
          <Segmented label="Decision frequency" value={form.frequency} options={['Daily', 'Weekly', 'Monthly']} onChange={(v) => patch({ frequency: v as PilotFrequency })} />
          <Segmented label="Model effort" value={form.effort} options={['low', 'medium', 'high', 'xhigh', 'max']} onChange={(v) => patch({ effort: v })}
            note="Higher effort means more thorough model reasoning and higher cost per decision." />
        </div>
      </FormGroup>
      <p className="text-xs text-muted-foreground">Long-only, no leverage: the platform supports nothing else. The server rejects an impossible mandate instead of adjusting it.</p>
    </div>
  )
}

function FormGroup({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <fieldset className="space-y-3">
      <legend className="mb-2 text-sm font-semibold">{hint ? <InfoTip of={title}>{hint}</InfoTip> : title}</legend>
      {children}
    </fieldset>
  )
}

/** Indexes as checkboxes, grouped by market (at most 10, as the server allows). */
function IndexChecklist({ markets, loading, value, onChange }: { markets: Market[]; loading: boolean; value: string[]; onChange: (v: string[]) => void }) {
  const [filter, setFilter] = useState('')
  const q = filter.trim().toLowerCase()
  const groups = markets
    .map((m) => ({ market: m, indices: m.indices.filter((i) => !q || i.name.toLowerCase().includes(q) || i.code.toLowerCase().includes(q) || m.name.toLowerCase().includes(q)) }))
    .filter((g) => g.indices.length > 0)
    .sort((a, b) => a.market.name.localeCompare(b.market.name))
  const toggle = (code: string, on: boolean) => onChange(on ? [...value, code] : value.filter((c) => c !== code))
  const full = value.length >= MAX_INDEXES
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-sm font-medium">Allowed indexes <span className="font-normal text-muted-foreground">· {value.length} of at most {MAX_INDEXES}</span></span>
        <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter markets or indexes" aria-label="Filter indexes" className="h-8 w-56" />
      </div>
      <div className="max-h-56 overflow-y-auto rounded-lg border p-3" role="group" aria-label="Allowed indexes">
        {loading ? <p className="text-sm text-muted-foreground">Loading markets…</p> : groups.length === 0 ? <p className="text-sm text-muted-foreground">No index matches.</p> : (
          <div className="grid gap-x-6 gap-y-3 sm:grid-cols-2">
            {groups.map(({ market, indices }) => (
              <div key={market.country} className="space-y-1">
                <p className="text-xs font-medium tracking-wide text-muted-foreground uppercase">{market.name}</p>
                {indices.map((i) => {
                  const checked = value.includes(i.code)
                  return (
                    <label key={i.code} className={cn('flex items-center gap-2 text-sm', !checked && full && 'opacity-50')}>
                      <Checkbox checked={checked} disabled={!checked && full} onCheckedChange={(c) => toggle(i.code, c === true)} aria-label={i.name} />
                      <span>{i.name}</span>
                      <span className="text-xs text-muted-foreground">{i.members} stocks{i.isHistorical ? ' · historical' : ''}</span>
                    </label>
                  )
                })}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

/** Chosen stocks as removable chips; "Choose stocks" opens the Forge stock chooser with its own selection. */
function SymbolPicker({ label, hint, value, onChange }: { label: string; hint: string; value: string[]; onChange: (v: string[]) => void }) {
  const [open, setOpen] = useState(false)
  const shown = value.slice(0, 40)
  return (
    <div className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <span className="text-sm font-medium"><InfoTip of={label}>{hint}</InfoTip> <span className="font-normal text-muted-foreground">· {value.length}</span></span>
        <div className="flex gap-1">
          {value.length > 0 && <Button type="button" size="xs" variant="ghost" onClick={() => onChange([])}>Clear</Button>}
          <Button type="button" size="xs" variant="outline" onClick={() => setOpen(true)}>Choose stocks</Button>
        </div>
      </div>
      <div className="flex min-h-10 flex-wrap gap-1 rounded-lg border p-2" aria-label={label} role="list">
        {value.length === 0 ? <span className="text-sm text-muted-foreground">None</span> : shown.map((s) => (
          <Badge key={s} variant="secondary" role="listitem" className="gap-1 pr-1">
            {s}
            <button type="button" aria-label={`Remove ${s}`} onClick={() => onChange(value.filter((x) => x !== s))} className="rounded-sm hover:text-foreground">
              <X className="size-3" aria-hidden />
            </button>
          </Badge>
        ))}
        {value.length > shown.length && <span className="self-center text-xs text-muted-foreground">and {value.length - shown.length} more</span>}
      </div>
      {open && <StockPickerDialog title={label} initial={value} onUse={(v) => { onChange(v); setOpen(false) }} onClose={() => setOpen(false)} />}
    </div>
  )
}

/**
 * The Forge stock chooser over the window, with a selection of its own (seeded with the mandate's stocks), so the
 * user's temporary selection elsewhere in the app is left alone.
 */
function StockPickerDialog({ title, initial, onUse, onClose }: { title: string; initial: string[]; onUse: (v: string[]) => void; onClose: () => void }) {
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[94dvh] overflow-y-auto sm:max-w-[min(96vw,80rem)]">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>Tick stocks in the list (filter by market, index and sector on the left), then use them. At most {MAX_SYMBOLS}.</DialogDescription>
        </DialogHeader>
        <SelectionProvider>
          <SeedSelection symbols={initial} />
          {/* No tray actions: the default ones would apply the selection to an optimization; "Use" is in the footer. */}
          <StockChooser trayActions={<></>} />
          <UseSelectionFooter onUse={onUse} onClose={onClose} />
        </SelectionProvider>
      </DialogContent>
    </Dialog>
  )
}

function SeedSelection({ symbols }: { symbols: string[] }) {
  const { addMany } = useSelection()
  const seeded = useRef(false)
  useEffect(() => {
    if (seeded.current) return
    seeded.current = true
    addMany(symbols.map((symbol) => ({ symbol, name: null, sector: null })))
  }, [symbols, addMany])
  return null
}

/** "Use" also with an empty selection (the tray hides itself then), so a list can be emptied here too. */
function UseSelectionFooter({ onUse, onClose }: { onUse: (v: string[]) => void; onClose: () => void }) {
  const selection = useSelection()
  return (
    <div className="flex justify-end gap-2 border-t pt-3">
      <Button variant="ghost" onClick={onClose}>Cancel</Button>
      <Button disabled={selection.count > MAX_SYMBOLS} onClick={() => onUse(selection.symbols)}>
        Use {selection.count} stock{selection.count === 1 ? '' : 's'}
      </Button>
    </div>
  )
}

/** The benchmark from the indexes' benchmark symbols, or the portfolio's own. */
function BenchmarkSelect({ markets, value, onChange }: { markets: Market[]; value: string; onChange: (v: string) => void }) {
  const options = useMemo(() => {
    const seen = new Map<string, string>()
    for (const m of markets) for (const i of m.indices) if (i.benchmarkSymbol && !seen.has(i.benchmarkSymbol)) seen.set(i.benchmarkSymbol, `${i.name} (${i.benchmarkSymbol})`)
    const list = [...seen].map(([v, l]) => ({ value: v, label: l })).sort((a, b) => a.label.localeCompare(b.label))
    if (value && !seen.has(value)) list.unshift({ value, label: value })
    return [{ value: 'portfolio', label: 'The portfolio\'s benchmark' }, ...list]
  }, [markets, value])
  return (
    <div className="space-y-1">
      <Label htmlFor="m-benchmark"><InfoTip of="Benchmark">The live record is compared with it.</InfoTip></Label>
      <Select value={value || 'portfolio'} onValueChange={(v) => v && onChange(v === 'portfolio' ? '' : String(v))} items={options}>
        <SelectTrigger id="m-benchmark" className="w-full" aria-label="Benchmark"><SelectValue /></SelectTrigger>
        <SelectContent className="max-h-80">{options.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
      </Select>
    </div>
  )
}

/** A limit per item: tick to limit it, then set its largest share (percent). */
function LimitChecklist({ title, items, loading, value, onChange }: {
  title: string; items: { key: string; label: string }[]; loading: boolean; value: Record<string, string>; onChange: (v: Record<string, string>) => void
}) {
  // Limits on items the catalogue no longer lists (e.g. an older mandate) stay visible and removable.
  const all = [...items, ...Object.keys(value).filter((k) => !items.some((i) => i.key === k)).map((k) => ({ key: k, label: k }))]
  const set = (key: string, on: boolean) => {
    const next = { ...value }
    if (on) next[key] = value[key] ?? DEFAULT_LIMIT
    else delete next[key]
    onChange(next)
  }
  const chosen = Object.keys(value).length
  return (
    <div className="space-y-2">
      <span className="text-sm font-medium">{title} <span className="font-normal text-muted-foreground">· {chosen === 0 ? 'no limit' : `${chosen} limited`}</span></span>
      <div className="max-h-56 space-y-1 overflow-y-auto rounded-lg border p-2" role="group" aria-label={`${title} limits`}>
        {loading ? <p className="text-sm text-muted-foreground">Loading…</p> : all.map((i) => {
          const checked = i.key in value
          return (
            <div key={i.key} className="flex min-h-8 items-center gap-2 text-sm">
              <Checkbox id={`limit-${title}-${i.key}`} checked={checked} onCheckedChange={(c) => set(i.key, c === true)} aria-label={`Limit ${i.label}`} />
              <label htmlFor={`limit-${title}-${i.key}`} className="min-w-0 flex-1 truncate">{i.label}</label>
              {checked && (
                <div className="relative w-20">
                  <Input value={value[i.key]} onChange={(e) => onChange({ ...value, [i.key]: e.target.value })} inputMode="decimal"
                    aria-label={`Largest share of ${i.label} (%)`} className="h-7 pr-6 text-right" />
                  <span className="pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 text-xs text-muted-foreground" aria-hidden>%</span>
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}

/** A small set of choices as one row of toggle buttons. */
function Segmented({ label, value, options, onChange, note }: { label: string; value: string; options: string[]; onChange: (v: string) => void; note?: string }) {
  return (
    <div className="space-y-1">
      <span className="text-sm font-medium">{label}</span>
      <div className="flex w-fit items-center gap-1 rounded-lg border bg-background p-0.5" role="group" aria-label={label}>
        {options.map((o) => (
          <Button key={o} type="button" size="sm" variant={value === o ? 'secondary' : 'ghost'} aria-pressed={value === o} onClick={() => onChange(o)}>{o}</Button>
        ))}
      </div>
      {note && <p className="text-xs text-muted-foreground">{note}</p>}
    </div>
  )
}
