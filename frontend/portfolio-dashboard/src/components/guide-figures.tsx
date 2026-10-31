import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import {
  constructWeights, equalWeightVolatility, fitBoosting, fitForest, gmvMix, growthAfterCosts, logistic, maxReturnForRiskMix, meanSquaredError,
  mixCurve, predictBoosting, predictForest, predictTree, ridgeShrink, sampleMonthlyReturns, sampleOlsCoefficients, samplePoints, sampleScores,
  sampleTwoAssets, tangencyMix, targetReturnMix, truePattern, type Mix, type TwoAssetParams, type WeightingRule,
} from '@/lib/guide-demos'
import { pct } from '@/lib/format'

/**
 * Interactive figures of the strategy guide. All of them run on the sample data of `lib/guide-demos.ts` (never on stored
 * prices or a user's data) and say so. Sliders change the example; "Play" animates the main slider and is not offered
 * when the system asks for reduced motion.
 */

// ------------------------------------------------------------------ building blocks

/** Drawing width before the figure is measured; afterwards every chart is drawn at its real width so text stays 11 px. */
const DEFAULT_W = 560
const PAD = { l: 52, r: 16, t: 14, b: 38 }
const C = { s1: 'var(--series-1)', s2: 'var(--series-2)', s3: 'var(--series-3)', s4: 'var(--series-4)', axis: 'var(--chart-axis)', grid: 'var(--chart-grid)', muted: 'var(--muted-foreground)' }

type Scale = (v: number) => number
const linear = (d0: number, d1: number, r0: number, r1: number): Scale => (v) => r0 + ((v - d0) / (d1 - d0)) * (r1 - r0)

function ticks(min: number, max: number, count = 5): number[] {
  const raw = (max - min) / count
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? raw
  const out: number[] = []
  for (let v = Math.ceil(min / step) * step; v <= max + step * 1e-9; v += step) out.push(Number(v.toFixed(10)))
  return out
}

