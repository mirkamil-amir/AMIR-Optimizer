import { Suspense, useEffect, useRef, useState } from 'react'
import { Link, Outlet, useLocation, useNavigate } from 'react-router'
import { Menu } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Sheet, SheetContent, SheetTitle } from '@/components/ui/sheet'
import { DataBadge, LoadingBlock } from '@/components/common'
import { BRAND, MatrixWordmark } from '@/components/brand'
import { ThemeQuickToggle } from '@/components/theme-controls'
import { MapControl, SelectionOnMap, WorldMapBackdrop } from '@/components/world-map'
import { useAuth } from '@/lib/auth'
import {
  adminSection, optimizerSections, primaryForPath, primaryNav, sidebarEntryForPath, type NavEntry, type NavSection,
} from '@/lib/navigation'
import { identityLabel } from '@/lib/identity'
import { useMarketSummary } from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import { relativeTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { WorldMapProvider } from '@/lib/world-map'
import { attachNavZoom } from '@/lib/nav-zoom'

function SidebarLink({ entry, active, onNavigate }: { entry: NavEntry; active: boolean; onNavigate?: () => void }) {
  return (
    <Link
      to={entry.to}
      onClick={onNavigate}
      aria-current={active ? 'page' : undefined}
      data-zoom-active={active ? '' : undefined}
      className={cn(
        'nav-zoom font-nav flex items-center rounded-md px-2.5 py-2 text-sm text-muted-foreground transition-colors hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none',
        active && 'bg-muted font-semibold text-foreground shadow-[inset_2px_0_0_var(--sidebar-primary)]',
      )}
    >
      <span>{entry.label}</span>
    </Link>
  )
}

/** Groups of entries, separated by space only (no headings; the administration area by a rule). */
function SidebarGroups({ sections, activeTo, onNavigate }: { sections: NavSection[]; activeTo?: string; onNavigate?: () => void }) {
  return sections.map((section) => (
    <div key={section.title} className="flex flex-col gap-0.5">
      {section.items.map((n) => <SidebarLink key={n.to} entry={n} active={n.to === activeTo} onNavigate={onNavigate} />)}
    </div>
  ))
}

/** Left sidebar: the Optimizer workspace's sections and, for administrators, the administration area. */
function SidebarNav({ onNavigate }: { onNavigate?: () => void }) {
  const { isAdministrator } = useAuth()
  const { pathname } = useLocation()
  const activeTo = sidebarEntryForPath(pathname, isAdministrator)?.to
  // Magnification follows the pointer anywhere over the sidebar, not only over the links.
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const area = ref.current?.parentElement
    return area ? attachNavZoom(area) : undefined
  }, [])
  return (
    <div ref={ref} className="flex flex-col gap-4">
      <nav className="flex flex-col gap-4" aria-label="Optimizer sections">
        <SidebarGroups sections={optimizerSections} activeTo={activeTo} onNavigate={onNavigate} />
      </nav>
      {isAdministrator && (
        <nav className="flex flex-col gap-4 border-t pt-4" aria-label="Administration">
          <SidebarGroups sections={[adminSection]} activeTo={activeTo} onNavigate={onNavigate} />
        </nav>
      )}
    </div>
  )
}

/** Top navigation: the primary areas. Scrolls horizontally when the viewport is too narrow. */
export function PrimaryNav() {
  const { pathname } = useLocation()
  const current = primaryForPath(pathname)
  const activeRef = useRef<HTMLAnchorElement>(null)
  const navRef = useRef<HTMLElement>(null)
  useEffect(() => {
    activeRef.current?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' })
  }, [current?.id])
  // Magnification along the row, like the sidebar's.
  useEffect(() => (navRef.current ? attachNavZoom(navRef.current, 'x') : undefined), [])
  return (
    <nav ref={navRef} aria-label="Primary" className="nav-zoom-row nav-scroll overflow-x-auto px-2 lg:px-6">
      <ul className="flex min-w-max items-stretch gap-0.5">
        {primaryNav.map((p) => {
          const active = current?.id === p.id
          return (
            <li key={p.id} className="flex">
              <Link
                ref={active ? activeRef : undefined}
                to={p.to}
                // "page" on the destination itself, "true" on a child route of that area (e.g. Optimizer › Backtest).
                aria-current={active ? (pathname === p.to ? 'page' : 'true') : undefined}
                // Inside the Optimizer workspace the item does nothing, so the open section and its inputs are kept.
                onClick={(e) => { if (active && p.id === 'optimizer') e.preventDefault() }}
                data-zoom-active={active ? '' : undefined}
                className={cn(
                  'nav-zoom font-nav inline-flex items-center gap-2 border-b-2 px-3 py-2.5 text-[0.95rem] whitespace-nowrap transition-colors focus-visible:rounded-t-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none focus-visible:ring-inset',
                  active
                    ? 'border-primary font-semibold text-foreground'
                    : 'border-transparent text-muted-foreground hover:border-border hover:text-foreground',
                )}
              >
                <span>{p.label}</span>
              </Link>
            </li>
          )
        })}
      </ul>
    </nav>
  )
}

