import { Link } from 'react-router'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { strategyKey } from '@/lib/strategies'
import type { PortfolioStrategy } from '@/lib/types'

/** Chooses one of the user's portfolios explicitly (the remembered one is only the default, never acted on silently). */
export function PortfolioPicker({ value, onChange, id = 'portfolio-picker', label = 'Portfolio' }: {
  value: string | undefined; onChange: (id: string) => void; id?: string; label?: string
}) {
  const { portfolios } = usePortfolioContext()
  const items = portfolios.map((p) => ({ value: p.id, label: `${p.name} (${p.baseCurrency})${p.status === 'Archived' ? ' · archived' : ''}` }))
  if (portfolios.length === 0) {
    return <p className="text-xs text-muted-foreground">No portfolio yet. <Link to="/" className="underline underline-offset-4">Create one</Link>.</p>
  }
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id} className="text-xs">{label}</Label>
      <Select value={value ?? null} onValueChange={(v) => v && onChange(String(v))} items={items}>
        <SelectTrigger id={id} className="w-full" aria-label={label}><SelectValue placeholder="Choose a portfolio" /></SelectTrigger>
        <SelectContent>{items.map((p) => <SelectItem key={p.value} value={p.value}>{p.label}</SelectItem>)}</SelectContent>
      </Select>
    </div>
  )
}

/** One optimization of the portfolio: Mathematical (mean-variance run) or ML (experiment), listed together. */
export function StrategySelect({ strategies, value, onChange, label, disabledKey }: {
  strategies: PortfolioStrategy[]; value: string | null; onChange: (key: string | null) => void; label: string; disabledKey?: string | null
}) {
  const items = strategies.map((s) => ({ value: strategyKey(s), label: `${s.kind === 'Mathematical' ? 'Math' : 'ML'} · ${s.label}` }))
  return (
    <div className="space-y-1.5">
      <Label className="text-xs">{label}</Label>
      <Select value={value} onValueChange={(v) => onChange(v ? String(v) : null)} items={items}>
        <SelectTrigger className="w-full" aria-label={label}><SelectValue placeholder="Choose an optimization" /></SelectTrigger>
        <SelectContent>
          {strategies.map((s) => (
            <SelectItem key={strategyKey(s)} value={strategyKey(s)} disabled={strategyKey(s) === disabledKey}>
              <span className="flex flex-col">
                <span>{s.kind === 'Mathematical' ? 'Math' : 'ML'} · {s.label}</span>
                <span className="text-[11px] text-muted-foreground">{s.detail} · {s.stocks} stocks</span>
              </span>
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  )
}

/** Shown when a portfolio has no optimization to choose. */
export function NoStrategies({ portfolioId }: { portfolioId: string }) {
  return (
    <p className="text-xs text-muted-foreground">
      This portfolio has no successful optimization yet. Run a <Link to={`/portfolios/${portfolioId}/optimize`} className="underline underline-offset-4">Math Optimization</Link> or
      an <Link to={`/portfolios/${portfolioId}/ml-optimize`} className="underline underline-offset-4">ML Optimization</Link> for it first.
    </p>
  )
}
