import type { ReactNode } from 'react'
import { InfoTip, MetricValue } from '@/components/common'
import type { StrategyPerformance } from '@/lib/types'
import { num, pct } from '@/lib/format'

/** Side-by-side performance, risk, trading and concentration statistics (identical definitions for every strategy). */
const metricRows: [string, (p: StrategyPerformance) => ReactNode, string][] = [
  ['Total return', (p) => pct(p.totalReturn), 'Cumulative return over the backtest.'],
  ['Annualized return', (p) => <MetricValue metric={p.annualizedReturn} format={(v) => pct(v)} />, 'Geometric annualized return (CAGR).'],
  ['Annualized volatility', (p) => <MetricValue metric={p.annualizedVolatility} format={(v) => pct(v)} />, 'Standard deviation of daily returns × √252.'],
  ['Sharpe ratio', (p) => <MetricValue metric={p.sharpeRatio} format={(v) => num(v)} />, 'Annualized excess return over the risk-free rate per unit of volatility.'],
  ['Sortino ratio', (p) => <MetricValue metric={p.sortinoRatio} format={(v) => num(v)} />, 'Like Sharpe but penalizing only downside deviation.'],
  ['Max drawdown', (p) => <MetricValue metric={p.maxDrawdown} format={(v) => pct(v)} />, 'Largest peak-to-trough decline.'],
  ['Excess return vs benchmark', (p) => <MetricValue metric={p.excessAnnualizedReturn} format={(v) => pct(v, 2)} />, 'Annualized return minus the benchmark’s.'],
  ['Tracking error', (p) => <MetricValue metric={p.trackingError} format={(v) => pct(v)} />, 'Annualized volatility of daily return differences vs the benchmark.'],
  ['Turnover (total)', (p) => pct(p.totalTurnover, 0), 'Sum of one-way turnover at all rebalances.'],
  ['Transaction costs', (p) => pct(p.transactionCostsFraction, 2), 'Costs paid as a fraction of initial capital.'],
  ['Avg. holdings', (p) => num(p.averageHoldings, 1), 'Average number of positions after rebalancing.'],
  ['Avg. effective holdings', (p) => num(p.averageEffectiveHoldings, 1), 'Concentration: (Σw)²/Σw² – equals the holding count for equal weights.'],
]

export function PerformanceTable({ items, colors }: { items: StrategyPerformance[]; colors: string[] }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm tabular">
        <thead>
          <tr className="text-left text-xs text-muted-foreground">
            <th className="py-1 font-medium">Metric</th>
            {items.map((p, i) => <th key={p.name} className="px-2 font-medium"><span className="inline-flex items-center gap-1.5"><span className="inline-block size-2 rounded-full" style={{ background: colors[i] }} aria-hidden />{p.name}</span></th>)}
          </tr>
        </thead>
        <tbody>{metricRows.map(([name, fn, hint]) => (
          <tr key={name} className="border-t">
            <td className="py-1.5 text-muted-foreground"><span className="inline-flex items-center gap-1">{name}<InfoTip>{hint}</InfoTip></span></td>
            {items.map((p) => <td key={p.name} className="px-2">{fn(p)}</td>)}
          </tr>
        ))}</tbody>
      </table>
    </div>
  )
}