const path = (pts: [number, number][]) => pts.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)},${y.toFixed(1)}`).join('')

function Plot({ w: W, x, y, xFormat, yFormat, xLabel, yLabel, label, height = 250, children }: {
  w: number; x: [number, number]; y: [number, number]; xFormat: (v: number) => string; yFormat: (v: number) => string
  xLabel: string; yLabel: string; label: string; height?: number; children: (sx: Scale, sy: Scale) => ReactNode
}) {
  const sx = linear(x[0], x[1], PAD.l, W - PAD.r)
  const sy = linear(y[0], y[1], height - PAD.b, PAD.t)
  return (
    <svg viewBox={`0 0 ${W} ${height}`} className="h-auto w-full" role="img" aria-label={label}>
      {ticks(y[0], y[1]).map((v) => (
        <g key={`y${v}`}>
          <line x1={PAD.l} x2={W - PAD.r} y1={sy(v)} y2={sy(v)} stroke={C.grid} />
          <text x={PAD.l - 6} y={sy(v)} dy="0.32em" textAnchor="end" fontSize="11" fill={C.muted}>{yFormat(v)}</text>
        </g>
      ))}
      {ticks(x[0], x[1]).map((v) => (
        <text key={`x${v}`} x={sx(v)} y={height - PAD.b + 16} textAnchor="middle" fontSize="11" fill={C.muted}>{xFormat(v)}</text>
      ))}
      <line x1={PAD.l} x2={W - PAD.r} y1={height - PAD.b} y2={height - PAD.b} stroke={C.axis} />
      <text x={(PAD.l + W - PAD.r) / 2} y={height - 4} textAnchor="middle" fontSize="11" fill={C.muted}>{xLabel}</text>
      <text transform={`translate(12 ${(PAD.t + height - PAD.b) / 2}) rotate(-90)`} textAnchor="middle" fontSize="11" fill={C.muted}>{yLabel}</text>
      <svg x={0} y={0} width={W} height={height} overflow="hidden">{children(sx, sy)}</svg>
    </svg>
  )
}

function Figure({ title, controls, readout, caption, children }: {
  title: string; controls?: ReactNode; readout?: ReactNode; caption: ReactNode; children: (width: number) => ReactNode
}) {
  const [width, setWidth] = useState(DEFAULT_W)
  const [el, setEl] = useState<HTMLDivElement | null>(null)
  useEffect(() => {
    if (!el || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.max(280, Math.round(entry.contentRect.width))))
    observer.observe(el)
    return () => observer.disconnect()
  }, [el])
  return (
    <figure className="my-6 space-y-3 rounded-xl border bg-background p-4 leading-normal">
      <figcaption className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <span className="text-sm font-semibold">{title}</span>
        <span className="text-[11px] text-muted-foreground">Sample data – not market data</span>
      </figcaption>
      {controls && <div className="flex flex-wrap items-end gap-x-6 gap-y-3">{controls}</div>}
      <div ref={setEl} className="space-y-3">{children(width)}</div>
      {readout && <p className="text-sm tabular" aria-live="polite">{readout}</p>}
      <p className="text-xs leading-relaxed text-muted-foreground">{caption}</p>
    </figure>
  )
}

function Slider({ label, value, min, max, step, format, onChange }: {
  label: string; value: number; min: number; max: number; step: number; format: (v: number) => string; onChange: (v: number) => void
}) {
  return (
    <label className="flex min-w-44 flex-1 flex-col gap-1 text-xs text-muted-foreground">
      <span>{label}: <strong className="text-foreground tabular">{format(value)}</strong></span>
      <input type="range" min={min} max={max} step={step} value={value} onChange={(e) => onChange(Number(e.target.value))} className="accent-series-1" />
    </label>
  )
}

function Choice<T extends string | number>({ label, value, options, onChange }: { label: string; value: T; options: { value: T; label: string }[]; onChange: (v: T) => void }) {
  return (
    <div className="flex flex-col gap-1 text-xs text-muted-foreground">
      <span>{label}</span>
      <div className="flex gap-1 rounded-lg border bg-background p-0.5" role="group" aria-label={label}>
        {options.map((o) => (
          <Button key={String(o.value)} type="button" size="xs" variant={o.value === value ? 'secondary' : 'ghost'} aria-pressed={o.value === value} onClick={() => onChange(o.value)}>{o.label}</Button>
        ))}
      </div>
    </div>
  )
}

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

/** Animates a value from `from` to `to` (eased, `ms` long) through `set`; rounds to `step`. Hidden with reduced motion. */
function PlayButton({ from, to, step, ms = 4000, set }: { from: number; to: number; step: number; ms?: number; set: (v: number) => void }) {
  const [playing, setPlaying] = useState(false)
  const frame = useRef(0)
  useEffect(() => () => cancelAnimationFrame(frame.current), [])
  if (prefersReducedMotion()) return null
  const stop = () => { cancelAnimationFrame(frame.current); setPlaying(false) }
  const start = () => {
    const t0 = performance.now()
    setPlaying(true)
    const tick = (now: number) => {
      const t = Math.min(1, (now - t0) / ms)
      const eased = t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) ** 2 / 2
      set(Math.round((from + (to - from) * eased) / step) * step)
      if (t < 1) frame.current = requestAnimationFrame(tick)
      else setPlaying(false)
    }
    frame.current = requestAnimationFrame(tick)
  }
  return <Button type="button" size="xs" variant="outline" onClick={playing ? stop : start}>{playing ? 'Stop' : 'Play'}</Button>
}

const p0 = (v: number) => pct(v, 0)
const p1 = (v: number) => pct(v, 1)

function WeightSplit({ m, label }: { m: Mix | null; label: string }) {
  if (!m) return <p className="text-sm text-muted-foreground">{label}: not possible with these settings.</p>
  return (
    <div className="space-y-1">
      <div className="flex justify-between text-xs text-muted-foreground"><span>{label}</span><span className="tabular">A {p1(m.wA)} · B {p1(1 - m.wA)}</span></div>
      <div className="flex h-3 overflow-hidden rounded-full bg-muted" aria-hidden>
        <div className="transition-[width] duration-200" style={{ width: `${m.wA * 100}%`, background: C.s1 }} />
        <div className="flex-1" style={{ background: C.s2 }} />
      </div>
    </div>
  )
}

// ------------------------------------------------------------------ mean-variance: two stocks

type FrontierMode = 'correlation' | 'sharpe' | 'target' | 'risk'

/** The set of all mixes of the two sample stocks, with the portfolio the guide's objective picks; one slider per mode. */
export function TwoStockFrontier({ mode }: { mode: FrontierMode }) {
  const [rho, setRho] = useState(sampleTwoAssets.rho)
  const [muB, setMuB] = useState(sampleTwoAssets.muB)
  const [target, setTarget] = useState(0.1)
  const [limit, setLimit] = useState(0.22)
  const p: TwoAssetParams = { ...sampleTwoAssets, rho, muB }
  const curve = mixCurve(p)
  const gmv = gmvMix(p)
  const tan = tangencyMix(p)
  const pick = mode === 'correlation' ? gmv : mode === 'sharpe' ? tan : mode === 'target' ? targetReturnMix(p, target) : maxReturnForRiskMix(p, limit)
  const pickLabel = { correlation: 'Minimum variance', sharpe: 'Maximum Sharpe', target: `Lowest risk for ≥ ${p1(target)}`, risk: `Highest return for ≤ ${p1(limit)} volatility` }[mode]

  const controls = {
    correlation: <><Slider label="Correlation of A and B" value={rho} min={-0.9} max={1} step={0.05} format={(v) => v.toFixed(2)} onChange={setRho} /><PlayButton from={1} to={-0.9} step={0.05} set={setRho} /></>,
    sharpe: <><Slider label="Expected return of Stock B" value={muB} min={0.06} max={0.18} step={0.005} format={p1} onChange={setMuB} /><PlayButton from={0.06} to={0.18} step={0.005} set={setMuB} /></>,
    target: <><Slider label="Required return" value={target} min={0.07} max={0.125} step={0.0025} format={(v) => pct(v, 2)} onChange={setTarget} /><PlayButton from={0.07} to={0.125} step={0.0025} set={setTarget} /></>,
    risk: <><Slider label="Volatility limit" value={limit} min={0.14} max={0.32} step={0.005} format={p1} onChange={setLimit} /><PlayButton from={0.14} to={0.32} step={0.005} set={setLimit} /></>,
  }[mode]

  const caption = {
    correlation: 'Each point of the curve is one mix of Stock A (20% volatility) and Stock B (30%). The lower the correlation, the more the curve bends to the left: mixing two stocks that rarely move together removes risk. The marked point is the minimum-variance mix.',
    sharpe: 'The straight line starts at the risk-free rate (3%) and touches the curve at the maximum-Sharpe mix – the steepest line possible. Raise Stock B\'s expected return and watch the weights swing: small changes in an estimate move the result a lot.',
    target: 'The dashed line is the return you require. Below the minimum-variance return it does not matter; above it, the portfolio moves along the curve toward Stock B – each extra point of return costs extra volatility. Above 12% no long-only mix reaches the target.',
    risk: 'The dashed line is your volatility limit. The optimizer takes the highest point of the curve left of that line. A limit below the minimum-variance volatility cannot be met; above 30% the whole portfolio goes into Stock B.',
  }[mode]

  return (
    <Figure
      title={{ correlation: 'How correlation bends the curve', sharpe: 'The tangency line', target: 'Choosing a target return', risk: 'Choosing a volatility limit' }[mode]}
      controls={controls}
      readout={pick ? <>{pickLabel}: <strong>A {p1(pick.wA)} · B {p1(1 - pick.wA)}</strong> · return {p1(pick.ret)} · volatility {p1(pick.vol)} · Sharpe {pick.sharpe.toFixed(2)}</> : <>{pickLabel}: no long-only mix of the two stocks meets this.</>}
      caption={caption}
    >
      {(W) => {
        return (
          <>
            <Plot w={W} x={[0, 0.35]} y={[0, 0.2]} xFormat={p0} yFormat={p0} xLabel="Volatility (annual)" yLabel="Expected return" label={`${pickLabel} on the curve of all mixes of Stock A and Stock B`}>
              {(sx, sy) => (
                <>
                  {mode === 'sharpe' && tan.vol > 0 && <line x1={sx(0)} y1={sy(p.rf)} x2={sx(0.35)} y2={sy(p.rf + tan.sharpe * 0.35)} stroke={C.s3} strokeWidth={1.5} strokeDasharray="5 4" />}
                  {mode === 'target' && <line x1={PAD.l} x2={W - PAD.r} y1={sy(target)} y2={sy(target)} stroke={C.s3} strokeDasharray="5 4" />}
                  {mode === 'risk' && <line x1={sx(limit)} x2={sx(limit)} y1={PAD.t} y2={sy(0)} stroke={C.s3} strokeDasharray="5 4" />}
                  <path d={path(curve.map((m) => [sx(m.vol), sy(m.ret)]))} fill="none" stroke={C.s1} strokeWidth={2.5} />
                  {[{ m: curve[curve.length - 1], t: 'A' }, { m: curve[0], t: 'B' }].map(({ m, t }) => (
                    <g key={t}><circle cx={sx(m.vol)} cy={sy(m.ret)} r={4} fill={C.axis} /><text x={sx(m.vol) + (sx(m.vol) > W - 70 ? -9 : 7)} y={sy(m.ret)} dy={sx(m.vol) > W - 70 ? '-0.6em' : '0.32em'} textAnchor={sx(m.vol) > W - 70 ? 'end' : 'start'} fontSize="12" fill={C.muted}>{`Stock ${t}`}</text></g>
                  ))}
                  {mode !== 'correlation' && <circle cx={sx(gmv.vol)} cy={sy(gmv.ret)} r={4} fill="none" stroke={C.muted} strokeWidth={1.5} />}
                  {pick && <circle cx={sx(pick.vol)} cy={sy(pick.ret)} r={7} fill={C.s2} stroke="var(--background)" strokeWidth={2} />}
                </>
              )}
            </Plot>
            <WeightSplit m={pick} label={pickLabel} />
          </>
        )
      }}
    </Figure>
  )
}

/** Volatility of every mix as a function of the weight in Stock A, with a movable mix. */
export function VolatilityByWeight() {
  const [wA, setWA] = useState(0.5)
  const p = sampleTwoAssets
  const curve = mixCurve(p)
  const gmv = gmvMix(p)
  const at = curve[Math.round(wA * 100)]
  return (
    <Figure
      title="Volatility for every mix of the two stocks"
      controls={<><Slider label="Weight in Stock A" value={wA} min={0} max={1} step={0.01} format={p0} onChange={setWA} /><PlayButton from={0} to={1} step={0.01} set={setWA} /></>}
      readout={<>A {p0(wA)} · B {p0(1 - wA)}: volatility <strong>{p1(at.vol)}</strong>{Math.abs(at.wA - gmv.wA) < 0.006 ? ' – the minimum' : ` (minimum ${p1(gmv.vol)} at A ${p1(gmv.wA)})`}</>}
      caption="Both stocks alone are riskier than many of their mixes: because they are only weakly correlated (0.2), their ups and downs partly cancel. Minimum variance picks the bottom of this curve."
    >
      {(W) => {
        return (
          <>
            <Plot w={W} x={[0, 1]} y={[0.1, 0.32]} xFormat={p0} yFormat={p0} xLabel="Weight in Stock A (rest in Stock B)" yLabel="Volatility" label="Portfolio volatility against the weight in Stock A, lowest near 74%">
              {(sx, sy) => (
                <>
                  <path d={path(curve.map((m) => [sx(m.wA), sy(m.vol)]))} fill="none" stroke={C.s1} strokeWidth={2.5} />
                  <line x1={sx(gmv.wA)} x2={sx(gmv.wA)} y1={sy(gmv.vol)} y2={sy(0.1)} stroke={C.muted} strokeDasharray="3 3" />
                  <circle cx={sx(gmv.wA)} cy={sy(gmv.vol)} r={4} fill="none" stroke={C.muted} strokeWidth={1.5} />
                  <circle cx={sx(at.wA)} cy={sy(at.vol)} r={7} fill={C.s2} stroke="var(--background)" strokeWidth={2} />
                </>
              )}
            </Plot>
          </>
        )
      }}
    </Figure>
  )
}

// ------------------------------------------------------------------ equal weight

export function EqualWeightDiversification() {
  const [n, setN] = useState(5)
  const [rho, setRho] = useState(0.25)
  const sigma = 0.3
  const ns = Array.from({ length: 50 }, (_, i) => i + 1)
  const vol = equalWeightVolatility(n, sigma, rho)
  const floor = sigma * Math.sqrt(rho)
  return (
    <Figure
      title="Adding stocks with equal weights"
      controls={<>
        <Slider label="Number of stocks" value={n} min={1} max={50} step={1} format={(v) => `${v} (each ${p1(1 / v)})`} onChange={setN} />
        <Slider label="Correlation between stocks" value={rho} min={0} max={0.8} step={0.05} format={(v) => v.toFixed(2)} onChange={setRho} />
        <PlayButton from={1} to={50} step={1} ms={5000} set={setN} />
      </>}
      readout={<>{n} stocks at {p1(1 / n)} each: volatility <strong>{p1(vol)}</strong> (one stock: {p1(sigma)}; floor with many stocks: {p1(floor)})</>}
      caption="Every stock here has 30% volatility. The first few additions remove most of the risk; after that the curve flattens toward a floor set by the correlation – the market risk that no amount of stocks diversifies away."
    >
      {(W) => {
        return (
          <>
            <Plot w={W} x={[1, 50]} y={[0, 0.32]} xFormat={(v) => String(v)} yFormat={p0} xLabel="Number of stocks (equal weights)" yLabel="Volatility" label="Equal-weight portfolio volatility falling with the number of stocks toward a floor">
              {(sx, sy) => (
                <>
                  <line x1={PAD.l} x2={W - PAD.r} y1={sy(floor)} y2={sy(floor)} stroke={C.s3} strokeDasharray="5 4" />
                  <path d={path(ns.map((k) => [sx(k), sy(equalWeightVolatility(k, sigma, rho))]))} fill="none" stroke={C.s1} strokeWidth={2.5} />
                  <circle cx={sx(n)} cy={sy(vol)} r={7} fill={C.s2} stroke="var(--background)" strokeWidth={2} />
                </>
              )}
            </Plot>
          </>
        )
      }}
    </Figure>
  )
}

// ------------------------------------------------------------------ ML portfolio construction

export function ConstructionPlayground() {
  const [k, setK] = useState(3)
  const [rule, setRule] = useState<WeightingRule>('rank')
  const [cap, setCap] = useState(0.4)
  const { weights, cash } = constructWeights(sampleScores, k, rule, cap)
  const rows = [...sampleScores.map((s) => ({ label: s.symbol, score: s.score, weight: weights[s.symbol] ?? 0 })), { label: 'Cash', score: null as number | null, weight: cash }]
  const H = 230
  const sy = linear(0, 1, H - PAD.b, PAD.t)
  return (
    <Figure
      title="From scores to weights"
      controls={<>
        <Slider label="Hold the top K" value={k} min={1} max={8} step={1} format={(v) => String(v)} onChange={setK} />
        <Slider label="Cap per stock" value={cap} min={0.1} max={1} step={0.05} format={p0} onChange={setCap} />
        <Choice label="Weighting" value={rule} onChange={setRule} options={[{ value: 'equal', label: 'Equal' }, { value: 'rank', label: 'Rank-linear' }, { value: 'score', label: 'Score' }]} />
        <PlayButton from={1} to={8} step={1} ms={4000} set={setK} />
      </>}
      readout={<>Weights: {Object.entries(weights).map(([s, w]) => `${s} ${p1(w)}`).join(' · ')}{cash > 1e-9 ? ` · cash ${p1(cash)}` : ''}</>}
      caption="Grey bars are the model's scores for eight sample stocks; blue bars are the resulting weights. Rank-linear gives the best stock K shares, the next K − 1 … ; score-proportional follows the score gaps above the lowest selected score, plus 10% of their range so the weakest selected stock keeps a weight. Anything above the cap is passed on to the other holdings – and stays in cash once every holding is at the cap."
    >
      {(W) => {
        const bw = (W - PAD.l - PAD.r) / rows.length
        return (
          <>
            <svg viewBox={`0 0 ${W} ${H}`} className="h-auto w-full" role="img" aria-label="Model scores and resulting portfolio weights per sample stock">
              {[0, 0.25, 0.5, 0.75, 1].map((v) => (
                <g key={v}><line x1={PAD.l} x2={W - PAD.r} y1={sy(v)} y2={sy(v)} stroke={C.grid} /><text x={PAD.l - 6} y={sy(v)} dy="0.32em" textAnchor="end" fontSize="11" fill={C.muted}>{v.toFixed(2)}</text></g>
              ))}
              <text transform={`translate(12 ${(PAD.t + H - PAD.b) / 2}) rotate(-90)`} textAnchor="middle" fontSize="11" fill={C.muted}>score · weight (1 = 100%)</text>
              <line x1={PAD.l} x2={W - PAD.r} y1={sy(cap)} y2={sy(cap)} stroke={C.s3} strokeDasharray="5 4" />
              <text x={W - PAD.r} y={sy(cap) - 4} textAnchor="end" fontSize="11" fill={C.muted}>cap</text>
              {rows.map((r, i) => {
                const x = PAD.l + i * bw
                return (
                  <g key={r.label}>
                    {r.score !== null && <rect x={x + bw * 0.12} width={bw * 0.32} y={sy(r.score)} height={sy(0) - sy(r.score)} rx={2} fill={C.axis} opacity={0.45} />}
                    <rect x={x + bw * (r.score === null ? 0.3 : 0.5)} width={bw * 0.38} y={sy(r.weight)} height={sy(0) - sy(r.weight)} rx={2} fill={r.score === null ? C.s4 : C.s1} style={{ transition: 'y 200ms, height 200ms' }} />
                    <text x={x + bw / 2} y={H - PAD.b + 16} textAnchor="middle" fontSize="12" fill={C.muted}>{r.label}</text>
                  </g>
                )
              })}
            </svg>
          </>
        )
      }}
    </Figure>
  )
}

// ------------------------------------------------------------------ strategy comparison

export function WalkForwardTimeline() {
  const months = 24
  const train = 6
  const [k, setK] = useState(8)
  const H = 124
  const rowY = 30
  const kinds = Array.from({ length: months }, (_, i) => (i < k - train ? 'old' : i < k ? 'train' : i === k ? 'test' : 'future'))
  const fill = { old: 'var(--muted)', train: C.s1, test: C.s2, future: 'transparent' } as const
  return (
    <Figure
      title="One rebalance after another, never looking ahead"
      controls={<><Slider label="Rebalance at the end of month" value={k} min={train} max={months - 1} step={1} format={(v) => String(v)} onChange={setK} /><PlayButton from={train} to={months - 1} step={1} ms={6000} set={setK} /></>}
      readout={<>Decision at the close of month {k} uses months {k - train + 1}–{k} only; it is traded the next day and judged on month {k + 1}. Months after {k + 1} are still unknown.</>}
      caption="Blue: the trailing window the strategy learns from. Orange: the month it is judged on. Each step moves the whole window forward; the results of all orange months, put together, are the backtest."
    >
      {(W) => {
        const bw = (W - PAD.l - PAD.r) / months
        return (
          <>
            <svg viewBox={`0 0 ${W} ${H}`} className="h-auto w-full" role="img" aria-label={`Walk-forward timeline: training months ${k - train + 1} to ${k}, evaluation month ${k + 1}`}>
              {kinds.map((kind, i) => (
                <rect key={i} x={PAD.l + i * bw + 1} y={rowY} width={bw - 2} height={36} rx={3} fill={fill[kind]} stroke={kind === 'future' ? C.grid : 'none'} strokeDasharray={kind === 'future' ? '3 3' : undefined} style={{ transition: 'fill 200ms' }} />
              ))}
              <line x1={PAD.l + k * bw} x2={PAD.l + k * bw} y1={rowY - 12} y2={rowY + 48} stroke="var(--foreground)" strokeWidth={2} style={{ transition: 'x1 200ms, x2 200ms' }} />
              <text x={PAD.l + k * bw} y={rowY - 18} textAnchor="middle" fontSize="11" fill="var(--foreground)">decision</text>
              {Array.from({ length: months }, (_, i) => i + 1).filter((m) => m % 3 === 0 || m === 1).map((m) => (
                <text key={m} x={PAD.l + (m - 0.5) * bw} y={rowY + 56} textAnchor="middle" fontSize="11" fill={C.muted}>{m}</text>
              ))}
              <text x={(PAD.l + W - PAD.r) / 2} y={H - 8} textAnchor="middle" fontSize="11" fill={C.muted}>Month</text>
            </svg>
          </>
        )
      }}
    </Figure>
  )
}

export function CostDrag() {
  const [bps, setBps] = useState(20)
  const gross = useMemo(() => sampleMonthlyReturns(), [])
  const lines = [
    { name: 'Before costs', values: growthAfterCosts(gross, 0, 0), color: C.axis, dash: '5 4' },
    { name: 'Low turnover (10% a month)', values: growthAfterCosts(gross, 0.1, bps), color: C.s1, dash: undefined },
    { name: 'High turnover (80% a month)', values: growthAfterCosts(gross, 0.8, bps), color: C.s2, dash: undefined },
  ]
  // Fixed axis over the whole slider range, so the lines move and the axis stays put.
  const all = [...growthAfterCosts(gross, 0, 0), ...growthAfterCosts(gross, 0.8, 100)]
  return (
    <Figure
      title="The same strategy, different trading costs"
      controls={<><Slider label="Cost per trade" value={bps} min={0} max={100} step={5} format={(v) => `${v} bps (${pct(v / 10_000, 2)})`} onChange={setBps} /><PlayButton from={0} to={100} step={5} set={setBps} /></>}
      readout={<>After 5 years from 100: {lines.map((l) => `${l.name.split(' (')[0].toLowerCase()} ${l.values.at(-1)!.toFixed(0)}`).join(' · ')}</>}
      caption="All three lines have the same gross monthly returns (sixty generated sample months). The only difference is how much is traded each month and what each trade costs. That is why a comparison must charge every strategy the same costs."
    >
      {(W) => {
        return (
          <>
            <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
              {lines.map((l) => <span key={l.name} className="flex items-center gap-1.5"><span className="h-0.5 w-4" style={{ background: l.color }} aria-hidden />{l.name}</span>)}
            </div>
            <Plot w={W} x={[0, 60]} y={[Math.floor(Math.min(...all) / 10) * 10, Math.ceil(Math.max(...all) / 10) * 10]} xFormat={(v) => String(v)} yFormat={(v) => v.toFixed(0)} xLabel="Month" yLabel="Value (start = 100)" label="Growth of 100 before costs and after costs for low and high turnover">
              {(sx, sy) => lines.map((l) => <path key={l.name} d={path(l.values.map((v, i) => [sx(i), sy(v)]))} fill="none" stroke={l.color} strokeWidth={2} strokeDasharray={l.dash} />)}
            </Plot>
          </>
        )
      }}
    </Figure>
  )
}

// ------------------------------------------------------------------ ML models

export function RidgeShrinkage() {
  const [logLambda, setLogLambda] = useState(-1)
  const lambda = 10 ** logLambda
  const H = 220
  const sy = linear(-0.3, 0.45, H - PAD.b, PAD.t)
  const kept = 1 / (1 + lambda)
  return (
    <Figure
      title="The penalty shrinks every coefficient"
      controls={<><Slider label="Penalty λ" value={logLambda} min={-3} max={1} step={0.1} format={(v) => (10 ** v < 0.1 ? (10 ** v).toFixed(3) : (10 ** v).toFixed(2))} onChange={setLogLambda} /><PlayButton from={-3} to={1} step={0.1} set={setLogLambda} /></>}
      readout={<>λ = {lambda < 0.1 ? lambda.toFixed(3) : lambda.toFixed(2)}: every coefficient keeps <strong>{p0(kept)}</strong> of its least-squares size · momentum {ridgeShrink(0.4, lambda).toFixed(3)}%</>}
      caption="Outlined bars: least-squares coefficients (no penalty). Filled bars: ridge. With uncorrelated standardized features – the simple case shown here – each coefficient is divided by 1 + λ; with correlated features ridge shrinks some more than others. The app tries λ ∈ {0.001, 0.01, 0.1, 1} and keeps the one that does best on the validation period."
    >
      {(W) => {
        const bw = (W - PAD.l - PAD.r) / sampleOlsCoefficients.length
        return (
          <>
            <svg viewBox={`0 0 ${W} ${H}`} className="h-auto w-full" role="img" aria-label={`Ridge coefficients of four sample features at penalty ${lambda.toFixed(3)}`}>
              {[-0.2, 0, 0.2, 0.4].map((v) => (
                <g key={v}><line x1={PAD.l} x2={W - PAD.r} y1={sy(v)} y2={sy(v)} stroke={v === 0 ? C.axis : C.grid} /><text x={PAD.l - 6} y={sy(v)} dy="0.32em" textAnchor="end" fontSize="11" fill={C.muted}>{`${v.toFixed(1)}%`}</text></g>
              ))}
              {sampleOlsCoefficients.map((c, i) => {
                const x = PAD.l + i * bw + bw * 0.25
                const r = ridgeShrink(c.beta, lambda)
                return (
                  <g key={c.feature}>
                    <rect x={x} width={bw * 0.5} y={Math.min(sy(c.beta), sy(0))} height={Math.abs(sy(c.beta) - sy(0))} fill="none" stroke={C.axis} strokeDasharray="3 3" />
                    <rect x={x} width={bw * 0.5} y={Math.min(sy(r), sy(0))} height={Math.abs(sy(r) - sy(0))} rx={2} fill={c.beta >= 0 ? C.s1 : C.s2} />
                    <text x={x + bw * 0.25} y={H - PAD.b + 16} textAnchor="middle" fontSize="12" fill={C.muted}>{c.feature}</text>
                  </g>
                )
              })}
            </svg>
          </>
        )
      }}
    </Figure>
  )
}

export function LogisticCurve() {
  const [beta, setBeta] = useState(0.3)
  const [z, setZ] = useState(1)
  const zs = Array.from({ length: 121 }, (_, i) => -3 + i * 0.05)
  const prob = logistic(beta * z)
  return (
    <Figure
      title="From a score to a probability"
      controls={<>
        <Slider label="Coefficient β (momentum)" value={beta} min={0} max={2} step={0.05} format={(v) => v.toFixed(2)} onChange={setBeta} />
        <Slider label="Stock's momentum z" value={z} min={-3} max={3} step={0.1} format={(v) => (v > 0 ? `+${v.toFixed(1)}` : v.toFixed(1))} onChange={setZ} />
        <PlayButton from={-3} to={3} step={0.1} set={setZ} />
      </>}
      readout={<>Score β × z = {(beta * z).toFixed(2)} → probability of beating the benchmark <strong>{p1(prob)}</strong></>}
      caption="The S-curve turns any score into a probability between 0% and 100%. With the guide's realistic β = 0.3 the curve is flat: even an extreme momentum reading moves the probability only a little away from 50%. Raise β to see what a strong (and in markets unusual) signal would look like."
    >
      {(W) => {
        return (
          <>
            <Plot w={W} x={[-3, 3]} y={[0, 1]} xFormat={(v) => (v > 0 ? `+${v}` : String(v))} yFormat={p0} xLabel="Momentum z (standard deviations)" yLabel="Probability" label={`Logistic curve with coefficient ${beta.toFixed(2)}; at z ${z.toFixed(1)} the probability is ${p1(prob)}`}>
              {(sx, sy) => (
                <>
                  <line x1={PAD.l} x2={W - PAD.r} y1={sy(0.5)} y2={sy(0.5)} stroke={C.s3} strokeDasharray="5 4" />
                  <path d={path(zs.map((v) => [sx(v), sy(logistic(beta * v))]))} fill="none" stroke={C.s1} strokeWidth={2.5} />
                  <line x1={sx(z)} x2={sx(z)} y1={sy(0)} y2={sy(prob)} stroke={C.muted} strokeDasharray="3 3" />
                  <circle cx={sx(z)} cy={sy(prob)} r={7} fill={C.s2} stroke="var(--background)" strokeWidth={2} />
                </>
              )}
            </Plot>
          </>
        )
      }}
    </Figure>
  )
}

const TRAIN = samplePoints(80, 11)
const TEST = samplePoints(80, 23)
const GRID = Array.from({ length: 121 }, (_, i) => i / 120)

function SampleScatter({ sx, sy }: { sx: Scale; sy: Scale }) {
  return (
    <>
      {TRAIN.map((q, i) => <circle key={i} cx={sx(q.x)} cy={sy(q.y)} r={2.5} fill={C.axis} opacity={0.5} />)}
      <path d={path(GRID.map((x) => [sx(x), sy(truePattern(x))]))} fill="none" stroke={C.s3} strokeWidth={1.5} strokeDasharray="5 4" />
    </>
  )
}

export function ForestAveraging() {
  const [count, setCount] = useState(1)
  const forest = useMemo(() => fitForest(TRAIN, 100, 4, 5), [])
  const errOne = meanSquaredError(TEST, (x) => predictTree(forest[0], x))
  const err = meanSquaredError(TEST, (x) => predictForest(forest, count, x))
  return (
    <Figure
      title="Averaging more and more trees"
      controls={<><Slider label="Trees in the forest" value={count} min={1} max={100} step={1} format={(v) => String(v)} onChange={setCount} /><PlayButton from={1} to={100} step={1} ms={5000} set={setCount} /></>}
      readout={<>{count} {count === 1 ? 'tree' : 'trees'}: error on new sample points <strong>{err.toFixed(3)}</strong> {count > 1 ? ` (one tree: ${errOne.toFixed(3)})` : ''}</>}
      caption="Grey dots: 80 generated training points around a hidden pattern (green, dashed). Each tree is fitted to a different random resample of them and draws a jumpy step line (thin). Their average (blue) gets smoother and closer to the pattern as trees are added – and its error on 80 fresh points falls."
    >
      {(W) => {
        return (
          <>
            <Plot w={W} x={[0, 1]} y={[-1.8, 2.2]} xFormat={(v) => v.toFixed(1)} yFormat={(v) => v.toFixed(1)} xLabel="Feature value" yLabel="Outcome" label={`Random forest of ${count} trees fitted to sample points`}>
              {(sx, sy) => (
                <>
                  <SampleScatter sx={sx} sy={sy} />
                  <path d={path(GRID.map((x) => [sx(x), sy(predictTree(forest[count - 1], x))]))} fill="none" stroke={C.s2} strokeWidth={1} opacity={0.7} />
                  <path d={path(GRID.map((x) => [sx(x), sy(predictForest(forest, count, x))]))} fill="none" stroke={C.s1} strokeWidth={2.5} />
                </>
              )}
            </Plot>
          </>
        )
      }}
    </Figure>
  )
}

export function BoostingSteps() {
  const [rate, setRate] = useState(0.05)
  const [steps, setSteps] = useState(20)
  const max = 200
  const model = useMemo(() => fitBoosting(TRAIN, max, rate, 3), [rate])
  const errors = useMemo(() => Array.from({ length: max + 1 }, (_, m) => ({
    train: meanSquaredError(TRAIN, (x) => predictBoosting(model, m, rate, x)),
    test: meanSquaredError(TEST, (x) => predictBoosting(model, m, rate, x)),
  })), [model, rate])
  const best = errors.reduce((b, e, m) => (e.test < errors[b].test ? m : b), 0)
  const yMax = Math.ceil(Math.max(...errors.map((e) => Math.max(e.train, e.test))) * 10) / 10
  return (
    <Figure
      title="Adding trees one by one"
      controls={<>
        <Slider label="Trees added" value={steps} min={0} max={max} step={1} format={(v) => String(v)} onChange={setSteps} />
        <Choice label="Learning rate" value={rate} onChange={setRate} options={[{ value: 0.05, label: '0.05' }, { value: 0.3, label: '0.3' }, { value: 0.8, label: '0.8' }]} />
        <PlayButton from={0} to={max} step={1} ms={6000} set={setSteps} />
      </>}
      readout={<>{steps} trees: training error {errors[steps].train.toFixed(3)} · error on new points <strong>{errors[steps].test.toFixed(3)}</strong> · lowest at {best} trees</>}
      caption="Top: the model's prediction (blue) after each added tree, on the same sample points as the random forest. Bottom: the training error keeps falling, but the error on new points stops improving and creeps up again once the trees start fitting noise. That is why the number of trees is chosen on a validation period. A higher learning rate gets there faster – and overfits sooner."
    >
      {(W) => {
        return (
          <>
            <Plot w={W} x={[0, 1]} y={[-1.8, 2.2]} xFormat={(v) => v.toFixed(1)} yFormat={(v) => v.toFixed(1)} xLabel="Feature value" yLabel="Outcome" label={`Gradient boosting prediction after ${steps} trees`}>
              {(sx, sy) => (
                <>
                  <SampleScatter sx={sx} sy={sy} />
                  <path d={path(GRID.map((x) => [sx(x), sy(predictBoosting(model, steps, rate, x))]))} fill="none" stroke={C.s1} strokeWidth={2.5} />
                </>
              )}
            </Plot>
            <div className="flex flex-wrap gap-x-4 text-xs text-muted-foreground">
              <span className="flex items-center gap-1.5"><span className="h-0.5 w-4" style={{ background: C.axis }} aria-hidden />Training error</span>
              <span className="flex items-center gap-1.5"><span className="h-0.5 w-4" style={{ background: C.s2 }} aria-hidden />Error on new points</span>
            </div>
            <Plot w={W} x={[0, max]} y={[0, yMax]} height={190} xFormat={(v) => String(v)} yFormat={(v) => v.toFixed(1)} xLabel="Trees added" yLabel="Mean squared error" label="Training error and test error against the number of trees">
              {(sx, sy) => (
                <>
                      <path d={path(errors.map((e, m) => [sx(m), sy(e.train)]))} fill="none" stroke={C.axis} strokeWidth={2} />
                      <path d={path(errors.map((e, m) => [sx(m), sy(e.test)]))} fill="none" stroke={C.s2} strokeWidth={2} />
                      <line x1={sx(best)} x2={sx(best)} y1={PAD.t} y2={sy(0)} stroke={C.s3} strokeDasharray="5 4" />
                      <line x1={sx(steps)} x2={sx(steps)} y1={PAD.t} y2={sy(0)} stroke="var(--foreground)" strokeWidth={1.5} />
                </>
              )}
            </Plot>
          </>
        )
      }}
    </Figure>
  )
}

// ------------------------------------------------------------------ which figures each guide shows

const figures: Record<string, { theory?: () => ReactNode; example?: () => ReactNode }> = {
  'minimum-variance': { theory: () => <VolatilityByWeight />, example: () => <TwoStockFrontier mode="correlation" /> },
  'maximum-sharpe': { example: () => <TwoStockFrontier mode="sharpe" /> },
  'target-return': { example: () => <TwoStockFrontier mode="target" /> },
  'maximum-return-for-risk': { example: () => <TwoStockFrontier mode="risk" /> },
  'equal-weight': { theory: () => <EqualWeightDiversification /> },
  'ml-portfolio-construction': { example: () => <ConstructionPlayground /> },
  'strategy-comparison': { theory: () => <WalkForwardTimeline />, example: () => <CostDrag /> },
  'ridge-regression': { theory: () => <RidgeShrinkage /> },
  'logistic-regression': { example: () => <LogisticCurve /> },
  'random-forest': { theory: () => <ForestAveraging /> },
  'gradient-boosting': { theory: () => <BoostingSteps /> },
}

/** The figure of one guide for one section: the idea under "The theory", the hands-on version under "Worked example". */
export function GuideFigure({ guide, section }: { guide: string; section: 'theory' | 'example' }) {
  return <>{figures[guide]?.[section]?.() ?? null}</>
}
