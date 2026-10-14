import { useState } from 'react'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { Plus, Trash2 } from 'lucide-react'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { DataBadge, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useAuth } from '@/lib/auth'
import { useAssets, useChangePassword, useCreatePortfolio, useDeletePortfolio, useSettings, useUpdateSettings } from '@/lib/queries'
import type { AppSettings, Asset, PortfolioSummary } from '@/lib/types'
import { dateTime, money } from '@/lib/format'

const settingsSchema = z.object({
  activeProvider: z.string().min(1),
  defaultBenchmark: z.string().min(1),
  defaultRiskFreeRate: z.number().min(-5).max(25),
  stalePriceDays: z.number().int().min(1).max(60),
})
type SettingsForm = z.infer<typeof settingsSchema>

const portfolioSchema = z.object({
  name: z.string().trim().min(1, 'Required').max(100),
  initialInvestment: z.number({ error: 'Enter an amount' }).min(100, 'At least 100').max(1_000_000_000),
})
type PortfolioForm = z.infer<typeof portfolioSchema>

const asNum = { setValueAs: (v: unknown) => (v === '' || v === null || v === undefined ? undefined : Number(v)) }

export function SettingsPage() {
  return (
    <>
      <PageHeader title="Settings" description="Your simulated portfolios and account. Data-provider settings are managed by administrators." />
      <div className="grid gap-6 lg:grid-cols-2">
        <PortfoliosCard />
        <AccountCard />
      </div>
    </>
  )
}

const passwordSchema = z.object({
  currentPassword: z.string().min(1, 'Required'),
  newPassword: z.string()
    .min(10, 'At least 10 characters')
    .regex(/[a-z]/, 'Include a lower-case letter')
    .regex(/[A-Z]/, 'Include an upper-case letter')
    .regex(/[0-9]/, 'Include a digit'),
  confirm: z.string(),
}).refine((v) => v.newPassword === v.confirm, { path: ['confirm'], message: 'Passwords do not match' })

function AccountCard() {
  const { user } = useAuth()
  const change = useChangePassword()
  const { register, handleSubmit, reset, formState: { errors } } = useForm<z.infer<typeof passwordSchema>>({
    resolver: zodResolver(passwordSchema),
    defaultValues: { currentPassword: '', newPassword: '', confirm: '' },
  })
  const onSubmit = handleSubmit(async (v) => {
    try {
      await change.mutateAsync({ currentPassword: v.currentPassword, newPassword: v.newPassword })
      reset()
      toast.success('Password changed', { description: 'Other sessions are signed out within a minute.' })
    } catch (e) {
      toast.error('Could not change the password', { description: errorMessage(e) })
    }
  })
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Account</CardTitle>
        <CardDescription className="text-xs">
          {user?.displayName} · {user?.email} · roles: {user?.roles.join(', ')}
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={onSubmit} className="space-y-3" noValidate>
          <div className="space-y-1.5">
            <Label htmlFor="cur">Current password</Label>
            <Input id="cur" type="password" autoComplete="current-password" aria-invalid={!!errors.currentPassword} {...register('currentPassword')} />
            {errors.currentPassword && <p role="alert" className="text-xs text-destructive">{errors.currentPassword.message}</p>}
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="new">New password</Label>
            <Input id="new" type="password" autoComplete="new-password" aria-invalid={!!errors.newPassword} {...register('newPassword')} />
            {errors.newPassword && <p role="alert" className="text-xs text-destructive">{errors.newPassword.message}</p>}
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="confirm">Confirm new password</Label>
            <Input id="confirm" type="password" autoComplete="new-password" aria-invalid={!!errors.confirm} {...register('confirm')} />
            {errors.confirm && <p role="alert" className="text-xs text-destructive">{errors.confirm.message}</p>}
          </div>
          <Button type="submit" disabled={change.isPending}>Change password</Button>
        </form>
      </CardContent>
    </Card>
  )
}

