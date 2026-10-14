import {
  Bar, BarChart, CartesianGrid, Cell, ErrorBar, LabelList, ReferenceLine, ResponsiveContainer, Scatter, ScatterChart, Tooltip, XAxis, YAxis, ZAxis,
} from 'recharts'
import { Legend, TooltipBox, axisProps, type TableView } from '@/components/charts'
import { pct } from '@/lib/format'

export interface BarSeries {
  name: string
  color: string
  values: (number | null)[]
}

/** Vertical grouped bars: one group per category, one bar per series (fixed series colours, 2px gaps, hover tooltip). */
export function GroupedBarChart({
  categories, series, format, height = 260, zeroLine = false, legend = true,
}: {
  categories: string[]
  series: BarSeries[]
  format: (v: number) => string
  height?: number
  zeroLine?: boolean
  /** Set to false when the surrounding ChartCard already renders the legend. */
  legend?: boolean
}) {
  const data = categories.map((c, i) => {
    const row: Record<string, string | number | null> = { category: c }
    series.forEach((s, k) => { row[`s${k}`] = s.values[i] })
    return row
  })
  return (
    <>
      {legend && series.length > 1 && <Legend items={series.map((s) => ({ name: s.name, color: s.color }))} />}
      <div style={{ height }} role="img" aria-label={`Bar chart of ${series.map((s) => s.name).join(', ')} by ${categories.length} categories`}>
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={data} margin={{ top: 4, right: 8, bottom: 0, left: 0 }} barGap={2} barCategoryGap="22%">
            <CartesianGrid vertical={false} stroke="var(--chart-grid)" />
            <XAxis dataKey="category" {...axisProps} interval={0} tick={{ ...axisProps.tick, fontSize: 10 }} />
            <YAxis {...axisProps} tickFormatter={format} width={56} />
            {zeroLine && <ReferenceLine y={0} stroke="var(--chart-baseline)" />}
            <Tooltip
              cursor={{ fill: 'var(--muted)', opacity: 0.5 }}
              content={({ active, payload, label }) => active && payload?.length ? (
                <TooltipBox title={String(label)} rows={payload.filter((p) => p.value !== null && p.value !== undefined).map((p) => {
                  const k = Number(String(p.dataKey).slice(1))
                  return { name: series[k].name, color: series[k].color, value: format(Number(p.value)) }
                })} />
              ) : null}
            />
            {series.map((s, k) => (
              <Bar key={s.name} dataKey={`s${k}`} fill={s.color} radius={[4, 4, 0, 0]} maxBarSize={22} isAnimationActive={false} />
            ))}
          </BarChart>
        </ResponsiveContainer>
      </div>
    </>
  )
}

export function groupedBarTable(categories: string[], series: BarSeries[], format: (v: number) => string): TableView {
  return {
    columns: ['', ...series.map((s) => s.name)],
    rows: categories.map((c, i) => [c, ...series.map((s) => (s.values[i] === null ? '—' : format(s.values[i]!)))]),
  }
}

/**
 * Horizontal bars for a single measure per item. Signed values use the diverging pair (positive vs negative) so polarity
 * is encoded by both direction and colour; optional ± error whiskers (e.g. permutation-importance spread).
 */
export function SignedBarChart({
  items, format, signed = true, height,
}: {
  items: { name: string; value: number; error?: number }[]
  format: (v: number) => string
  signed?: boolean
  height?: number
}) {
  const h = height ?? Math.max(140, items.length * 26 + 30)
  return (
    <div style={{ height: h }} role="img" aria-label={`Bar chart of ${items.length} values`}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={items} layout="vertical" margin={{ top: 0, right: 56, bottom: 0, left: 0 }} barCategoryGap="24%">
          <CartesianGrid horizontal={false} stroke="var(--chart-grid)" />
          <XAxis type="number" {...axisProps} tickFormatter={format} />
          <YAxis type="category" dataKey="name" {...axisProps} width={128} tick={{ ...axisProps.tick, fontSize: 10 }} />
          <ReferenceLine x={0} stroke="var(--chart-baseline)" />
          <Tooltip cursor={{ fill: 'var(--muted)', opacity: 0.5 }} content={({ active, payload }) => {
            if (!active || !payload?.length) return null
            const item = payload[0].payload as { name: string; value: number; error?: number }
            return <TooltipBox title={item.name} rows={[
              { name: 'Value', color: 'transparent', value: format(item.value) },
              ...(item.error !== undefined ? [{ name: '± spread', color: 'transparent', value: format(item.error) }] : []),
            ]} />
          }} />
          <Bar dataKey="value" radius={4} maxBarSize={14} isAnimationActive={false}>
            {items.map((i) => (
              <Cell key={i.name} fill={!signed || i.value >= 0 ? 'var(--diverging-pos)' : 'var(--diverging-neg)'} />
            ))}
            {items.some((i) => i.error !== undefined) && <ErrorBar dataKey="error" width={4} stroke="var(--chart-axis)" direction="x" />}
            {/* Value labels collide with whiskers; with error bars the values live in the tooltip and table view. */}
            {!items.some((i) => i.error !== undefined) && (
              <LabelList dataKey="value" position="right" formatter={(v: unknown) => format(Number(v))} style={{ fill: 'var(--muted-foreground)', fontSize: 10 }} />
            )}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  )
}

