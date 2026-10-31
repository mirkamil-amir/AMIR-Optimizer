import { useState } from 'react'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { errorMessage } from '@/lib/api'
import { money } from '@/lib/format'
import { portfolioPath } from '@/lib/portfolio-context'
import { useCreatePortfolio, useFxInfo, useSettings } from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import { CurrencySelect } from '@/components/currency'
import { MatrixLoader } from '@/components/matrix-loader'

export interface PortfolioDraft {
  name: string
  description: string
  currency: string
  capital: string
  maxWeight: string
}

/** Validation of the creation form (pure, unit-tested). Returns field → message. */
// eslint-disable-next-line react-refresh/only-export-components
export function validateDraft(d: PortfolioDraft): Partial<Record<keyof PortfolioDraft, string>> {
  const errors: Partial<Record<keyof PortfolioDraft, string>> = {}
  if (!d.name.trim()) errors.name = 'Enter a name.'
  else if (d.name.trim().length > 100) errors.name = 'At most 100 characters.'
  if (d.description.length > 500) errors.description = 'At most 500 characters.'
  const capital = Number(d.capital)
  if (d.capital.trim() === '' || !Number.isFinite(capital)) errors.capital = 'Enter an amount.'
  else if (capital < 100) errors.capital = 'At least 100.'
  else if (capital > 1_000_000_000) errors.capital = 'At most 1,000,000,000.'
  if (d.maxWeight.trim() !== '') {
    const w = Number(d.maxWeight)
    if (!Number.isFinite(w) || w < 1 || w > 100) errors.maxWeight = 'Between 1 and 100 %.'
  }
  return errors
}

/**
 * Step 1 and 3 of New portfolio: details of the new portfolio and the final "Forge portfolio". Step 2 – choosing the
 * stocks it may invest in – is the stock browser on the same page (the temporary selection). Nothing is stored until the
 * user clicks Create; the chosen stocks become the portfolio's working universe for the optimizer, not holdings.
 * `currency` belongs to the page: the stock list below shows its prices in the same currency and can change it too.
 */
