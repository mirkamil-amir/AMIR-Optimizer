import { Suspense, useState } from 'react'
import { NavLink, Outlet } from 'react-router'
import { BarChart3, Briefcase, Database, FlaskConical, Gauge, History, LayoutDashboard, Menu, Settings, Sigma } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetTitle } from '@/components/ui/sheet'
import { DataBadge, LoadingBlock } from '@/components/common'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useMarketStatus } from '@/lib/queries'
import { money, relativeTime } from '@/lib/format'
import { cn } from '@/lib/utils'

const nav = [
  { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
  { to: '/holdings', label: 'Holdings', icon: Briefcase },
  { to: '/optimize', label: 'Optimize', icon: Sigma },
  { to: '/trades', label: 'Trades & ledger', icon: History },
  { to: '/analytics', label: 'Analytics', icon: BarChart3 },
  { to: '/backtest', label: 'Backtest', icon: Gauge },
  { to: '/market-data', label: 'Market data', icon: Database },
  { to: '/settings', label: 'Settings', icon: Settings },
]

function NavItems({ onNavigate }: { onNavigate?: () => void }) {
  return (
    <nav className="flex flex-col gap-0.5" aria-label="Main">
      {nav.map((n) => (
        <NavLink
          key={n.to}
          to={n.to}
          end={n.end}
          onClick={onNavigate}
          className={({ isActive }) =>
            cn(
              'flex items-center gap-2.5 rounded-md px-2.5 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground',
              isActive && 'bg-muted font-medium text-foreground',
            )
          }
        >
          <n.icon className="size-4" aria-hidden />
          {n.label}
        </NavLink>
      ))}
    </nav>
  )
}

function Brand() {
  return (
    <div className="flex items-center gap-2 px-2.5">
      <div className="flex size-7 items-center justify-center rounded-md bg-foreground text-background">
        <Sigma className="size-4" aria-hidden />
      </div>
      <div className="leading-tight">
        <div className="text-sm font-semibold">Portfolio Optimizer</div>
        <div className="text-[11px] text-muted-foreground">Paper trading · analytics</div>
      </div>
    </div>
  )
}

function DataStatus() {
  const { data } = useMarketStatus()
  if (!data) return null
  const enabled = data.coverage.filter((c) => c.isEnabled)
  const stale = enabled.filter((c) => c.freshness !== 'Fresh').length
  const lastRetrieved = enabled.map((c) => c.lastRetrievedAtUtc).filter(Boolean).sort().at(-1) ?? null
  const running = data.recentJobs.some((j) => j.status === 'Queued' || j.status === 'Running')
  return (
    <div className="space-y-1.5 rounded-lg border p-2.5 text-xs">
      <div className="flex items-center justify-between">
        <span className="font-medium">{data.activeProvider}</span>
        {data.activeProviderIsSimulated ? <DataBadge kind="simulated" /> : stale > 0 ? <DataBadge kind="stale" label={`${stale} stale`} /> : <DataBadge kind="live" />}
      </div>
      <div className="text-muted-foreground">{running ? 'Synchronizing…' : `Updated ${relativeTime(lastRetrieved)}`}</div>
    </div>
  )
}

function PortfolioSelector() {
  const { portfolios, portfolioId, select } = usePortfolioContext()
  if (portfolios.length === 0) return null
  return (
    <Select value={portfolioId ?? null} onValueChange={(v) => v && select(String(v))} items={portfolios.map((p) => ({ value: p.id, label: p.name }))}>
      <SelectTrigger className="w-56" aria-label="Selected portfolio">
        <SelectValue placeholder="Select portfolio" />
      </SelectTrigger>
      <SelectContent>
        {portfolios.map((p) => (
          <SelectItem key={p.id} value={p.id}>
            <span className="flex w-full items-center justify-between gap-3">
              <span>{p.name}</span>
              <span className="tabular text-xs text-muted-foreground">{money(p.totalValue, p.baseCurrency, { compact: true })}</span>
            </span>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

export function AppShell() {
  const [open, setOpen] = useState(false)
  return (
    <div className="min-h-svh bg-muted/30">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded focus:bg-background focus:px-3 focus:py-2">
        Skip to content
      </a>
      <aside className="fixed inset-y-0 left-0 hidden w-60 flex-col gap-6 border-r bg-background px-3 py-4 lg:flex">
        <Brand />
        <NavItems />
        <div className="mt-auto space-y-2">
          <DataStatus />
          <p className="px-1 text-[11px] leading-snug text-muted-foreground">Simulated trading only. No real orders are placed. Not investment advice.</p>
        </div>
      </aside>

      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="left" className="w-64 gap-6 p-4">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <Brand />
          <NavItems onNavigate={() => setOpen(false)} />
          <DataStatus />
        </SheetContent>
      </Sheet>

      <div className="lg:pl-60">
        <header className="sticky top-0 z-30 flex h-14 items-center gap-3 border-b bg-background/90 px-4 backdrop-blur lg:px-8">
          <Button variant="ghost" size="icon" className="lg:hidden" aria-label="Open navigation" onClick={() => setOpen(true)}>
            <Menu />
          </Button>
          <PortfolioSelector />
          <div className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
            <FlaskConical className="size-3.5" aria-hidden />
            <span className="hidden sm:inline">Paper portfolio · local single-user</span>
          </div>
        </header>
        <main id="main" className="mx-auto max-w-7xl px-4 py-6 lg:px-8">
          <Suspense fallback={<LoadingBlock height="h-96" />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
    </div>
  )
}
