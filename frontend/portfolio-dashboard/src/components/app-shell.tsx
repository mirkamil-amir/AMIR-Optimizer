import { Suspense, useState } from 'react'
import { Link, NavLink, Outlet, useNavigate } from 'react-router'
import {
  Activity, BarChart3, Eye, Newspaper, BookOpen, BrainCircuit, GraduationCap, Briefcase, Database, FlaskConical, Gauge, GitCompareArrows, History, LayoutDashboard, ListChecks, LogOut, Menu,
  Palette, ScrollText, Search, Settings, ShieldCheck, Sigma, Sparkles, UserRound, Users,
} from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetTitle } from '@/components/ui/sheet'
import { DataBadge, LoadingBlock } from '@/components/common'
import { AmirWordmark } from '@/components/brand'
import { ThemeQuickToggle } from '@/components/theme-controls'
import { useAuth } from '@/lib/auth'
import { identityLabel } from '@/lib/identity'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useMarketStatus } from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import { money, relativeTime } from '@/lib/format'
import { cn } from '@/lib/utils'

interface NavEntry { to: string; label: string; icon: typeof Search; end?: boolean }

const sections: { title: string; items: NavEntry[] }[] = [
  {
    title: 'Research',
    items: [
      { to: '/research', label: 'AI research', icon: Sparkles },
      { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
      { to: '/watchlist', label: 'Watchlist', icon: Eye },
      { to: '/news', label: 'Financial news', icon: Newspaper },
      { to: '/stocks', label: 'Stock selection', icon: Search, end: true },
      { to: '/optimize', label: 'Optimize', icon: Sigma },
      { to: '/machine-learning', label: 'Machine learning', icon: BrainCircuit },
      { to: '/strategy-comparison', label: 'Strategy comparison', icon: GitCompareArrows },
      { to: '/history', label: 'Optimization history', icon: ListChecks },
      { to: '/backtest', label: 'Backtest', icon: Gauge },
    ],
  },
  {
    title: 'Learn',
    items: [
      { to: '/learn', label: 'Strategy guide', icon: GraduationCap, end: true },
      { to: '/learn/glossary', label: 'Glossary', icon: BookOpen },
    ],
  },
  {
    title: 'Paper portfolio',
    items: [
      { to: '/holdings', label: 'Holdings', icon: Briefcase },
      { to: '/trades', label: 'Trades & ledger', icon: History },
      { to: '/analytics', label: 'Analytics', icon: BarChart3 },
    ],
  },
  {
    title: 'Data',
    items: [
      { to: '/market-data', label: 'Market data', icon: Database },
      { to: '/settings', label: 'Settings', icon: Settings },
    ],
  },
]

const adminSection: { title: string; items: NavEntry[] } = {
  title: 'Administration',
  items: [
    { to: '/admin', label: 'Admin overview', icon: Activity, end: true },
    { to: '/admin/users', label: 'Users', icon: Users },
    { to: '/admin/settings', label: 'Admin settings', icon: ShieldCheck },
  ],
}

function NavItems({ onNavigate }: { onNavigate?: () => void }) {
  const { isAdministrator } = useAuth()
  const all = isAdministrator ? [...sections, adminSection] : sections
  return (
    <nav className="flex flex-col gap-4" aria-label="Main">
      {all.map((section) => (
        <div key={section.title} className="flex flex-col gap-0.5">
          <div className="px-2.5 pb-1 text-[11px] font-medium tracking-wide text-muted-foreground uppercase">{section.title}</div>
          {section.items.map((n) => (
        <NavLink
          key={n.to}
          to={n.to}
          end={n.end}
          onClick={onNavigate}
          className={({ isActive }) =>
            cn(
              'flex items-center gap-2.5 rounded-md px-2.5 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground',
              isActive && 'bg-muted font-medium text-foreground shadow-[inset_2px_0_0_var(--sidebar-primary)]',
            )
          }
        >
          <n.icon className="size-4" aria-hidden />
          {n.label}
        </NavLink>
          ))}
        </div>
      ))}
    </nav>
  )
}

function Brand() {
  return (
    <Link to="/" className="block rounded-md px-2.5 focus-visible:ring-2 focus-visible:ring-ring" aria-label="AMIR Optimizer – home">
      <AmirWordmark compact />
    </Link>
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

function SelectionIndicator() {
  const { count } = useSelection()
  return (
    <Button variant="outline" size="sm" render={<Link to="/stocks" />} aria-label={`Stock selection: ${count} selected`}>
      <ListChecks /> <span className="hidden sm:inline">Selection</span>
      <Badge variant="secondary" className="tabular">{count}</Badge>
    </Button>
  )
}

/** Who is signed in, top-left of every authenticated page (name and role from the server-side profile). */
function IdentityBadge() {
  const { user } = useAuth()
  if (!user) return null
  const id = identityLabel(user)
  return (
    <div className="flex min-w-0 items-center gap-2.5" aria-label={`Signed in as ${id.name}, ${id.role}`}>
      <span
        aria-hidden
        className={cn(
          'grid size-8 shrink-0 place-items-center rounded-full text-xs font-semibold',
          user.isAdministrator ? 'bg-primary text-primary-foreground' : 'bg-muted text-foreground',
        )}
      >
        {id.initials}
      </span>
      <div className="min-w-0 leading-tight">
        <div className="truncate text-sm font-semibold text-foreground">{id.name}</div>
        <div className="flex items-center gap-1 truncate text-[11px] text-muted-foreground">
          {user.isAdministrator && <ShieldCheck className="size-3" aria-hidden />}
          {id.role}
        </div>
      </div>
    </div>
  )
}

function UserMenu() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  if (!user) return null
  const id = identityLabel(user)
  return (
    <DropdownMenu>
      <DropdownMenuTrigger render={<Button variant="ghost" size="sm" aria-label="Account menu" />}>
        <UserRound /> <span className="hidden md:inline">Account</span>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuGroup>
          <DropdownMenuLabel className="text-xs">
            <div className="font-medium text-foreground">{id.name}</div>
            <div className="truncate font-normal text-muted-foreground">{user.email}</div>
            <Badge variant="outline" className="mt-1">{id.role}</Badge>
          </DropdownMenuLabel>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => navigate('/settings')}><Settings /> Account &amp; portfolios</DropdownMenuItem>
        <DropdownMenuItem onClick={() => navigate('/settings#appearance')}><Palette /> Appearance &amp; themes</DropdownMenuItem>
        {user.isAdministrator && <DropdownMenuItem onClick={() => navigate('/admin')}><ScrollText /> Administration</DropdownMenuItem>}
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => void logout()}><LogOut /> Sign out</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

export function AppShell() {
  const [open, setOpen] = useState(false)
  return (
    <div className="min-h-svh bg-muted/30">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded focus:bg-background focus:px-3 focus:py-2">
        Skip to content
      </a>
      <aside className="sidebar-surface fixed inset-y-0 left-0 hidden w-60 flex-col gap-6 overflow-y-auto border-r bg-sidebar px-3 py-4 text-sidebar-foreground lg:flex">
        <Brand />
        <NavItems />
        <div className="mt-auto space-y-2">
          <DataStatus />
          <p className="px-1 text-[11px] leading-snug text-muted-foreground">Simulated trading only. No real orders are placed. Not investment advice.</p>
        </div>
      </aside>

      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="left" className="sidebar-surface w-64 gap-6 bg-sidebar p-4 text-sidebar-foreground">
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
          <IdentityBadge />
          <div className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
            <span className="hidden items-center gap-1 2xl:flex"><FlaskConical className="size-3.5" aria-hidden /> Simulated trading only</span>
            <div className="hidden md:block"><PortfolioSelector /></div>
            <SelectionIndicator />
            <ThemeQuickToggle />
            <UserMenu />
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