/** Data provider and analytics defaults (administrators; the server rejects changes by other users). */
export function AppSettingsCard() {
  const { data, isLoading, error } = useSettings()
  const assets = useAssets()
  if (isLoading || !assets.data) return <LoadingBlock />
  if (error || !data) return <ErrorState error={error} />
  // Mounted only with data so the selects are controlled from the first render.
  return <AppSettingsForm key={data.activeProvider + data.defaultBenchmark} data={data} assets={assets.data} />
}

function AppSettingsForm({ data, assets: assetList }: { data: AppSettings; assets: Asset[] }) {
  const assets = { data: assetList }
  const update = useUpdateSettings()
  const { register, control, handleSubmit, formState: { errors, isDirty } } = useForm<SettingsForm>({
    resolver: zodResolver(settingsSchema),
    defaultValues: { activeProvider: data.activeProvider, defaultBenchmark: data.defaultBenchmark, defaultRiskFreeRate: data.defaultRiskFreeRate * 100, stalePriceDays: data.stalePriceDays },
  })

  const onSubmit = handleSubmit(async (v) => {
    try {
      await update.mutateAsync({ activeProvider: v.activeProvider, defaultCurrency: data.defaultCurrency, defaultBenchmark: v.defaultBenchmark, defaultRiskFreeRate: v.defaultRiskFreeRate / 100, stalePriceDays: v.stalePriceDays })
      toast.success('Settings saved')
    } catch (e) {
      toast.error('Could not save settings', { description: errorMessage(e) })
    }
  })

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Application settings</CardTitle>
        <CardDescription className="text-xs">Switching the provider changes which stored price series all analytics use. Simulated and real data are never mixed.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={onSubmit} className="space-y-4" noValidate>
          <div className="space-y-1.5">
            <Label>Market data provider</Label>
            <Controller control={control} name="activeProvider" render={({ field }) => (
              <Select value={field.value} onValueChange={(v) => field.onChange(String(v))} items={data.providers.map((p) => ({ value: p.name, label: p.displayName }))}>
                <SelectTrigger className="w-full" aria-label="Market data provider"><SelectValue /></SelectTrigger>
                <SelectContent>
                  {data.providers.map((p) => (
                    <SelectItem key={p.name} value={p.name} disabled={!p.isConfigured}>
                      {p.displayName}{!p.isConfigured ? (p.requiresApiKey ? ' – API key missing' : ' – disabled') : ''}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )} />
            <ul className="space-y-1 pt-1 text-xs text-muted-foreground">
              {data.providers.map((p) => (
                <li key={p.name} className="flex flex-wrap items-center gap-1.5">
                  <span className="font-medium text-foreground">{p.name}:</span>
                  {p.isSimulated && <DataBadge kind="simulated" />}
                  {p.requiresApiKey && <Badge variant="outline">{p.isConfigured ? 'API key set (server)' : 'Set MarketData__AlphaVantage__ApiKey on the server'}</Badge>}
                  <span>{p.terms}</span>
                </li>
              ))}
            </ul>
          </div>
          <div className="grid grid-cols-3 gap-3">
            <div className="space-y-1.5">
              <Label>Default benchmark</Label>
              <Controller control={control} name="defaultBenchmark" render={({ field }) => (
                <Select value={field.value} onValueChange={(v) => field.onChange(String(v))} items={(assets.data ?? []).map((a) => ({ value: a.symbol, label: a.symbol }))}>
                  <SelectTrigger className="w-full" aria-label="Default benchmark"><SelectValue /></SelectTrigger>
                  <SelectContent>{(assets.data ?? []).map((a) => <SelectItem key={a.symbol} value={a.symbol}>{a.symbol}</SelectItem>)}</SelectContent>
                </Select>
              )} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="rf">Risk-free rate (%)</Label>
              <Input id="rf" type="number" step="0.05" aria-invalid={!!errors.defaultRiskFreeRate} {...register('defaultRiskFreeRate', asNum)} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="stale">Stale after (days)</Label>
              <Input id="stale" type="number" aria-invalid={!!errors.stalePriceDays} {...register('stalePriceDays', asNum)} />
            </div>
          </div>
          <div className="text-xs text-muted-foreground">
            Default currency: {data.defaultCurrency} (FX conversion is not supported). Scheduled refresh: {data.scheduledRefreshEnabled ? `every ${data.scheduledRefreshIntervalHours} h` : 'disabled'} (server configuration).
          </div>
          <Button type="submit" disabled={!isDirty || update.isPending}>Save settings</Button>
        </form>
      </CardContent>
    </Card>
  )
}

function PortfoliosCard() {
  const { portfolios, select } = usePortfolioContext()
  const create = useCreatePortfolio()
  const remove = useDeletePortfolio()
  const settings = useSettings()
  const [toDelete, setToDelete] = useState<PortfolioSummary | null>(null)
  const { register, handleSubmit, reset, formState: { errors } } = useForm<PortfolioForm>({ resolver: zodResolver(portfolioSchema), defaultValues: { initialInvestment: 100000 } })

  const onCreate = handleSubmit(async (v) => {
    try {
      const p = await create.mutateAsync({ name: v.name, initialInvestment: v.initialInvestment, baseCurrency: settings.data?.defaultCurrency ?? 'USD' })
      select(p.id)
      reset({ name: '', initialInvestment: 100000 })
      toast.success(`Portfolio “${p.name}” created`, { description: `${money(p.cashBalance, p.baseCurrency)} simulated cash` })
    } catch (e) {
      toast.error('Could not create portfolio', { description: errorMessage(e) })
    }
  })

  async function onDelete() {
    if (!toDelete) return
    try {
      await remove.mutateAsync(toDelete.id)
      toast.success(`Deleted “${toDelete.name}”`)
    } catch (e) {
      toast.error('Delete failed', { description: errorMessage(e) })
    }
    setToDelete(null)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Portfolios</CardTitle>
        <CardDescription className="text-xs">Each portfolio starts with simulated cash. Optimization settings are edited in the Optimize workspace.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <form onSubmit={onCreate} className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_10rem_auto] sm:items-end" noValidate>
          <div className="space-y-1.5">
            <Label htmlFor="pname">Name</Label>
            <Input id="pname" placeholder="e.g. Core US equities" aria-invalid={!!errors.name} {...register('name')} />
            {errors.name && <p role="alert" className="text-xs text-destructive">{errors.name.message}</p>}
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="pamt">Investment ({settings.data?.defaultCurrency ?? 'USD'})</Label>
            <Input id="pamt" type="number" step="1000" aria-invalid={!!errors.initialInvestment} {...register('initialInvestment', asNum)} />
            {errors.initialInvestment && <p role="alert" className="text-xs text-destructive">{errors.initialInvestment.message}</p>}
          </div>
          <Button type="submit" disabled={create.isPending}><Plus /> Create</Button>
        </form>

        <ul className="divide-y rounded-md border">
          {portfolios.map((p) => (
            <li key={p.id} className="flex items-center justify-between gap-3 px-3 py-2 text-sm">
              <div>
                <div className="font-medium">{p.name}</div>
                <div className="text-xs text-muted-foreground tabular">
                  {money(p.totalValue, p.baseCurrency)} · {p.holdings} holding{p.holdings === 1 ? '' : 's'} · created {dateTime(p.createdAtUtc)}
                </div>
              </div>
              <Button variant="ghost" size="icon-sm" aria-label={`Delete ${p.name}`} onClick={() => setToDelete(p)}><Trash2 /></Button>
            </li>
          ))}
          {portfolios.length === 0 && <li className="px-3 py-4 text-sm text-muted-foreground">No portfolios yet.</li>}
        </ul>
      </CardContent>

      <AlertDialog open={!!toDelete} onOpenChange={(o) => !o && setToDelete(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete “{toDelete?.name}”?</AlertDialogTitle>
            <AlertDialogDescription>This permanently removes the simulated portfolio with its holdings, ledger, proposals and optimization runs.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={onDelete}>Delete</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  )
}