/** Risk–return scatter: one labelled point per strategy (identity by fixed colour and direct label; the label is the name up to ':' or '(', the tooltip shows it in full). */
export function RiskReturnScatter({ points, height = 280 }: { points: { name: string; color: string; risk: number; ret: number }[]; height?: number }) {
  return (
    <div style={{ height }} role="img" aria-label="Annualized return versus annualized volatility per strategy">
      <ResponsiveContainer width="100%" height="100%">
        <ScatterChart margin={{ top: 12, right: 112, bottom: 16, left: 0 }}>
          <CartesianGrid stroke="var(--chart-grid)" />
          <XAxis type="number" dataKey="risk" {...axisProps} tickFormatter={(v) => pct(v, 0)} domain={['auto', 'auto']}
            label={{ value: 'Annualized volatility', position: 'insideBottom', offset: -8, fill: 'var(--chart-axis)', fontSize: 11 }} />
          <YAxis type="number" dataKey="ret" {...axisProps} tickFormatter={(v) => pct(v, 0)} width={56} domain={['auto', 'auto']} />
          <ZAxis range={[80, 80]} />
          <Tooltip cursor={false} content={({ active, payload }) => {
            if (!active || !payload?.length) return null
            const p = payload[0].payload as { name: string; color: string; risk: number; ret: number }
            return <TooltipBox title={p.name} rows={[
              { name: 'Annualized return', color: p.color, value: pct(p.ret) },
              { name: 'Annualized volatility', color: p.color, value: pct(p.risk) },
            ]} />
          }} />
          <Scatter data={points} isAnimationActive={false}
            shape={(props: { cx?: number; cy?: number; payload?: { name: string; color: string } }) => (
              <g>
                <circle cx={props.cx} cy={props.cy} r={6} fill={props.payload?.color} stroke="var(--chart-surface)" strokeWidth={2} />
                <text x={(props.cx ?? 0) + 9} y={(props.cy ?? 0) + 3} fontSize={10} fill="var(--muted-foreground)">{props.payload?.name.split(/[:(]/)[0].trim()}</text>
              </g>
            )} />
        </ScatterChart>
      </ResponsiveContainer>
    </div>
  )
}

/** Reliability diagram: mean predicted probability vs observed frequency per bin; the diagonal is perfect calibration. */
export function CalibrationChart({ bins, height = 240 }: { bins: { meanPredicted: number; observedFrequency: number; count: number }[]; height?: number }) {
  return (
    <div style={{ height }} role="img" aria-label="Calibration: observed frequency versus predicted probability">
      <ResponsiveContainer width="100%" height="100%">
        <ScatterChart margin={{ top: 8, right: 16, bottom: 16, left: 0 }}>
          <CartesianGrid stroke="var(--chart-grid)" />
          <XAxis type="number" dataKey="meanPredicted" {...axisProps} domain={[0, 1]} tickFormatter={(v) => pct(v, 0)}
            label={{ value: 'Predicted probability', position: 'insideBottom', offset: -8, fill: 'var(--chart-axis)', fontSize: 11 }} />
          <YAxis type="number" dataKey="observedFrequency" {...axisProps} domain={[0, 1]} tickFormatter={(v) => pct(v, 0)} width={48} />
          <ZAxis range={[70, 70]} />
          <ReferenceLine segment={[{ x: 0, y: 0 }, { x: 1, y: 1 }]} stroke="var(--chart-baseline)" strokeDasharray="4 4" />
          <Tooltip cursor={false} content={({ active, payload }) => {
            if (!active || !payload?.length) return null
            const b = payload[0].payload as { meanPredicted: number; observedFrequency: number; count: number }
            return <TooltipBox title={`Bin (${b.count} predictions)`} rows={[
              { name: 'Mean predicted', color: 'var(--series-1)', value: pct(b.meanPredicted, 1) },
              { name: 'Observed', color: 'var(--series-1)', value: pct(b.observedFrequency, 1) },
            ]} />
          }} />
          <Scatter data={bins} fill="var(--series-1)" line={{ stroke: 'var(--series-1)', strokeWidth: 2 }} isAnimationActive={false} />
        </ScatterChart>
      </ResponsiveContainer>
    </div>
  )
}
