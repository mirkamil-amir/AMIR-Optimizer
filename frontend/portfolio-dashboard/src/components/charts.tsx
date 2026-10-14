import { useMemo, useState, type ReactNode } from 'react'
import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Cell, Line, LineChart, Pie, PieChart, ReferenceLine,
  ResponsiveContainer, Scatter, ScatterChart, Tooltip, XAxis, YAxis, ZAxis,
} from 'recharts'
import { Table2, LineChart as LineIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { InfoTip } from '@/components/common'
import { monthNames, pct, shortDate, date as fmtDate } from '@/lib/format'
import { cn } from '@/lib/utils'

// Categorical slots in fixed order (validated palette); never cycled. Past 8 series → fold into "Other".
export const SERIES_VARS = ['--series-1', '--series-2', '--series-3', '--series-4', '--series-5', '--series-6', '--series-7', '--series-8']
export const seriesColor = (i: number) => `var(${SERIES_VARS[Math.min(i, SERIES_VARS.length - 1)]})`
export const OTHER_COLOR = 'var(--chart-axis)'

export const axisProps = {
  stroke: 'var(--chart-baseline)',
  tick: { fill: 'var(--chart-axis)', fontSize: 11 },
  tickLine: false,
  axisLine: { stroke: 'var(--chart-baseline)' },
} as const

// ------------------------------------------------------------------ chart card with legend + table view

export interface TableView {
  columns: string[]
  rows: (string | number)[][]
}

export function ChartCard({
  title, description, hint, legend, table, children, actions, className,
}: {
  title: string
  description?: ReactNode
  hint?: ReactNode
  legend?: { name: string; color: string; dashed?: boolean }[]
  table?: TableView
  children: ReactNode
  actions?: ReactNode
  className?: string
}) {
  const [showTable, setShowTable] = useState(false)
  return (
    <Card className={cn('gap-3', className)}>
      <CardHeader className="flex flex-row items-start justify-between gap-2">
        <div>
          <CardTitle className="flex items-center gap-1.5 text-sm">
            {title}
            {hint && <InfoTip>{hint}</InfoTip>}
          </CardTitle>
          {description && <CardDescription className="mt-1 text-xs">{description}</CardDescription>}
        </div>
        <div className="flex items-center gap-1">
          {actions}
          {table && (
            <Button
              variant="ghost"
              size="icon-sm"
              aria-pressed={showTable}
              aria-label={showTable ? 'Show chart' : 'Show data table'}
              title={showTable ? 'Show chart' : 'Show data table'}
              onClick={() => setShowTable((s) => !s)}
            >
              {showTable ? <LineIcon /> : <Table2 />}
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent>
        {legend && legend.length > 1 && !showTable && <Legend items={legend} />}
        {showTable && table ? <DataView table={table} /> : children}
      </CardContent>
    </Card>
  )
}

export function Legend({ items }: { items: { name: string; color: string; dashed?: boolean }[] }) {
  return (
    <ul className="mb-3 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground" aria-label="Legend">
      {items.map((i) => (
        <li key={i.name} className="flex items-center gap-1.5">
          <span
            aria-hidden
            className="inline-block h-0.5 w-4 rounded-full"
            style={{ background: i.dashed ? `repeating-linear-gradient(90deg, ${i.color} 0 4px, transparent 4px 7px)` : i.color, height: 3 }}
          />
          {i.name}
        </li>
      ))}
    </ul>
  )
}

function DataView({ table }: { table: TableView }) {
  return (
    <div className="max-h-80 overflow-auto rounded-md border text-xs">
      <table className="w-full tabular">
        <thead className="sticky top-0 bg-muted">
          <tr>
            {table.columns.map((c) => (
              <th key={c} className="px-2 py-1.5 text-left font-medium">{c}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {table.rows.map((r, i) => (
            <tr key={i} className="border-t">
              {r.map((v, j) => (
                <td key={j} className="px-2 py-1">{v}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// ------------------------------------------------------------------ tooltip shell

export function TooltipBox({ title, rows }: { title: ReactNode; rows: { name: string; color: string; value: string }[] }) {
  return (
    <div className="min-w-40 rounded-lg border bg-popover px-3 py-2 text-xs shadow-md">
      <div className="mb-1 font-medium text-foreground">{title}</div>
      {rows.map((r) => (
        <div key={r.name} className="flex items-center justify-between gap-4 py-0.5">
          <span className="flex items-center gap-1.5 text-muted-foreground">
            <span className="inline-block size-2 rounded-full" style={{ background: r.color }} aria-hidden />
            {r.name}
          </span>
          <span className="tabular font-medium text-foreground">{r.value}</span>
        </div>
      ))}
    </div>
  )
}

// ------------------------------------------------------------------ time series (line / area)

export interface Series {
  name: string
  values: (number | null)[]
  color?: string
  dashed?: boolean
}

/** Multi-series time chart with crosshair tooltip. One y-axis only. */
export function TimeSeriesChart({
  dates, series, format, height = 280, area = false, zeroLine = false, yDomain,
}: {
  dates: string[]
  series: Series[]
  format: (v: number) => string
  height?: number
  area?: boolean
  zeroLine?: boolean
  yDomain?: [number | 'auto' | 'dataMin', number | 'auto' | 'dataMax']
}) {
  const data = useMemo(
    () => dates.map((d, i) => {
      const row: Record<string, number | string | null> = { date: d }
      series.forEach((s, k) => { row[`s${k}`] = s.values[i] ?? null })
      return row
    }),
    [dates, series],
  )
  const colors = series.map((s, i) => s.color ?? seriesColor(i))
  const Chart = area ? AreaChart : LineChart

  return (
    <div style={{ height }} role="img" aria-label={`Chart of ${series.map((s) => s.name).join(', ')} from ${fmtDate(dates[0])} to ${fmtDate(dates[dates.length - 1])}`}>
      <ResponsiveContainer width="100%" height="100%">
        <Chart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
          <CartesianGrid vertical={false} stroke="var(--chart-grid)" strokeWidth={1} />
          <XAxis dataKey="date" {...axisProps} tickFormatter={shortDate} minTickGap={48} />
          <YAxis {...axisProps} tickFormatter={format} width={64} domain={yDomain ?? ['auto', 'auto']} />
          {zeroLine && <ReferenceLine y={0} stroke="var(--chart-baseline)" />}
          <Tooltip
            cursor={{ stroke: 'var(--chart-axis)', strokeWidth: 1 }}
            content={({ active, payload, label }) =>
              active && payload?.length ? (
                <TooltipBox
                  title={fmtDate(String(label))}
                  rows={payload
                    .filter((p) => p.value !== null && p.value !== undefined)
                    .map((p) => {
                      const k = Number(String(p.dataKey).slice(1))
                      return { name: series[k].name, color: colors[k], value: format(Number(p.value)) }
                    })}
                />
              ) : null
            }
          />
          {series.map((s, k) =>
            area ? (
              <Area
                key={s.name}
                type="monotone"
                dataKey={`s${k}`}
                stroke={colors[k]}
                strokeWidth={2}
                fill={colors[k]}
                fillOpacity={0.1}
                dot={false}
                activeDot={{ r: 4, stroke: 'var(--chart-surface)', strokeWidth: 2 }}
                isAnimationActive={false}
                connectNulls
              />
            ) : (
              <Line
                key={s.name}
                type="monotone"
                dataKey={`s${k}`}
                stroke={colors[k]}
                strokeWidth={2}
                strokeDasharray={s.dashed ? '5 4' : undefined}
                dot={false}
                activeDot={{ r: 4, stroke: 'var(--chart-surface)', strokeWidth: 2 }}
                isAnimationActive={false}
                connectNulls
              />
            ),
          )}
        </Chart>
      </ResponsiveContainer>
    </div>
  )
}

/** Builds a table view (one row per date, sampled to at most ~260 rows). */
export function timeSeriesTable(dates: string[], series: Series[], format: (v: number) => string): TableView {
  const step = Math.max(1, Math.ceil(dates.length / 260))
  const rows: (string | number)[][] = []
  for (let i = 0; i < dates.length; i += step) {
    rows.push([dates[i], ...series.map((s) => (s.values[i] === null || s.values[i] === undefined ? '—' : format(s.values[i]!)))])
  }
  if ((dates.length - 1) % step !== 0) {
    const i = dates.length - 1
    rows.push([dates[i], ...series.map((s) => (s.values[i] === null || s.values[i] === undefined ? '—' : format(s.values[i]!)))])
  }
  return { columns: ['Date', ...series.map((s) => s.name)], rows }
}

// ------------------------------------------------------------------ allocation donut

export function foldSlices(items: { name: string; value: number }[], max = 8) {
  const sorted = [...items].filter((i) => i.value > 1e-6).sort((a, b) => b.value - a.value)
  if (sorted.length <= max) return sorted
  const head = sorted.slice(0, max - 1)
  const rest = sorted.slice(max - 1).reduce((s, i) => s + i.value, 0)
  return [...head, { name: 'Other', value: rest }]
}

export function AllocationDonut({ items, height = 240, centerLabel }: { items: { name: string; value: number }[]; height?: number; centerLabel?: ReactNode }) {
  const slices = foldSlices(items)
  return (
    <div className="grid items-center gap-4 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <div className="relative" style={{ height }}>
        <ResponsiveContainer width="100%" height="100%">
          <PieChart>
            <Pie data={slices} dataKey="value" nameKey="name" innerRadius="62%" outerRadius="92%" stroke="var(--chart-surface)" strokeWidth={2} isAnimationActive={false}>
              {slices.map((s, i) => (
                <Cell key={s.name} fill={s.name === 'Other' || s.name === 'Cash' ? OTHER_COLOR : seriesColor(i)} />
              ))}
            </Pie>
            <Tooltip content={({ active, payload }) => active && payload?.length ? (
              <TooltipBox title={String(payload[0].name)} rows={[{ name: 'Weight', color: String(payload[0].payload.fill ?? OTHER_COLOR), value: pct(Number(payload[0].value)) }]} />
            ) : null} />
          </PieChart>
        </ResponsiveContainer>
        {centerLabel && <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center text-center">{centerLabel}</div>}
      </div>
      <ul className="space-y-1.5 text-sm" aria-label="Allocation">
        {slices.map((s, i) => (
          <li key={s.name} className="flex items-center justify-between gap-3">
            <span className="flex items-center gap-2">
              <span className="inline-block size-2.5 rounded-sm" style={{ background: s.name === 'Other' || s.name === 'Cash' ? OTHER_COLOR : seriesColor(i) }} aria-hidden />
              {s.name}
            </span>
            <span className="tabular text-muted-foreground">{pct(s.value, 1)}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

// ------------------------------------------------------------------ efficient frontier

export function FrontierChart({
  frontier, assets, selected, references, height = 320,
}: {
  frontier: { volatility: number; expectedReturn: number; sharpeRatio: number | null }[]
  assets: { symbol: string; volatility: number; expectedReturn: number }[]
  selected?: { volatility: number; expectedReturn: number; sharpeRatio: number | null }
  references: { name: string; volatility: number | null; expectedReturn: number | null }[]
  height?: number
}) {
  const refs = references.filter((r) => r.volatility !== null && r.expectedReturn !== null)
  type P = { x: number; y: number; label: string; kind: string; sharpe?: number | null }
  const frontierData: P[] = frontier.map((p) => ({ x: p.volatility, y: p.expectedReturn, label: 'Efficient frontier', kind: 'frontier', sharpe: p.sharpeRatio }))
  const assetData: P[] = assets.map((a) => ({ x: a.volatility, y: a.expectedReturn, label: a.symbol, kind: 'asset' }))
  const refData: P[] = refs.map((r) => ({ x: r.volatility!, y: r.expectedReturn!, label: r.name, kind: 'ref' }))
  const selData: P[] = selected ? [{ x: selected.volatility, y: selected.expectedReturn, label: 'Selected portfolio', kind: 'selected', sharpe: selected.sharpeRatio }] : []

  return (
    <div style={{ height }} role="img" aria-label="Efficient frontier: expected return versus volatility">
      <ResponsiveContainer width="100%" height="100%">
        <ScatterChart margin={{ top: 8, right: 16, bottom: 16, left: 0 }}>
          <CartesianGrid stroke="var(--chart-grid)" />
          <XAxis type="number" dataKey="x" name="Volatility" {...axisProps} tickFormatter={(v) => pct(v, 0)} domain={['auto', 'auto']}
            label={{ value: 'Annualized volatility', position: 'insideBottom', offset: -8, fill: 'var(--chart-axis)', fontSize: 11 }} />
          <YAxis type="number" dataKey="y" name="Expected return" {...axisProps} tickFormatter={(v) => pct(v, 0)} width={56} domain={['auto', 'auto']} />
          <ZAxis range={[60, 60]} />
          <Tooltip
            cursor={false}
            content={({ active, payload }) => {
              if (!active || !payload?.length) return null
              const p = payload[0].payload as P
              return (
                <TooltipBox title={p.label} rows={[
                  { name: 'Expected return', color: 'transparent', value: pct(p.y) },
                  { name: 'Volatility', color: 'transparent', value: pct(p.x) },
                  ...(p.sharpe !== undefined && p.sharpe !== null ? [{ name: 'Sharpe', color: 'transparent', value: p.sharpe.toFixed(2) }] : []),
                ]} />
              )
            }}
          />
          <Scatter data={frontierData} line={{ stroke: seriesColor(0), strokeWidth: 2 }} fill={seriesColor(0)} shape={() => <g />} isAnimationActive={false} />
          <Scatter data={assetData} fill="var(--chart-axis)" isAnimationActive={false}
            shape={(props: { cx?: number; cy?: number; payload?: P }) => (
              <g>
                <circle cx={props.cx} cy={props.cy} r={4} fill="var(--chart-axis)" stroke="var(--chart-surface)" strokeWidth={2} />
                <text x={(props.cx ?? 0) + 7} y={(props.cy ?? 0) + 3} fontSize={10} fill="var(--muted-foreground)">{props.payload?.label}</text>
              </g>
            )} />
          <Scatter data={refData} fill={seriesColor(2)} isAnimationActive={false}
            shape={(props: { cx?: number; cy?: number }) => <rect x={(props.cx ?? 0) - 5} y={(props.cy ?? 0) - 5} width={10} height={10} rx={2} fill={seriesColor(2)} stroke="var(--chart-surface)" strokeWidth={2} />} />
          <Scatter data={selData} fill={seriesColor(1)} isAnimationActive={false}
            shape={(props: { cx?: number; cy?: number }) => <circle cx={props.cx} cy={props.cy} r={7} fill={seriesColor(1)} stroke="var(--chart-surface)" strokeWidth={2} />} />
        </ScatterChart>
      </ResponsiveContainer>
    </div>
  )
}

// ------------------------------------------------------------------ correlation heatmap (diverging, gray midpoint)

export function correlationColor(v: number) {
  const a = Math.round(Math.min(1, Math.abs(v)) * 100)
  return `color-mix(in oklab, ${v >= 0 ? 'var(--diverging-pos)' : 'var(--diverging-neg)'} ${a}%, var(--diverging-mid))`
}

export function CorrelationHeatmap({ symbols, matrix }: { symbols: string[]; matrix: number[][] }) {
  const [hover, setHover] = useState<{ i: number; j: number } | null>(null)
  if (symbols.length === 0) return null
  return (
    <div className="max-w-3xl">
      <div className="overflow-x-auto">
        <div
          className="grid gap-[2px] text-[10px]"
          style={{ gridTemplateColumns: `3.5rem repeat(${symbols.length}, minmax(2.25rem, 1fr))` }}
          role="table"
          aria-label="Correlation matrix of daily returns"
        >
          <div role="columnheader" />
          {symbols.map((s) => (
            <div key={s} role="columnheader" className="truncate pb-1 text-center font-medium text-muted-foreground">{s}</div>
          ))}
          {symbols.map((row, i) => (
            <div key={row} role="row" className="contents">
              <div role="rowheader" className="flex items-center pr-1 font-medium text-muted-foreground">{row}</div>
              {symbols.map((col, j) => {
                const v = matrix[i][j]
                const strong = Math.abs(v) > 0.6
                return (
                  <div
                    key={col}
                    role="cell"
                    tabIndex={0}
                    aria-label={`${row} vs ${col}: ${v.toFixed(2)}`}
                    onMouseEnter={() => setHover({ i, j })}
                    onFocus={() => setHover({ i, j })}
                    onMouseLeave={() => setHover(null)}
                    className={cn('flex aspect-square items-center justify-center rounded-[3px] tabular outline-none focus-visible:ring-2 focus-visible:ring-ring', hover && (hover.i === i || hover.j === j) && 'ring-1 ring-foreground/30')}
                    style={{ background: correlationColor(v), color: strong ? '#fff' : 'var(--foreground)' }}
                  >
                    {v.toFixed(2)}
                  </div>
                )
              })}
            </div>
          ))}
        </div>
      </div>
      <div className="mt-3 flex items-center gap-2 text-xs text-muted-foreground">
        <span>−1</span>
        <span className="h-2 w-40 rounded-full" style={{ background: 'linear-gradient(90deg, var(--diverging-neg), var(--diverging-mid), var(--diverging-pos))' }} aria-hidden />
        <span>+1</span>
        <span className="ml-auto tabular">{hover ? `${symbols[hover.i]} · ${symbols[hover.j]}: ${matrix[hover.i][hover.j].toFixed(3)}` : 'Hover a cell for details'}</span>
      </div>
    </div>
  )
}

// ------------------------------------------------------------------ weight vs risk contribution (grouped bars)

export function RiskContributionChart({ items, height }: { items: { symbol: string; weight: number; risk: number | null }[]; height?: number }) {
  const data = items.filter((i) => i.weight > 1e-6).map((i) => ({ symbol: i.symbol, weight: i.weight, risk: i.risk ?? 0 }))
  const h = height ?? Math.max(160, data.length * 34 + 40)
  return (
    <div style={{ height: h }} role="img" aria-label="Portfolio weight versus contribution to risk by holding">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 0, right: 16, bottom: 0, left: 0 }} barGap={2} barCategoryGap="28%">
          <CartesianGrid horizontal={false} stroke="var(--chart-grid)" />
          <XAxis type="number" {...axisProps} tickFormatter={(v) => pct(v, 0)} />
          <YAxis type="category" dataKey="symbol" {...axisProps} width={56} />
          <Tooltip cursor={{ fill: 'var(--muted)', opacity: 0.5 }} content={({ active, payload, label }) => active && payload?.length ? (
            <TooltipBox title={String(label)} rows={[
              { name: 'Weight', color: seriesColor(0), value: pct(Number(payload[0]?.value)) },
              { name: 'Risk contribution', color: seriesColor(1), value: pct(Number(payload[1]?.value)) },
            ]} />
          ) : null} />
          <Bar dataKey="weight" fill={seriesColor(0)} radius={[0, 4, 4, 0]} maxBarSize={12} isAnimationActive={false} />
          <Bar dataKey="risk" fill={seriesColor(1)} radius={[0, 4, 4, 0]} maxBarSize={12} isAnimationActive={false} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}

// ------------------------------------------------------------------ allocation through time (stacked area)

export function WeightsOverTimeChart({ points, height = 260 }: { points: { date: string; weights: Record<string, number> }[]; height?: number }) {
  const totals = new Map<string, number>()
  points.forEach((p) => Object.entries(p.weights).forEach(([k, v]) => totals.set(k, (totals.get(k) ?? 0) + v)))
  const ranked = [...totals.entries()].sort((a, b) => b[1] - a[1]).map(([k]) => k)
  const shown = ranked.length > 8 ? ranked.slice(0, 7) : ranked
  const keysAll = ranked.length > 8 ? [...shown, 'Other'] : shown
  const data = points.map((p) => {
    const row: Record<string, number | string> = { date: p.date }
    shown.forEach((k) => { row[k] = p.weights[k] ?? 0 })
    if (ranked.length > 8) row.Other = ranked.slice(7).reduce((s, k) => s + (p.weights[k] ?? 0), 0)
    return row
  })
  return (
    <>
      <Legend items={keysAll.map((k, i) => ({ name: k, color: k === 'Other' ? OTHER_COLOR : seriesColor(i) }))} />
      <div style={{ height }} role="img" aria-label="Target weights at each rebalance">
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid vertical={false} stroke="var(--chart-grid)" />
            <XAxis dataKey="date" {...axisProps} tickFormatter={shortDate} minTickGap={48} />
            <YAxis {...axisProps} tickFormatter={(v) => pct(v, 0)} width={48} domain={[0, 1]} />
            <Tooltip content={({ active, payload, label }) => active && payload?.length ? (
              <TooltipBox title={fmtDate(String(label))} rows={[...payload].reverse().filter((p) => Number(p.value) > 0.0005).map((p) => ({
                name: String(p.dataKey), color: String(p.color), value: pct(Number(p.value), 1),
              }))} />
            ) : null} />
            {keysAll.map((k, i) => (
              <Area key={k} type="stepAfter" dataKey={k} stackId="w" stroke="var(--chart-surface)" strokeWidth={1}
                fill={k === 'Other' ? OTHER_COLOR : seriesColor(i)} fillOpacity={0.85} isAnimationActive={false} />
            ))}
          </AreaChart>
        </ResponsiveContainer>
      </div>
    </>
  )
}

// ------------------------------------------------------------------ monthly returns grid

export function MonthlyReturnsGrid({ items, label = 'Portfolio' }: { items: { year: number; month: number; value: number | null }[]; label?: string }) {
  const years = [...new Set(items.map((i) => i.year))].sort()
  const lookup = new Map(items.map((i) => [`${i.year}-${i.month}`, i.value]))
  const annual = (y: number) => items.filter((i) => i.year === y && i.value !== null).reduce((g, i) => g * (1 + i.value!), 1) - 1
  if (years.length === 0) return <p className="text-sm text-muted-foreground">No monthly returns yet.</p>
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs tabular" aria-label={`${label} monthly returns`}>
        <thead>
          <tr className="text-muted-foreground">
            <th className="px-1.5 py-1 text-left font-medium">Year</th>
            {monthNames.map((m) => <th key={m} className="px-1 py-1 text-right font-medium">{m}</th>)}
            <th className="px-1.5 py-1 text-right font-semibold">Year</th>
          </tr>
        </thead>
        <tbody>
          {years.map((y) => (
            <tr key={y}>
              <td className="px-1.5 py-1 font-medium">{y}</td>
              {monthNames.map((_, m) => {
                const v = lookup.get(`${y}-${m + 1}`)
                return (
                  <td key={m} className="px-0.5 py-0.5">
                    <div
                      className="rounded px-1 py-1 text-right"
                      style={v === undefined || v === null ? undefined : { background: correlationColor(Math.max(-1, Math.min(1, v / 0.1))), color: Math.abs(v) > 0.06 ? '#fff' : undefined }}
                      title={v === undefined || v === null ? undefined : `${monthNames[m]} ${y}: ${pct(v)}`}
                    >
                      {v === undefined || v === null ? '' : pct(v, 1, true)}
                    </div>
                  </td>
                )
              })}
              <td className={cn('px-1.5 py-1 text-right font-semibold', annual(y) >= 0 ? 'text-gain' : 'text-loss')}>{pct(annual(y), 1, true)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="mt-2 text-[11px] text-muted-foreground">Cell shading: blue = gain, red = loss, saturating at ±10%.</p>
    </div>
  )
}
