import { useEffect } from 'react'
import { Link, useLocation } from 'react-router'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { Plus } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { DataBadge, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { AppearanceCard } from '@/components/theme-controls'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useAuth } from '@/lib/auth'
import { useAssets, useChangePassword, useSettings, useUpdateSettings } from '@/lib/queries'
import type { AppSettings, Asset } from '@/lib/types'

const settingsSchema = z.object({
  activeProvider: z.string().min(1),
  defaultBenchmark: z.string().min(1),
  defaultRiskFreeRate: z.number().min(-5).max(25),
  stalePriceDays: z.number().int().min(1).max(60),
})
type SettingsForm = z.infer<typeof settingsSchema>

const asNum = { setValueAs: (v: unknown) => (v === '' || v === null || v === undefined ? undefined : Number(v)) }

export function SettingsPage() {
  const { hash } = useLocation()
  useEffect(() => {
    if (hash === '#appearance') document.getElementById('appearance')?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [hash])
  return (
    <>
      <PageHeader title="Settings" info="Your account and appearance. Portfolios are managed under My portfolios; data-provider settings are managed by administrators." />
      <div className="grid gap-6 lg:grid-cols-2">
        <PortfoliosCard />
        <AccountCard />
      </div>
      <div className="mt-6"><AppearanceCard /></div>
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
            Default currency: {data.defaultCurrency} (preselected for new portfolios; stocks in other currencies are converted with dated exchange rates). Scheduled refresh: {data.scheduledRefreshEnabled ? `every ${data.scheduledRefreshIntervalHours} h` : 'disabled'} (server configuration).
          </div>
          <Button type="submit" disabled={!isDirty || update.isPending}>Save settings</Button>
        </form>
      </CardContent>
    </Card>
  )
}

/** Portfolios are created and managed on the overview (My portfolios); Settings only points there. */
function PortfoliosCard() {
  const { portfolios } = usePortfolioContext()
  const active = portfolios.filter((p) => p.status === 'Active').length
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Portfolios</CardTitle>
        <CardDescription className="text-xs">
          You have {active} active portfolio{active === 1 ? '' : 's'}{portfolios.length > active ? ` and ${portfolios.length - active} archived` : ''}.
          Create, rename, archive and delete portfolios on the overview.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-wrap gap-2">
        <Button render={<Link to="/" />}>My portfolios</Button>
        <Button variant="outline" render={<Link to="/stocks" />}><Plus /> Portfolio Creation</Button>
      </CardContent>
    </Card>
  )
}
