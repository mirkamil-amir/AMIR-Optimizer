import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { Download, Plus, RefreshCw } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Progress } from '@/components/ui/progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { DataTable } from '@/components/data-table'
import { ChartCard, TimeSeriesChart } from '@/components/charts'
import { DataBadge, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { useCreateAsset, useMarketStatus, usePrices, useSync, useUpdateAsset } from '@/lib/queries'
import type { AssetCoverage } from '@/lib/types'
import { date, dateTime, money, relativeTime } from '@/lib/format'

const historyOptions = [
  { value: '1', label: '1 year' },
  { value: '3', label: '3 years' },
  { value: '5', label: '5 years' },
  { value: '10', label: '10 years' },
]

export function MarketDataPage() {
  const { data, isLoading, error, refetch } = useMarketStatus()
  const sync = useSync()
  const update = useUpdateAsset()
  const [years, setYears] = useState('5')
  const [preview, setPreview] = useState<string | null>(null)
  const [addOpen, setAddOpen] = useState(false)
  const running = data?.recentJobs.find((j) => j.status === 'Queued' || j.status === 'Running')

  async function runSync(symbols?: string[], force = false) {
    const start = new Date()
    start.setFullYear(start.getFullYear() - Number(years))
    try {
      const job = await sync.mutateAsync({ symbols, startDate: start.toISOString().slice(0, 10), forceFullRefresh: force })
      toast.success('Download queued', { description: `${job.assetsTotal} asset(s) from ${job.source}` })
    } catch (e) {
      toast.error('Could not start download', { description: errorMessage(e) })
    }
  }

  if (isLoading) return <LoadingBlock height="h-96" />
  if (error || !data) return <ErrorState error={error} onRetry={() => refetch()} />

  return (
    <>
      <PageHeader
        title="Market data"
        description={<>Active source <strong>{data.activeProvider}</strong> · prices older than {data.stalePriceDays} business days are flagged stale.</>}
        actions={
          <>
            <Select value={years} onValueChange={(v) => setYears(String(v))} items={historyOptions}>
              <SelectTrigger className="w-32" aria-label="History length"><SelectValue /></SelectTrigger>
              <SelectContent>{historyOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
            <Button onClick={() => runSync()} disabled={!!running || sync.isPending}><Download /> Sync all enabled</Button>
            <Button variant="outline" onClick={() => setAddOpen(true)}><Plus /> Add asset</Button>
          </>
        }
      />

      {data.activeProviderIsSimulated && (
        <div className="mb-4 flex items-center gap-2 rounded-lg border border-series-2/50 bg-series-2/10 px-3 py-2 text-sm">
          <DataBadge kind="simulated" /> The active provider generates synthetic prices. Switch to a live provider in Settings for real market data.
        </div>
      )}

      {running && (
        <Card className="mb-4">
          <CardContent className="space-y-2 py-4">
            <div className="flex items-center justify-between text-sm">
              <span className="flex items-center gap-2"><RefreshCw className="size-4 animate-spin" /> Downloading from {running.source}…</span>
              <span className="tabular text-muted-foreground">{running.assetsCompleted + running.assetsFailed} / {running.assetsTotal}</span>
            </div>
            <Progress value={((running.assetsCompleted + running.assetsFailed) / Math.max(1, running.assetsTotal)) * 100} />
          </CardContent>
        </Card>
      )}

      <DataTable<AssetCoverage>
        caption="Data coverage"
        rows={data.coverage}
        rowKey={(c) => c.symbol}
        initialSort={{ key: 'symbol', dir: 'asc' }}
        onRowClick={(c) => setPreview(c.symbol)}
        columns={[
          { key: 'symbol', header: 'Symbol', sortValue: (c) => c.symbol, cell: (c) => <div><div className="font-medium">{c.symbol}</div><div className="max-w-44 truncate text-xs text-muted-foreground">{c.name}</div></div> },
          {
            key: 'status', header: 'Status', sortValue: (c) => c.freshness,
            cell: (c) => c.freshness === 'Missing' ? <DataBadge kind="missing" /> : c.isSimulated ? <DataBadge kind="simulated" /> : c.freshness === 'Stale' ? <DataBadge kind="stale" /> : <DataBadge kind="live" label="Fresh" />,
          },
          { key: 'obs', header: 'Observations', align: 'right', sortValue: (c) => c.observations, cell: (c) => c.observations.toLocaleString() },
          { key: 'range', header: 'Coverage', sortValue: (c) => c.firstDate, cell: (c) => c.firstDate ? <span className="text-xs">{date(c.firstDate)} – {date(c.lastDate)}</span> : '—' },
          { key: 'adj', header: 'Adj. close', cell: (c) => c.observations === 0 ? '—' : c.hasAdjustedClose ? <Badge variant="secondary">Yes</Badge> : <Badge variant="outline" className="text-warn" title="Returns exclude dividends">Raw only</Badge> },
          { key: 'px', header: 'Latest price', align: 'right', sortValue: (c) => c.latestPrice, cell: (c) => <span title={c.latestPriceAsOfUtc ? dateTime(c.latestPriceAsOfUtc) : undefined}>{money(c.latestPrice)}</span> },
          { key: 'ret', header: 'Retrieved', sortValue: (c) => c.lastRetrievedAtUtc, cell: (c) => <span className="text-xs text-muted-foreground">{relativeTime(c.lastRetrievedAtUtc)}</span> },
          {
            key: 'msg', header: 'Last sync',
            // Successful syncs show "OK"; routine notes (e.g. a history refresh after a dividend) stay available as a tooltip.
            cell: (c) => c.lastSyncSuccess === false
              ? <span className="text-xs text-loss" title={c.lastSyncMessage ?? ''}>Failed: {(c.lastSyncMessage ?? '').slice(0, 60)}</span>
              : <span className="text-xs text-muted-foreground" title={c.lastSyncMessage && c.lastSyncMessage !== 'Already up to date.' ? c.lastSyncMessage : undefined}>{c.lastSyncSuccess ? 'OK' : '—'}</span>,
          },
          {
            key: 'enabled', header: 'Enabled',
            cell: (c) => (
              <span onClick={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()}>
                <Switch checked={c.isEnabled} aria-label={`${c.isEnabled ? 'Disable' : 'Enable'} ${c.symbol}`}
                  onCheckedChange={(on) => update.mutate({ symbol: c.symbol, isEnabled: on }, { onError: (e) => toast.error(errorMessage(e)) })} />
              </span>
            ),
          },
          {
            key: 'act', header: '',
            cell: (c) => <Button size="xs" variant="ghost" disabled={!!running} onClick={(e) => { e.stopPropagation(); runSync([c.symbol], true) }}>Refresh</Button>,
          },
        ]}
      />

      {preview && <PricePreview symbol={preview} />}

      <div className="mt-6 grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader><CardTitle className="text-sm">Providers</CardTitle><CardDescription className="text-xs">API keys are configured on the server only and never sent to the browser.</CardDescription></CardHeader>
          <CardContent className="space-y-3 text-sm">
            {data.providers.map((p) => (
              <div key={p.name} className="rounded-md border p-3">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium">{p.displayName}</span>
                  <span className="flex gap-1">
                    {p.name === data.activeProvider && <Badge>Active</Badge>}
                    {p.isSimulated && <DataBadge kind="simulated" />}
                    <Badge variant={p.isConfigured ? 'secondary' : 'outline'}>{p.isConfigured ? 'Configured' : p.requiresApiKey ? 'API key missing' : 'Disabled'}</Badge>
                  </span>
                </div>
                <p className="mt-1 text-xs text-muted-foreground">{p.rateLimit}</p>
                <p className="text-xs text-muted-foreground">{p.terms} {p.providesAdjustedClose ? 'Provides adjusted closes.' : 'Raw closes only.'}</p>
              </div>
            ))}
          </CardContent>
        </Card>
        <Card>
          <CardHeader><CardTitle className="text-sm">Recent download jobs</CardTitle></CardHeader>
          <CardContent className="space-y-2 text-sm">
            {data.recentJobs.length === 0 && <p className="text-muted-foreground">No downloads yet.</p>}
            {data.recentJobs.map((j) => (
              <details key={j.id} className="rounded-md border px-3 py-2">
                <summary className="flex cursor-pointer items-center justify-between gap-2">
                  <span>{j.source} · {j.symbols.length} asset(s)</span>
                  <span className="flex items-center gap-2 text-xs text-muted-foreground">{dateTime(j.requestedAtUtc)}<Badge variant={j.status === 'Failed' ? 'destructive' : j.status === 'Succeeded' ? 'secondary' : 'outline'}>{j.status}</Badge></span>
                </summary>
                <p className="mt-1 text-xs text-muted-foreground">{j.message}</p>
                <table className="mt-2 w-full text-xs tabular">
                  <thead className="text-muted-foreground"><tr><th className="text-left">Symbol</th><th className="text-right">Rcvd</th><th className="text-right">New</th><th className="text-right">Upd</th><th className="text-right">Rejected</th><th className="text-right">Dupl.</th></tr></thead>
                  <tbody>
                    {j.logs.map((l) => (
                      <tr key={l.symbol} className={l.success ? '' : 'text-loss'} title={l.message ?? ''}>
                        <td>{l.symbol}</td><td className="text-right">{l.rowsReceived}</td><td className="text-right">{l.rowsInserted}</td><td className="text-right">{l.rowsUpdated}</td><td className="text-right">{l.rowsRejected}</td><td className="text-right">{l.duplicatesSkipped}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </details>
            ))}
          </CardContent>
        </Card>
      </div>

      <AddAssetDialog open={addOpen} onOpenChange={setAddOpen} onAdded={(s) => runSync([s])} />
    </>
  )
}

function PricePreview({ symbol }: { symbol: string }) {
  const { data, isLoading } = usePrices(symbol)
  if (isLoading) return <div className="mt-4"><LoadingBlock height="h-72" /></div>
  if (!data || data.items.length < 2) return <p className="mt-4 text-sm text-muted-foreground">No stored prices for {symbol}.</p>
  const items = data.items
  const hasAdj = items.every((p) => p.adjustedClose !== null)
  return (
    <ChartCard
      className="mt-4"
      title={`${symbol} price history`}
      description={`${items[0].source}${items[0].isSimulated ? ' (simulated)' : ''}, USD, ${date(items[0].date)} – ${date(items[items.length - 1].date)} · ${items.length.toLocaleString()} observations`}
      hint="Raw close is the traded price. Adjusted close is back-adjusted for splits and dividends and is used for return calculations."
      legend={hasAdj ? [{ name: 'Raw close', color: 'var(--series-1)' }, { name: 'Adjusted close', color: 'var(--series-2)', dashed: true }] : undefined}
      table={{ columns: ['Date', 'Close', 'Adj. close', 'Volume'], rows: items.slice(-260).reverse().map((p) => [p.date, money(p.close), p.adjustedClose !== null ? money(p.adjustedClose) : '—', p.volume?.toLocaleString() ?? '—']) }}
    >
      <TimeSeriesChart
        dates={items.map((p) => p.date)}
        series={[
          { name: 'Raw close', values: items.map((p) => p.close) },
          ...(hasAdj ? [{ name: 'Adjusted close', values: items.map((p) => p.adjustedClose), dashed: true }] : []),
        ]}
        format={(v) => money(v, 'USD', { decimals: 0 })}
      />
    </ChartCard>
  )
}

const assetSchema = z.object({
  symbol: z.string().trim().regex(/^[A-Za-z0-9.\-^=]{1,15}$/, 'Letters, digits, ".", "-", "^" or "=" (max 15)'),
  name: z.string().max(200).optional(),
  sector: z.string().max(100).optional(),
  assetType: z.enum(['Equity', 'Etf']),
})
type AssetForm = z.infer<typeof assetSchema>

function AddAssetDialog({ open, onOpenChange, onAdded }: { open: boolean; onOpenChange: (o: boolean) => void; onAdded: (symbol: string) => void }) {
  const create = useCreateAsset()
  const { register, handleSubmit, reset, setValue, watch, formState: { errors } } = useForm<AssetForm>({ resolver: zodResolver(assetSchema), defaultValues: { assetType: 'Equity' } })
  const onSubmit = handleSubmit(async (v) => {
    try {
      const a = await create.mutateAsync({ symbol: v.symbol.toUpperCase(), name: v.name || undefined, sector: v.sector || undefined, assetType: v.assetType })
      toast.success(`${a.symbol} added`, { description: 'Downloading its price history…' })
      reset()
      onOpenChange(false)
      onAdded(a.symbol)
    } catch (e) {
      toast.error('Could not add asset', { description: errorMessage(e) })
    }
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add asset to the universe</DialogTitle>
          <DialogDescription>Listings in any supported currency; prices stay in that currency and are converted for analyses. Prices are downloaded right after adding.</DialogDescription>
        </DialogHeader>
        <form onSubmit={onSubmit} className="space-y-3" noValidate id="add-asset">
          <div className="space-y-1.5">
            <Label htmlFor="sym">Symbol</Label>
            <Input id="sym" placeholder="e.g. COST" autoFocus aria-invalid={!!errors.symbol} {...register('symbol')} />
            {errors.symbol && <p role="alert" className="text-xs text-destructive">{errors.symbol.message}</p>}
          </div>
          <div className="space-y-1.5"><Label htmlFor="nm">Name (optional – filled from provider)</Label><Input id="nm" {...register('name')} /></div>
          <div className="space-y-1.5"><Label htmlFor="sec">Sector (optional, used for sector limits)</Label><Input id="sec" {...register('sector')} /></div>
          <div className="space-y-1.5">
            <Label>Type</Label>
            <Select value={watch('assetType')} onValueChange={(v) => setValue('assetType', v as 'Equity' | 'Etf')} items={[{ value: 'Equity', label: 'Equity' }, { value: 'Etf', label: 'ETF' }]}>
              <SelectTrigger className="w-full" aria-label="Asset type"><SelectValue /></SelectTrigger>
              <SelectContent><SelectItem value="Equity">Equity</SelectItem><SelectItem value="Etf">ETF</SelectItem></SelectContent>
            </Select>
          </div>
        </form>
        <DialogFooter>
          <Button type="submit" form="add-asset" disabled={create.isPending}>Add asset</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
