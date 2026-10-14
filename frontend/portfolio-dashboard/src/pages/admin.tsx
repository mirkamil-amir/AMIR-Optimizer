import { useState } from 'react'
import { toast } from 'sonner'
import { Check, Loader2, Search, ShieldCheck, ShieldOff, Upload, UserCheck, UserX } from 'lucide-react'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { DataTable, Pager, type Column } from '@/components/data-table'
import { EmptyState, ErrorState, LoadingBlock, PageHeader, Warnings } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { useAdminUsers, useAuditEvents, useImportCatalogue, useSetUserRoles, useSetUserStatus } from '@/lib/queries'
import type { AuditEvent, UserAccountStatus, UserSummary } from '@/lib/types'
import { dateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { AppSettingsCard } from './settings'

const statusTabs: { value: UserAccountStatus | 'All'; label: string }[] = [
  { value: 'Pending', label: 'Pending' },
  { value: 'Approved', label: 'Approved' },
  { value: 'Suspended', label: 'Suspended' },
  { value: 'Rejected', label: 'Rejected' },
  { value: 'All', label: 'All' },
]

const statusStyles: Record<UserAccountStatus, string> = {
  Pending: 'border-warn/50 text-warn',
  Approved: 'border-gain/40 text-gain',
  Rejected: 'border-loss/40 text-loss',
  Suspended: 'border-loss/40 text-loss',
}

type PendingAction = { user: UserSummary; kind: 'status'; status: UserAccountStatus } | { user: UserSummary; kind: 'roles'; makeAdmin: boolean }

export function AdminUsersPage() {
  const { user: me } = useAuth()
  const [tab, setTab] = useState<UserAccountStatus | 'All'>('Pending')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [showTestAccounts, setShowTestAccounts] = useState(false)
  const users = useAdminUsers(tab === 'All' ? undefined : tab, search.trim() || undefined, page, showTestAccounts)
  const setStatus = useSetUserStatus()
  const setRoles = useSetUserRoles()
  const [pending, setPending] = useState<PendingAction | null>(null)
  const [reason, setReason] = useState('')

  const confirm = async () => {
    if (!pending) return
    try {
      if (pending.kind === 'status') {
        await setStatus.mutateAsync({ id: pending.user.id, status: pending.status, reason: reason || undefined })
        toast.success(`${pending.user.email}: ${pending.status}`)
      } else {
        const roles = pending.makeAdmin ? ['User', 'Administrator'] : ['User']
        await setRoles.mutateAsync({ id: pending.user.id, roles })
        toast.success(`${pending.user.email}: ${pending.makeAdmin ? 'now an administrator' : 'administrator role removed'}`)
      }
    } catch (e) {
      toast.error('Action failed', { description: errorMessage(e) })
    }
    setPending(null)
    setReason('')
  }

  const columns: Column<UserSummary>[] = [
    {
      key: 'user',
      header: 'User',
      sortValue: (u) => u.email,
      cell: (u) => (
        <div>
          <div className="font-medium">
            {u.displayName}
            {u.isTestAccount && <Badge variant="outline" className="ml-1.5 border-series-2/60 text-[10px]" title="Automated-test account (reserved .test domain)">Test account</Badge>}
          </div>
          <div className="text-xs text-muted-foreground">{u.email}</div>
        </div>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      sortValue: (u) => u.status,
      cell: (u) => (
        <div className="space-y-0.5">
          <Badge variant="outline" className={statusStyles[u.status]}>{u.status}</Badge>
          {u.isLockedOut && <Badge variant="outline" className="ml-1 border-warn/50 text-warn">Locked out</Badge>}
          {u.statusReason && <div className="max-w-48 truncate text-[11px] text-muted-foreground" title={u.statusReason}>{u.statusReason}</div>}
        </div>
      ),
    },
    { key: 'roles', header: 'Roles', cell: (u) => <span className="text-xs">{u.roles.join(', ')}</span> },
    { key: 'created', header: 'Applied', sortValue: (u) => u.createdAtUtc, cell: (u) => <span className="text-xs">{dateTime(u.createdAtUtc)}</span> },
    { key: 'login', header: 'Last sign-in', sortValue: (u) => u.lastLoginAtUtc ?? '', cell: (u) => <span className="text-xs">{dateTime(u.lastLoginAtUtc)}</span>, className: 'hidden md:table-cell' },
    {
      key: 'actions',
      header: <span className="sr-only">Actions</span>,
      align: 'right',
      cell: (u) => {
        if (u.id === me?.id) return <span className="text-xs text-muted-foreground">You</span>
        const isAdmin = u.roles.includes('Administrator')
        return (
          <div className="flex flex-wrap justify-end gap-1">
            {u.status === 'Pending' && (
              <>
                <Button size="xs" onClick={() => setPending({ user: u, kind: 'status', status: 'Approved' })}><Check /> Approve</Button>
                <Button size="xs" variant="outline" onClick={() => setPending({ user: u, kind: 'status', status: 'Rejected' })}><UserX /> Reject</Button>
              </>
            )}
            {u.status === 'Approved' && (
              <>
                <Button size="xs" variant="outline" onClick={() => setPending({ user: u, kind: 'status', status: 'Suspended' })}><ShieldOff /> Suspend</Button>
                <Button size="xs" variant="ghost" onClick={() => setPending({ user: u, kind: 'roles', makeAdmin: !isAdmin })}>
                  <ShieldCheck /> {isAdmin ? 'Remove admin' : 'Make admin'}
                </Button>
              </>
            )}
            {(u.status === 'Suspended' || u.status === 'Rejected') && (
              <Button size="xs" variant="outline" onClick={() => setPending({ user: u, kind: 'status', status: 'Approved' })}>
                <UserCheck /> {u.status === 'Suspended' ? 'Reactivate' : 'Approve'}
              </Button>
            )}
          </div>
        )
      },
    },
  ]

  return (
    <>
      <PageHeader title="User management" description="Review applications, approve or reject accounts, suspend access and assign the administrator role. Every action is audited." />
      <Card className="py-4">
        <CardContent className="space-y-3 px-4">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <Tabs value={tab} onValueChange={(v) => { setTab(v as UserAccountStatus | 'All'); setPage(1) }}>
              <TabsList>{statusTabs.map((t) => <TabsTrigger key={t.value} value={t.value}>{t.label}</TabsTrigger>)}</TabsList>
            </Tabs>
            <div className="flex items-center gap-2 text-sm">
              <Switch id="show-test" checked={showTestAccounts} onCheckedChange={(v) => { setShowTestAccounts(v); setPage(1) }} />
              <Label htmlFor="show-test" className="font-normal">Show test accounts</Label>
            </div>
            <div className="relative sm:w-72">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
              <Input value={search} onChange={(e) => { setSearch(e.target.value); setPage(1) }} placeholder="Search e-mail or name" className="pl-8" aria-label="Search users" />
            </div>
          </div>
          {users.error ? <ErrorState error={users.error} /> : users.isLoading ? <LoadingBlock /> : (
            <>
              <DataTable
                rows={users.data?.items ?? []}
                columns={columns}
                rowKey={(u) => u.id}
                caption="Users"
                empty={<EmptyState title={tab === 'Pending' ? 'No pending applications' : 'No users found'} />}
              />
              <Pager page={page} totalPages={users.data?.totalPages ?? 1} onChange={setPage} />
            </>
          )}
        </CardContent>
      </Card>

      <AlertDialog open={!!pending} onOpenChange={(o) => { if (!o) { setPending(null); setReason('') } }}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {pending?.kind === 'status' ? `Set ${pending.user.email} to ${pending.status}?` : pending?.makeAdmin ? `Make ${pending.user.email} an administrator?` : `Remove administrator role from ${pending?.user.email}?`}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {pending?.kind === 'status' && pending.status === 'Approved' && 'The user gets access to the application immediately.'}
              {pending?.kind === 'status' && pending.status === 'Suspended' && 'All access ends immediately, including open sessions. Data is kept.'}
              {pending?.kind === 'status' && pending.status === 'Rejected' && 'The application is declined; the account cannot use the application.'}
              {pending?.kind === 'roles' && (pending.makeAdmin ? 'Administrators can manage users, settings and the instrument catalogue.' : 'The user keeps normal access.')}
            </AlertDialogDescription>
          </AlertDialogHeader>
          {pending?.kind === 'status' && pending.status !== 'Approved' && (
            <div className="space-y-1.5">
              <Label htmlFor="reason">Reason (optional, visible to administrators)</Label>
              <Input id="reason" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} />
            </div>
          )}
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={confirm} variant={pending?.kind === 'status' && pending.status !== 'Approved' ? 'destructive' : 'default'}>
              {(setStatus.isPending || setRoles.isPending) && <Loader2 className="animate-spin" />}Confirm
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}

export function AdminSettingsPage() {
  return (
    <>
      <PageHeader title="Administration" description="Data provider and defaults, the shared instrument catalogue and the audit trail. Administrators only – enforced by the server." />
      <Tabs defaultValue="settings">
        <TabsList>
          <TabsTrigger value="settings">Data &amp; defaults</TabsTrigger>
          <TabsTrigger value="catalogue">Instrument catalogue</TabsTrigger>
          <TabsTrigger value="audit">Audit log</TabsTrigger>
        </TabsList>
        <TabsContent value="settings" className="mt-4 max-w-2xl"><AppSettingsCard /></TabsContent>
        <TabsContent value="catalogue" className="mt-4 max-w-3xl"><CatalogueImportCard /></TabsContent>
        <TabsContent value="audit" className="mt-4"><AuditLogCard /></TabsContent>
      </Tabs>
    </>
  )
}

function CatalogueImportCard() {
  const importCatalogue = useImportCatalogue()
  const [csv, setCsv] = useState('')
  const [source, setSource] = useState('')
  const [track, setTrack] = useState(false)
  const [overwrite, setOverwrite] = useState(false)
  const result = importCatalogue.data

  const onFile = async (file: File | undefined) => {
    if (!file) return
    if (file.size > 2_000_000) { toast.error('The file is larger than 2 MB'); return }
    setCsv(await file.text())
    if (!source) setSource(`${file.name}, imported ${new Date().toISOString().slice(0, 10)}`)
  }

  const submit = async () => {
    try {
      const r = await importCatalogue.mutateAsync({ csv, source, track, overwriteMetadata: overwrite })
      toast.success('Catalogue imported', { description: `${r.inserted} new, ${r.updated} updated, ${r.unchanged} unchanged` })
    } catch (e) {
      toast.error('Import failed', { description: errorMessage(e) })
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Import a universe file</CardTitle>
        <CardDescription className="text-xs">
          CSV columns: <code>symbol,name,sector,industry,exchange,currency,type</code> (lines starting with # are ignored). Only import data you
          are licensed to use. Metadata is stored with the source you enter and is never inferred. The bundled S&amp;P 500 list (Wikipedia, CC BY-SA 4.0) is loaded automatically.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="space-y-1.5">
          <Label htmlFor="cat-file">CSV file</Label>
          <Input id="cat-file" type="file" accept=".csv,text/csv" onChange={(e) => onFile(e.target.files?.[0])} />
          {csv && <p className="text-xs text-muted-foreground">{csv.split('\n').filter((l) => l.trim() && !l.startsWith('#')).length - 1} data line(s) loaded.</p>}
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="cat-source">Source / provenance</Label>
          <Input id="cat-source" value={source} onChange={(e) => setSource(e.target.value)} placeholder="e.g. Vendor X constituents file, 2026-10-01" />
        </div>
        <div className="flex flex-wrap gap-6">
          <div className="flex items-center gap-2">
            <Switch id="cat-track" checked={track} onCheckedChange={setTrack} />
            <Label htmlFor="cat-track" className="font-normal">Track (include in scheduled price refreshes)</Label>
          </div>
          <div className="flex items-center gap-2">
            <Switch id="cat-overwrite" checked={overwrite} onCheckedChange={setOverwrite} />
            <Label htmlFor="cat-overwrite" className="font-normal">Overwrite existing name/sector/industry</Label>
          </div>
        </div>
        <Button onClick={submit} disabled={!csv || source.trim().length < 3 || importCatalogue.isPending}>
          {importCatalogue.isPending ? <Loader2 className="animate-spin" /> : <Upload />} Import
        </Button>
        {result && (
          <div className="space-y-2 text-sm">
            <p className="tabular">{result.rows} rows · {result.inserted} inserted · {result.updated} updated · {result.unchanged} unchanged</p>
            <Warnings items={result.errors} title="Rows skipped or not changed" />
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function AuditLogCard() {
  const [page, setPage] = useState(1)
  const events = useAuditEvents(page)
  const columns: Column<AuditEvent>[] = [
    { key: 'time', header: 'Time', cell: (e) => <span className="text-xs">{dateTime(e.timestampUtc)}</span> },
    { key: 'action', header: 'Action', cell: (e) => <code className={cn('text-xs', !e.success && 'text-loss')}>{e.action}{!e.success && ' (failed)'}</code> },
    { key: 'actor', header: 'Actor', cell: (e) => <span className="text-xs">{e.actorEmail ?? e.actorUserId ?? 'system'}</span> },
    { key: 'target', header: 'Target', cell: (e) => <span className="text-xs text-muted-foreground">{e.targetType ? `${e.targetType} ${e.targetId ?? ''}` : '—'}</span>, className: 'hidden lg:table-cell' },
    { key: 'details', header: 'Details', cell: (e) => <span className="block max-w-md truncate text-xs" title={e.details ?? ''}>{e.details ?? ''}</span> },
    { key: 'ip', header: 'IP', cell: (e) => <span className="text-xs text-muted-foreground">{e.ipAddress ?? ''}</span>, className: 'hidden xl:table-cell' },
  ]
  return (
    <Card className="py-4">
      <CardContent className="px-4">
        {events.error ? <ErrorState error={events.error} /> : events.isLoading ? <LoadingBlock /> : (
          <>
            <DataTable rows={events.data?.items ?? []} columns={columns} rowKey={(e) => String(e.id)} dense caption="Audit events" empty={<EmptyState title="No audit events" />} />
            <Pager page={page} totalPages={events.data?.totalPages ?? 1} onChange={setPage} />
          </>
        )}
      </CardContent>
    </Card>
  )
}