function Brand() {
  return (
    <Link to="/" className="block rounded-md px-2.5 focus-visible:ring-2 focus-visible:ring-ring" aria-label={`${BRAND.name} – home`}>
      <MatrixWordmark compact />
    </Link>
  )
}

function DataStatus() {
  const { data } = useMarketSummary()
  // Data-source names are operational details: only administrators see them.
  const { isAdministrator } = useAuth()
  if (!data) return null
  const stale = data.stale + data.missing
  const lastRetrieved = data.lastRetrievedAtUtc
  const running = data.syncRunning
  return (
    <div className="space-y-1.5 rounded-lg border p-2.5 text-xs">
      <div className="flex items-center justify-between">
        <span className="font-medium">{isAdministrator ? data.activeProvider : 'Market data'}</span>
        {data.activeProviderIsSimulated ? <DataBadge kind="simulated" /> : stale > 0 ? <DataBadge kind="stale" label={`${stale} stale`} /> : <DataBadge kind="live" />}
      </div>
      <div className="text-muted-foreground">{running ? 'Synchronizing…' : `Updated ${relativeTime(lastRetrieved)}`}</div>
    </div>
  )
}


function SelectionIndicator() {
  const { count } = useSelection()
  return (
    <Button variant="outline" size="sm" render={<Link to="/stocks" />} aria-label={`Portfolio Creation: ${count} stock${count === 1 ? '' : 's'} selected`}>
      <span>Selection</span>
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
      {/* The role is not shown (only read out by screen readers via the label above). */}
      <div className="min-w-0 truncate text-sm font-semibold text-foreground">{id.name}</div>
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
        Account
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
        <DropdownMenuItem onClick={() => navigate('/')}>My portfolios</DropdownMenuItem>
        <DropdownMenuItem onClick={() => navigate('/settings')}>Account settings</DropdownMenuItem>
        <DropdownMenuItem onClick={() => navigate('/settings#appearance')}>Appearance &amp; themes</DropdownMenuItem>
        {user.isAdministrator && <DropdownMenuItem onClick={() => navigate('/admin')}>Administration</DropdownMenuItem>}
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => void logout()}>Sign out</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

export function AppShell() {
  const [open, setOpen] = useState(false)
  const { isAdministrator } = useAuth()
  const menuLabel = isAdministrator ? 'Optimizer and administration menu' : 'Optimizer menu'
  return (
    <WorldMapProvider>
    <div className="min-h-svh bg-muted/30">
      <SelectionOnMap />
      {/* Background world map behind the content area (below the sidebar on large screens). */}
      <WorldMapBackdrop className="fixed inset-0 z-0 lg:left-60" />
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded focus:bg-background focus:px-3 focus:py-2">
        Skip to content
      </a>
      <aside className="sidebar-surface fixed inset-y-0 left-0 z-20 hidden w-60 flex-col gap-6 overflow-y-auto border-r bg-sidebar px-3 py-4 text-sidebar-foreground lg:flex">
        <Brand />
        <SidebarNav />
        <div className="mt-auto space-y-2">
          <DataStatus />
          <p className="px-1 text-[11px] leading-snug text-muted-foreground">Simulated trading only. No real orders are placed. Not investment advice.</p>
        </div>
      </aside>

      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="left" className="sidebar-surface w-64 gap-6 bg-sidebar p-4 text-sidebar-foreground">
          <SheetTitle className="sr-only">{menuLabel}</SheetTitle>
          <Brand />
          <SidebarNav onNavigate={() => setOpen(false)} />
          <DataStatus />
        </SheetContent>
      </Sheet>

      <div className="relative z-10 lg:pl-60">
        <header className="sticky top-0 z-30 border-b bg-background/90 backdrop-blur">
          <div className="flex h-14 items-center gap-3 px-4 lg:px-8">
            <Button variant="ghost" size="icon" className="lg:hidden" aria-label={`Open ${menuLabel}`} aria-expanded={open} onClick={() => setOpen(true)}>
              <Menu />
            </Button>
            <IdentityBadge />
            <div className="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
              <span className="hidden items-center gap-1 2xl:flex">Simulated trading only</span>
              <SelectionIndicator />
              {/* Text-only controls need room: on phones the map and theme stay off the header (theme: Account › Appearance). */}
              <div className="hidden items-center gap-2 sm:flex">
                <MapControl />
                <ThemeQuickToggle />
              </div>
              <UserMenu />
            </div>
          </div>
          <PrimaryNav />
        </header>
        <main id="main" className="mx-auto min-h-[calc(100svh-6.25rem)] max-w-7xl px-4 py-6 lg:px-8">
          <Suspense fallback={<LoadingBlock height="h-96" />}>
            <Outlet />
          </Suspense>
        </main>
      </div>
    </div>
    </WorldMapProvider>
  )
}