export function CreatePortfolioPanel({ currency, onCurrencyChange }: { currency: string; onCurrencyChange: (currency: string) => void }) {
  const settings = useSettings()
  const fx = useFxInfo()
  const selection = useSelection()
  const create = useCreatePortfolio()
  const navigate = useNavigate()
  const [draft, setDraft] = useState<PortfolioDraft>({ name: '', description: '', currency: '', capital: '100000', maxWeight: '' })
  const [touched, setTouched] = useState(false)
  const errors = validateDraft(draft)
  const show = (k: keyof PortfolioDraft) => (touched ? errors[k] : undefined)
  const set = (k: keyof PortfolioDraft) => (e: React.ChangeEvent<HTMLInputElement>) => setDraft({ ...draft, [k]: e.target.value })
  const foreign = selection.currencies.filter((c) => c !== currency)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setTouched(true)
    if (Object.keys(errors).length > 0) return
    try {
      const p = await create.mutateAsync({
        name: draft.name.trim(),
        description: draft.description.trim() || undefined,
        baseCurrency: currency,
        initialInvestment: Number(draft.capital),
        universe: selection.symbols.length > 0 ? selection.symbols : undefined,
        maxWeight: draft.maxWeight.trim() ? Number(draft.maxWeight) / 100 : undefined,
      })
      // The selection is now stored as the portfolio's universe; start the next portfolio with a clean slate.
      selection.clear()
      toast.success(`Portfolio “${p.name}” created`, { description: `${money(p.cashBalance, p.baseCurrency)} simulated cash · ${p.settings.universe.length} stocks in its universe` })
      navigate(`${portfolioPath(p.id)}?created=1`)
    } catch (err) {
      toast.error('Could not create the portfolio', { description: errorMessage(err) })
    }
  }

  return (
    // The steps (describe, choose stocks, create) are in the page title's tip; the card holds only the form.
    <Card aria-label="New portfolio">
      <CardContent>
        <form onSubmit={submit} noValidate className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,20rem)]">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5 sm:col-span-2">
              <Label htmlFor="np-name">Name</Label>
              <Input id="np-name" placeholder="e.g. Long-term diversified" maxLength={100} value={draft.name} onChange={set('name')}
                aria-invalid={!!show('name')} aria-describedby={show('name') ? 'np-name-err' : undefined} />
              {show('name') && <p id="np-name-err" role="alert" className="text-xs text-destructive">{show('name')}</p>}
            </div>
            <div className="space-y-1.5 sm:col-span-2">
              <Label htmlFor="np-desc">Description (optional)</Label>
              <Input id="np-desc" placeholder="What is this portfolio for?" maxLength={500} value={draft.description} onChange={set('description')} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="np-ccy">Currency</Label>
              <CurrencySelect id="np-ccy" value={currency} onChange={onCurrencyChange} supported={fx.data?.currencies}
                first={settings.data?.defaultCurrency} label="Portfolio currency" className="w-full" />
              <p className="text-[11px] text-muted-foreground">Holds the cash; fixed after creation.</p>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="np-cap">Starting cash ({currency})</Label>
              <Input id="np-cap" type="number" inputMode="decimal" min="100" step="1000" value={draft.capital} onChange={set('capital')}
                aria-invalid={!!show('capital')} aria-describedby={show('capital') ? 'np-cap-err' : undefined} />
              {show('capital') && <p id="np-cap-err" role="alert" className="text-xs text-destructive">{show('capital')}</p>}
              <p className="text-[11px] text-muted-foreground">Simulated money. You can deposit or withdraw later.</p>
            </div>
            <details className="sm:col-span-2">
              <summary className="cursor-pointer text-sm text-muted-foreground">Advanced: allocation limit</summary>
              <div className="mt-2 max-w-48 space-y-1.5">
                <Label htmlFor="np-max">Maximum weight per stock (%)</Label>
                <Input id="np-max" type="number" min="1" max="100" placeholder="35" value={draft.maxWeight} onChange={set('maxWeight')}
                  aria-invalid={!!show('maxWeight')} />
                {show('maxWeight') && <p role="alert" className="text-xs text-destructive">{show('maxWeight')}</p>}
                <p className="text-[11px] text-muted-foreground">Raised automatically if too low for the number of stocks. All other settings are in Optimize.</p>
              </div>
            </details>
          </div>

          <div className="space-y-3 rounded-lg border bg-muted/30 p-3" aria-live="polite">
            <div className="text-sm font-medium">Review</div>
            <dl className="space-y-1 text-sm">
              <div className="flex justify-between gap-2"><dt className="text-muted-foreground">Name</dt><dd className="truncate font-medium">{draft.name.trim() || '—'}</dd></div>
              <div className="flex justify-between gap-2"><dt className="text-muted-foreground">Cash</dt><dd className="tabular">{errors.capital ? '—' : money(Number(draft.capital), currency)}</dd></div>
              <div className="flex justify-between gap-2"><dt className="text-muted-foreground">Stocks chosen</dt><dd><Badge variant="secondary" className="tabular">{selection.count}</Badge></dd></div>
            </dl>
            <p className="text-xs text-muted-foreground">
              {selection.count === 0
                ? 'No stocks chosen yet: the optimizer would start from all tracked stocks. Pick stocks in the list below.'
                : selection.count === 1 ? 'One stock: an optimizer needs at least two – add more or record transactions directly.'
                : `${selection.count} stocks become the portfolio's optimization universe (not holdings).`}
            </p>
            {foreign.length > 0 && (
              <p className="text-xs text-muted-foreground">Stocks in {foreign.join(', ')} are valued in {currency} with dated reference rates.</p>
            )}
            <Button type="submit" className="w-full" disabled={create.isPending}>
              {create.isPending && <MatrixLoader />} {create.isPending ? 'Forging…' : 'Forge portfolio'}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
