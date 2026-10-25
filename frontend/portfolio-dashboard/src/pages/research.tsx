import { useMemo, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { toast } from 'sonner'
import {
  ArrowLeft, Bot, Check, CircleDashed, CircleDot, ClipboardList, Cpu, ExternalLink, GitCompareArrows, Loader2, MessageSquareText, Play,
  Save, Sigma, Sparkles, Trash2, Wrench, X,
} from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, PageHeader, Warnings } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { date, dateTime, pct, relativeTime } from '@/lib/format'
import {
  useApplyRefinement, useCompanyContext, useCompareHypotheses, useDeleteResearch, useDiscardRefinement, useEvaluateResearch, useProposeRefinement,
  useResearchSession, useResearchSessions, useSaveHypothesis, useSetUniverse, useStartResearch, useUpdateBrief,
} from '@/lib/queries'
import { briefRows, eventLabel, isEligible, marketNames, objectiveNames, originLabel, sectors } from '@/lib/research'
import type { Hypothesis, Level, RefinementChange, ResearchBrief, ResearchCandidate, ResearchSession, ReviewFlag, UpdateBriefRequest } from '@/lib/types'
import { cn } from '@/lib/utils'

const examples = [
  'I want a low-risk portfolio of US stocks without energy companies, at most 20% in any single stock.',
  'Build a diversified German portfolio for 10 years; I can tolerate medium fluctuations.',
  'Compare a maximum-Sharpe portfolio of UK stocks with an equal-weight baseline.',
]

const kindStyles: Record<ReviewFlag['kind'], string> = {
  Fact: 'border-border text-muted-foreground',
  Estimate: 'border-series-1/50 text-foreground',
  Interpretation: 'border-series-7/50 text-foreground',
  Caution: 'border-warn/50 text-warn',
}

const textareaClass =
  'min-h-24 w-full rounded-lg border border-input bg-transparent px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50'

// ================================================================ start page

export function ResearchHomePage() {
  const navigate = useNavigate()
  const sessions = useResearchSessions()
  const start = useStartResearch()
  const remove = useDeleteResearch()
  const [objective, setObjective] = useState('')
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  // Research can be about one of the user's portfolios (its currency is used; a saved run is attached to it) or independent.
  const { portfolios } = usePortfolioContext()
  const [params] = useSearchParams()
  const activePortfolios = portfolios.filter((p) => p.status === 'Active')
  const [context, setContext] = useState(() => params.get('portfolio') ?? 'none')
  const contextId = activePortfolios.some((p) => p.id === context) ? context : 'none'
  const contextOptions = [{ value: 'none', label: 'No portfolio (independent research)' }, ...activePortfolios.map((p) => ({ value: p.id, label: `${p.name} (${p.baseCurrency})` }))]

  const submit = () =>
    start.mutate({ objective, portfolioId: contextId === 'none' ? undefined : contextId }, {
      onSuccess: (s) => navigate(`/research/${s.id}`),
      onError: (e) => toast.error(errorMessage(e)),
    })

  return (
    <>
      <PageHeader
        title="AI research assistant"
        info="Describe what you want to research. The assistant turns it into a brief you can edit, researches a universe, runs the real optimizer and explains the results with the evidence."
        actions={<Button variant="outline" render={<Link to="/optimize" />}><Sigma /> Optimize manually</Button>}
      />

      <Card>
        <CardHeader><CardTitle>Your research objective</CardTitle></CardHeader>
        <CardContent className="space-y-3">
          <textarea
            aria-label="Your research objective"
            className={textareaClass}
            maxLength={2000}
            value={objective}
            onChange={(e) => setObjective(e.target.value)}
            placeholder="e.g. A low-risk portfolio of US stocks without energy companies, at most 20% per stock."
          />
          <div className="flex flex-wrap gap-2">
            {examples.map((ex) => (
              <Button key={ex} type="button" size="sm" variant="outline" className="h-auto py-1 text-left whitespace-normal" onClick={() => setObjective(ex)}>
                {ex}
              </Button>
            ))}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Label htmlFor="research-context" className="text-sm">Research context</Label>
            <Select value={contextId} onValueChange={(v) => v && setContext(String(v))} items={contextOptions}>
              <SelectTrigger id="research-context" className="w-72" aria-label="Research context"><SelectValue /></SelectTrigger>
              <SelectContent>{contextOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
            <InfoTip label="Research context">
              With a portfolio, the analysis uses its currency and a run you save is attached to it. Its holdings are not sent to a language model.
            </InfoTip>
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <Button onClick={submit} disabled={objective.trim().length < 3 || start.isPending}>
              {start.isPending ? <Loader2 className="animate-spin" /> : <Sparkles />} Start research
            </Button>
            <p className="text-xs text-muted-foreground">
              Runs on the server while you wait. Nothing is saved to your portfolios unless you confirm it. Educational research – not investment advice.
            </p>
          </div>
        </CardContent>
      </Card>

      <h2 className="mt-8 mb-3 text-sm font-semibold">Your research sessions</h2>
      {sessions.error ? <ErrorState error={sessions.error} onRetry={() => sessions.refetch()} /> : sessions.isLoading ? <LoadingBlock height="h-32" /> : !sessions.data?.length ? (
        <EmptyState title="No research yet" description="Start with an objective above. Sessions stay private to your account." icon={<Bot className="size-8" />} />
      ) : (
        <div className="overflow-x-auto rounded-xl border">
          <Table>
            <TableHeader>
              <TableRow><TableHead>Session</TableHead><TableHead>Stage</TableHead><TableHead className="text-right">Hypotheses</TableHead><TableHead>Updated</TableHead><TableHead /></TableRow>
            </TableHeader>
            <TableBody>
              {sessions.data.map((s) => (
                <TableRow key={s.id}>
                  <TableCell className="max-w-md"><Link className="font-medium hover:underline" to={`/research/${s.id}`}>{s.title}</Link></TableCell>
                  <TableCell>{stateLabel[s.state] ?? s.state}{s.hasSavedRun && <Badge variant="outline" className="ml-2">saved run</Badge>}</TableCell>
                  <TableCell className="text-right tabular">{s.hypotheses}</TableCell>
                  <TableCell className="text-muted-foreground" title={dateTime(s.updatedAtUtc)}>{relativeTime(s.updatedAtUtc)}</TableCell>
                  <TableCell className="text-right">
                    {confirmDelete === s.id ? (
                      <span className="inline-flex gap-1">
                        <Button size="sm" variant="destructive" onClick={() => remove.mutate(s.id, { onSuccess: () => setConfirmDelete(null) })}>Delete session</Button>
                        <Button size="sm" variant="ghost" onClick={() => setConfirmDelete(null)}>Keep</Button>
                      </span>
                    ) : (
                      <Button size="icon-sm" variant="ghost" aria-label={`Delete ${s.title}`} onClick={() => setConfirmDelete(s.id)}><Trash2 /></Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      <p className="mt-3 text-xs text-muted-foreground">Deleting a session removes its brief, hypotheses and activity log. Optimization runs you saved from it are kept.</p>
    </>
  )
}

const stateLabel: Record<string, string> = {
  AwaitingInput: 'Needs your input',
  BriefReady: 'Brief ready',
  UniverseReady: 'Universe ready',
  Evaluated: 'Results ready',
}

// ================================================================ workspace

export function ResearchWorkspacePage() {
  const { sessionId } = useParams()
  const q = useResearchSession(sessionId)
  if (q.error) return <ErrorState error={q.error} onRetry={() => q.refetch()} />
  if (!q.data) return <LoadingBlock />
  return <Workspace session={q.data} />
}

function Workspace({ session: s }: { session: ResearchSession }) {
  const [companySymbol, setCompanySymbol] = useState<string>()
  return (
    <div className="space-y-6">
      <Button variant="ghost" size="sm" className="-ml-2" render={<Link to="/research" />}><ArrowLeft /> All research</Button>
      <PageHeader
        title={s.title}
        description={<>
          Started {dateTime(s.createdAtUtc)} · {stateLabel[s.state] ?? s.state}
          {s.portfolioId ? <> · For portfolio <Link className="underline underline-offset-4" to={`/portfolios/${s.portfolioId}`}>{s.portfolioName}</Link> ({s.portfolioCurrency})</> : ' · Independent research'}
        </>}
        actions={<AssistantBadge session={s} />}
      />
      <Steps session={s} />
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="min-w-0 space-y-6">
          <BriefCard session={s} />
          {s.candidates && <UniverseCard key={s.candidates.selectedSymbols.join(',') + s.candidates.retrievedAtUtc} session={s} onCompany={setCompanySymbol} />}
          {s.hypotheses.length > 0 && <Hypotheses session={s} onCompany={setCompanySymbol} />}
          {s.hypotheses.length > 0 && <RefineCard session={s} />}
          {s.hypotheses.length > 1 && <CompareCard session={s} />}
          <p className="text-xs text-muted-foreground">{s.disclaimer}</p>
        </div>
        <aside className="min-w-0 space-y-6">
          <BudgetCard session={s} />
          <ActivityLog session={s} />
        </aside>
      </div>
      {companySymbol && <CompanyDialog sessionId={s.id} symbol={companySymbol} onClose={() => setCompanySymbol(undefined)} />}
    </div>
  )
}

function AssistantBadge({ session: s }: { session: ResearchSession }) {
  const a = s.assistant
  return (
    <Badge variant="outline" className="gap-1" title={a.note}>
      {a.available ? <Bot /> : <Cpu />}
      {a.available ? `Language model: ${a.model ?? a.provider}` : 'Deterministic rules (no language model)'}
    </Badge>
  )
}

function Steps({ session: s }: { session: ResearchSession }) {
  return (
    <ol aria-label="Research steps" className="flex flex-wrap gap-2">
      {s.steps.map((st, i) => (
        <li key={st.key} aria-current={st.status === 'current' ? 'step' : undefined}
          className={cn('flex items-center gap-1.5 rounded-full border px-3 py-1 text-xs',
            st.status === 'done' && 'border-gain/40 text-foreground',
            st.status === 'current' && 'border-primary bg-primary/10 font-medium',
            st.status === 'waiting' && 'border-warn/50 text-warn',
            st.status === 'pending' && 'text-muted-foreground')}>
          {st.status === 'done' ? <Check className="size-3" /> : st.status === 'current' ? <CircleDot className="size-3" /> : <CircleDashed className="size-3" />}
          <span>{i + 1}. {st.title}</span>
          <span className="sr-only">({st.status})</span>
        </li>
      ))}
    </ol>
  )
}

// ---------------------------------------------------------------- brief

function OriginBadge({ origin, reason }: { origin: 'user' | 'assumption' | null; reason: string | null }) {
  if (!origin) return null
  return (
    <Badge variant="outline" title={reason ?? undefined} className={origin === 'user' ? 'border-gain/40' : 'border-warn/50 text-warn'}>
      {origin === 'user' ? 'You said' : 'Assumed'}
    </Badge>
  )
}

function BriefCard({ session: s }: { session: ResearchSession }) {
  const [editing, setEditing] = useState(s.state === 'AwaitingInput')
  const b = s.brief
  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between gap-2">
        <CardTitle className="flex items-center gap-2"><ClipboardList className="size-4" /> Research brief</CardTitle>
        <Button size="sm" variant="outline" onClick={() => setEditing((e) => !e)}>{editing ? 'Close editor' : 'Edit brief'}</Button>
      </CardHeader>
      <CardContent className="space-y-4">
        <blockquote className="border-l-2 pl-3 text-sm text-muted-foreground">“{b.question}”</blockquote>
        {b.missingInformation.length > 0 && (
          <Alert className={s.state === 'AwaitingInput' ? 'border-warn/50' : ''}>
            <MessageSquareText />
            <AlertTitle>{s.state === 'AwaitingInput' ? 'One answer needed before research can start' : 'Not stated – you may want to add'}</AlertTitle>
            <AlertDescription><ul className="list-disc space-y-0.5 pl-4">{b.missingInformation.map((m) => <li key={m}>{m}</li>)}</ul></AlertDescription>
          </Alert>
        )}
        {editing ? <BriefEditor session={s} onDone={() => setEditing(false)} /> : (
          <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
            {briefRows(b).map((r) => (
              <div key={r.key} className="flex items-start justify-between gap-2 border-b py-1.5">
                <dt className="text-muted-foreground">{r.label}</dt>
                <dd className="flex items-center gap-2 text-right font-medium">
                  {r.value}
                  <OriginBadge origin={r.origin} reason={r.reason} />
                </dd>
              </div>
            ))}
          </dl>
        )}
        {!editing && (
          <details className="text-xs text-muted-foreground">
            <summary className="cursor-pointer">Why these assumptions and data requirements</summary>
            <ul className="mt-2 list-disc space-y-0.5 pl-4">
              {briefRows(b).filter((r) => r.reason).map((r) => <li key={r.key}><span className="font-medium text-foreground">{r.label}:</span> {r.reason}</li>)}
              {b.dataRequirements.map((d) => <li key={d}>{d}</li>)}
              <li>Output: {b.intendedOutput}</li>
            </ul>
          </details>
        )}
      </CardContent>
    </Card>
  )
}

function BriefEditor({ session: s, onDone }: { session: ResearchSession; onDone: () => void }) {
  const update = useUpdateBrief(s.id)
  const b = s.brief
  const [form, setForm] = useState<UpdateBriefRequest>({})
  const v = <K extends keyof ResearchBrief>(k: K) => (form[k as keyof UpdateBriefRequest] ?? b[k]) as ResearchBrief[K]
  const set = (patch: UpdateBriefRequest) => setForm((f) => ({ ...f, ...patch }))
  const excluded = (form.excludeSectors ?? b.excludeSectors)

  const levels: Level[] = ['low', 'medium', 'high']
  const save = () =>
    update.mutate(form, {
      onSuccess: () => { setForm({}); onDone() },
      onError: (e) => toast.error(errorMessage(e)),
    })

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Field label="Market">
          <Select value={v('market') ?? ''} onValueChange={(m) => set({ market: m as string })} items={Object.entries(marketNames).map(([value, label]) => ({ value, label }))}>
            <SelectTrigger className="w-full" aria-label="Market"><SelectValue placeholder="Choose a market" /></SelectTrigger>
            <SelectContent>{Object.entries(marketNames).map(([k, l]) => <SelectItem key={k} value={k}>{l}</SelectItem>)}</SelectContent>
          </Select>
        </Field>
        <Field label="Willingness to accept fluctuations">
          <Select value={v('riskPreference')} onValueChange={(r) => set({ riskPreference: r as Level })} items={levels.map((l) => ({ value: l, label: l }))}>
            <SelectTrigger className="w-full" aria-label="Risk preference"><SelectValue /></SelectTrigger>
            <SelectContent>{levels.map((l) => <SelectItem key={l} value={l}>{l}</SelectItem>)}</SelectContent>
          </Select>
        </Field>
        <Field label="Ability to bear losses">
          <Select value={v('lossCapacity') ?? ''} onValueChange={(r) => set({ lossCapacity: r as Level })} items={levels.map((l) => ({ value: l, label: l }))}>
            <SelectTrigger className="w-full" aria-label="Loss capacity"><SelectValue placeholder="not stated" /></SelectTrigger>
            <SelectContent>{levels.map((l) => <SelectItem key={l} value={l}>{l}</SelectItem>)}</SelectContent>
          </Select>
        </Field>
        <Field label="Method">
          <Select value={v('objective')} onValueChange={(o) => set({ objective: o as ResearchBrief['objective'] })} items={Object.entries(objectiveNames).map(([value, label]) => ({ value, label }))}>
            <SelectTrigger className="w-full" aria-label="Method"><SelectValue /></SelectTrigger>
            <SelectContent>{Object.entries(objectiveNames).map(([k, l]) => <SelectItem key={k} value={k}>{l}</SelectItem>)}</SelectContent>
          </Select>
        </Field>
        <Field label="Per-stock maximum (%)">
          <Input type="number" min={5} max={100} aria-label="Per-stock maximum in percent" value={Math.round(v('maxWeight') * 100)}
            onChange={(e) => set({ maxWeight: Number(e.target.value) / 100 })} />
        </Field>
        <Field label="Horizon (years)">
          <Input type="number" min={1} max={50} aria-label="Horizon in years" value={v('horizonYears') ?? ''}
            onChange={(e) => set({ horizonYears: e.target.value ? Number(e.target.value) : undefined })} />
        </Field>
        <Field label="Shortlist size">
          <Input type="number" min={4} max={25} aria-label="Shortlist size" value={v('candidateCount')} onChange={(e) => set({ candidateCount: Number(e.target.value) })} />
        </Field>
      </div>
      <fieldset>
        <legend className="mb-2 text-sm font-medium">Exclude sectors</legend>
        <div className="grid gap-1.5 sm:grid-cols-2 lg:grid-cols-3">
          {sectors.map((sec) => (
            <label key={sec} className="flex items-center gap-2 text-sm">
              <Checkbox checked={excluded.includes(sec)}
                onCheckedChange={(c) => set({ excludeSectors: c ? [...excluded, sec] : excluded.filter((x) => x !== sec) })} />
              {sec}
            </label>
          ))}
        </div>
      </fieldset>
      <div className="flex gap-2">
        <Button onClick={save} disabled={Object.keys(form).length === 0 || update.isPending}>
          {update.isPending ? <Loader2 className="animate-spin" /> : <Check />} Save brief and research universe
        </Button>
        <Button variant="ghost" onClick={() => { setForm({}); onDone() }}>Cancel</Button>
      </div>
      <p className="text-xs text-muted-foreground">Changing the brief researches the universe again; existing hypotheses stay for comparison.</p>
    </div>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <div className="space-y-1.5"><Label>{label}</Label>{children}</div>
}

// ---------------------------------------------------------------- universe

function UniverseCard({ session: s, onCompany }: { session: ResearchSession; onCompany: (symbol: string) => void }) {
  const c = s.candidates!
  const setUniverse = useSetUniverse(s.id)
  const evaluate = useEvaluateResearch(s.id)
  const [picked, setPicked] = useState<string[]>(c.selectedSymbols)
  const changed = picked.length !== c.selectedSymbols.length || picked.some((x) => !c.selectedSymbols.includes(x))
  const [showAll, setShowAll] = useState(false)
  const rows = showAll ? c.candidates : c.candidates.filter(isEligible)
  const excluded = exclusionCounts(c.candidates)

  return (
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-2">
        <CardTitle>Universe research · {marketNames[c.market] ?? c.market}</CardTitle>
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          {c.candidates.some((x) => x.isSimulated) && <DataBadge kind="simulated" />}
          <span>Retrieved {dateTime(c.retrievedAtUtc)}</span>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        <p className="text-sm"><span className="font-medium">How the shortlist was chosen:</span> {c.criterion}</p>
        <p className="text-xs text-muted-foreground">This is a transparent screen on stored price history, not a prediction or a recommendation. Figures are the last year of stored daily closes.</p>
        <Warnings items={[...c.dataWarnings]} />
        <p className="text-sm text-muted-foreground" aria-label="Screening summary">
          {c.candidates.length} index members screened · {c.candidates.filter(isEligible).length} meet the data requirements · {c.selectedSymbols.length} shortlisted
          {excluded.map(([label, n]) => ` · ${n} ${label}`).join('')}
        </p>
        <div className="overflow-x-auto rounded-xl border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-10"><span className="sr-only">Include</span></TableHead>
                <TableHead>Stock</TableHead><TableHead>Sector</TableHead>
                <TableHead className="text-right">Volatility 1Y</TableHead><TableHead className="text-right">Return 1Y</TableHead><TableHead className="text-right">Max drawdown 1Y</TableHead>
                <TableHead className="text-right">Prices</TableHead><TableHead>Evidence</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((x) => (
                <TableRow key={x.symbol} className={cn(!isEligible(x) && 'text-muted-foreground')}>
                  <TableCell>
                    <Checkbox aria-label={`Include ${x.symbol}`} disabled={!isEligible(x)} checked={picked.includes(x.symbol)}
                      onCheckedChange={(on) => setPicked((p) => on ? [...p, x.symbol] : p.filter((y) => y !== x.symbol))} />
                  </TableCell>
                  <TableCell>
                    <button type="button" className="font-medium hover:underline" onClick={() => onCompany(x.symbol)} aria-label={`Company context for ${x.symbol}`}>{x.symbol}</button>
                    <div className="max-w-48 truncate text-xs text-muted-foreground">{x.name}</div>
                  </TableCell>
                  <TableCell className="text-xs">{x.sector ?? '—'}</TableCell>
                  <TableCell className="text-right tabular">{pct(x.volatility1Y, 1)}</TableCell>
                  <TableCell className="text-right tabular">{pct(x.return1Y, 1, true)}</TableCell>
                  <TableCell className="text-right tabular">{pct(x.maxDrawdown1Y, 1)}</TableCell>
                  <TableCell className="text-right tabular">{x.observations}{x.stalePrice && <Badge variant="outline" className="ml-1 border-warn/50 text-warn">stale</Badge>}</TableCell>
                  <TableCell className="min-w-56 text-xs">{x.reason}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="ghost" size="sm" onClick={() => setShowAll((a) => !a)}>
            {showAll ? 'Show eligible stocks only' : `Show all ${c.candidates.length} screened stocks with reasons`}
          </Button>
          <span className="text-xs text-muted-foreground">{picked.length} selected</span>
          <div className="ml-auto flex gap-2">
            {changed && (
              <Button variant="outline" disabled={picked.length < 2 || setUniverse.isPending}
                onClick={() => setUniverse.mutate(picked, { onError: (e) => toast.error(errorMessage(e)) })}>
                Use these {picked.length} stocks
              </Button>
            )}
            <Button disabled={changed || evaluate.isPending}
              onClick={() => evaluate.mutate(undefined, { onError: (e) => toast.error(errorMessage(e)) })}>
              {evaluate.isPending ? <Loader2 className="animate-spin" /> : <Play />} {s.hypotheses.length ? 'Run analysis again' : 'Run analysis'}
            </Button>
          </div>
        </div>
        {evaluate.isPending && <p className="text-xs text-muted-foreground" role="status">Optimizing and reviewing on the server – this usually takes a few seconds.</p>}
      </CardContent>
    </Card>
  )
}

function exclusionCounts(cs: ResearchCandidate[]): [string, number][] {
  const n = (f: (x: ResearchCandidate) => boolean) => cs.filter((x) => !isEligible(x) && f(x)).length
  const rows: [string, number][] = [
    ['excluded by you', n((x) => x.reason.includes('excluded by you') || x.reason === 'Excluded by you.')],
    ['without enough stored history', n((x) => x.reason.startsWith('Only '))],
    ['with a stale price', n((x) => x.reason.includes('stale'))],
  ]
  return rows.filter(([, k]) => k > 0)
}

// ---------------------------------------------------------------- hypotheses

function Hypotheses({ session: s, onCompany }: { session: ResearchSession; onCompany: (symbol: string) => void }) {
  // A newly added hypothesis becomes the visible one; otherwise the user's choice stays.
  const last = s.hypotheses[s.hypotheses.length - 1].id
  const [choice, setChoice] = useState<{ id: string; whenLast: string }>()
  const active = choice && choice.whenLast === last ? choice.id : last
  const setActive = (id: string) => setChoice({ id, whenLast: last })
  const h = s.hypotheses.find((x) => x.id === active) ?? s.hypotheses[0]
  return (
    <section aria-labelledby="hypotheses-title" className="space-y-3">
      <h2 id="hypotheses-title" className="text-sm font-semibold">Portfolio hypotheses</h2>
      <div role="tablist" aria-label="Hypotheses" className="flex flex-wrap gap-2">
        {s.hypotheses.map((x) => (
          <Button key={x.id} role="tab" aria-selected={x.id === h.id} size="sm" variant={x.id === h.id ? 'default' : 'outline'} onClick={() => setActive(x.id)}>
            {x.label}{x.savedRunId && <Save className="size-3" />}
          </Button>
        ))}
      </div>
      <HypothesisCard key={h.id} session={s} h={h} onCompany={onCompany} />
    </section>
  )
}

function HypothesisCard({ session: s, h, onCompany }: { session: ResearchSession; h: Hypothesis; onCompany: (symbol: string) => void }) {
  const r = h.result
  const ok = r.status === 'Succeeded'
  const propose = useProposeRefinement(s.id)
  const weights = [...r.allocation].sort((a, b) => b.weight - a.weight)
  return (
    <Card role="tabpanel" aria-label={h.label}>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
        <div>
          <CardTitle>{h.label}</CardTitle>
          <p className="mt-1 text-xs text-muted-foreground">{originLabel(h)} · {objectiveNames[h.config.objective]} · max {pct(h.config.maxWeight, 0)} per stock
            {Object.entries(h.config.sectorLimits).map(([k, v]) => ` · ${k} ≤ ${pct(v, 0)}`).join('')} · {h.config.covarianceMethod === 'LedoitWolf' ? 'Ledoit–Wolf' : 'sample'} covariance · {h.config.lookbackDays} days
            {h.config.excludedSymbols.length > 0 && ` · without ${h.config.excludedSymbols.join(', ')}`}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {r.usesSimulatedData && <DataBadge kind="simulated" />}
          <DataBadge kind="estimate" label="In-sample estimate" />
          <SaveButton session={s} h={h} />
        </div>
      </CardHeader>
      <CardContent className="space-y-5">
        {!ok ? (
          <Alert variant="destructive"><AlertTitle>The optimizer found no portfolio ({r.status})</AlertTitle><AlertDescription>{r.message}</AlertDescription></Alert>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Stat label="Expected return (est.)" value={pct(r.expectedReturn, 1)} hint="Annualized mean of past daily returns over the window – not a forecast." />
              <Stat label="Volatility (est.)" value={pct(r.volatility, 1)} hint="Annualized standard deviation implied by the estimated covariance." />
              <Stat label="Sharpe ratio (est.)" value={r.sharpeRatio?.toFixed(2) ?? '—'} hint="(Expected return − risk-free rate) / volatility, in-sample." />
              <Stat label="Window" value={`${date(r.periodStart)} – ${date(r.periodEnd)}`} hint={`${r.observations} daily observations`} />
            </div>
            <div>
              <h3 className="mb-2 text-xs font-medium text-muted-foreground uppercase">Weights</h3>
              <ul className="space-y-1">
                {weights.map((a) => (
                  <li key={a.symbol} className="grid grid-cols-[4.5rem_1fr_3.5rem] items-center gap-2 text-sm">
                    <button type="button" className="text-left font-medium hover:underline" onClick={() => onCompany(a.symbol)}>{a.symbol}</button>
                    <div className="h-2 rounded bg-muted" aria-hidden><div className="h-2 rounded bg-series-1" style={{ width: `${Math.max(0.5, a.weight * 100)}%` }} /></div>
                    <span className="text-right tabular">{pct(a.weight, 1)}</span>
                  </li>
                ))}
              </ul>
            </div>
          </>
        )}

        {h.flags.length > 0 && (
          <div>
            <h3 className="mb-2 text-xs font-medium text-muted-foreground uppercase">Critical review</h3>
            <ul className="space-y-1.5">
              {h.flags.map((f, i) => (
                <li key={i} className="flex gap-2 text-sm">
                  <Badge variant="outline" className={cn('shrink-0', kindStyles[f.kind])}>{f.kind}</Badge>
                  <span><span className="font-medium">{f.topic}:</span> {f.text}{f.basis && <span className="text-xs text-muted-foreground"> ({f.basis})</span>}</span>
                </li>
              ))}
            </ul>
          </div>
        )}

        <InterpretationView h={h} />

        {h.suggestions.length > 0 && (
          <div>
            <h3 className="mb-2 text-xs font-medium text-muted-foreground uppercase">Investigate next</h3>
            <div className="flex flex-wrap gap-2">
              {h.suggestions.map((sg) => (
                <Button key={sg.text} size="sm" variant="outline" className="h-auto py-1 text-left whitespace-normal" disabled={propose.isPending}
                  onClick={() => propose.mutate({ hypothesisId: h.id, changes: sg.changes as RefinementChange[] }, { onError: (e) => toast.error(errorMessage(e)) })}>
                  <Wrench /> {sg.text}
                </Button>
              ))}
            </div>
            <p className="mt-1 text-xs text-muted-foreground">A suggestion is only proposed – you see the exact change and confirm it before anything is recalculated.</p>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function Stat({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="rounded-lg border p-3">
      <div className="flex items-center gap-1 text-xs text-muted-foreground">{label}<InfoTip>{hint}</InfoTip></div>
      <div className="mt-1 font-semibold tabular">{value}</div>
    </div>
  )
}

function InterpretationView({ h }: { h: Hypothesis }) {
  const i = h.interpretation
  return (
    <div className="rounded-xl border bg-muted/30 p-4">
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold">Interpretation</h3>
        <Badge variant="outline" title={i.note ?? undefined}>
          {i.source === 'model' ? <><Bot /> Written by {i.model ?? 'the language model'} · numbers verified</> : <><Cpu /> Generated from the computed values</>}
        </Badge>
      </div>
      {i.note && <p className="mb-3 text-xs text-muted-foreground">{i.note}</p>}
      <div className="space-y-3">
        {i.sections.map((sec) => (
          <div key={sec.key}>
            <h4 className="text-sm font-medium">{sec.title}</h4>
            {sec.paragraphs.map((p, k) => <p key={k} className="mt-1 text-sm text-muted-foreground">{p}</p>)}
          </div>
        ))}
      </div>
    </div>
  )
}

function SaveButton({ session: s, h }: { session: ResearchSession; h: Hypothesis }) {
  const save = useSaveHypothesis(s.id)
  const [open, setOpen] = useState(false)
  if (h.savedRunId)
    return <Button size="sm" variant="outline" render={<Link to={`/optimize/${h.savedRunId}`} />}><ExternalLink /> Open saved run</Button>
  if (h.result.status !== 'Succeeded') return null
  return (
    <>
      <Button size="sm" onClick={() => setOpen(true)}><Save /> Save as optimization run</Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Save “{h.label}”?</DialogTitle>
            <DialogDescription>
              This stores the hypothesis as an optimization run in your history, attached to your first portfolio in the same currency. It is recalculated
              with exactly these inputs; no orders are placed and nothing else changes. You can delete the run later from the history.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
            <Button disabled={save.isPending}
              onClick={() => save.mutate({ hypothesisId: h.id, confirm: true }, {
                onSuccess: () => { setOpen(false); toast.success('Saved as an optimization run') },
                onError: (e) => toast.error(errorMessage(e)),
              })}>
              {save.isPending ? <Loader2 className="animate-spin" /> : <Save />} Confirm and save
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )
}

// ---------------------------------------------------------------- refinement

function RefineCard({ session: s }: { session: ResearchSession }) {
  const propose = useProposeRefinement(s.id)
  const apply = useApplyRefinement(s.id)
  const discard = useDiscardRefinement(s.id)
  const [text, setText] = useState('')
  const [base, setBase] = useState<string>(s.hypotheses[0].id)
  const p = s.pendingRefinement
  const onError = (e: unknown) => toast.error(errorMessage(e))
  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-2"><MessageSquareText className="size-4" /> Ask or refine</CardTitle></CardHeader>
      <CardContent className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-[1fr_240px]">
          <textarea aria-label="Your question or change" className={textareaClass} maxLength={500} value={text} onChange={(e) => setText(e.target.value)}
            placeholder="e.g. “Why does MSFT have such a large weight?”, “cap each stock at 15%”, “exclude JPM”, “show a simpler alternative”" />
          <Field label="Based on">
            <Select value={base} onValueChange={(v) => setBase(v as string)} items={s.hypotheses.map((h) => ({ value: h.id, label: h.label }))}>
              <SelectTrigger className="w-full" aria-label="Base hypothesis"><SelectValue /></SelectTrigger>
              <SelectContent>{s.hypotheses.map((h) => <SelectItem key={h.id} value={h.id}>{h.label}</SelectItem>)}</SelectContent>
            </Select>
          </Field>
        </div>
        <Button disabled={text.trim().length < 2 || propose.isPending}
          onClick={() => propose.mutate({ text, hypothesisId: base }, { onSuccess: () => setText(''), onError })}>
          {propose.isPending ? <Loader2 className="animate-spin" /> : <Sparkles />} Interpret
        </Button>

        {p && (
          <div className="rounded-xl border p-4" role="region" aria-label="Proposed change">
            <p className="text-xs text-muted-foreground">You asked: “{p.request}” · interpreted by {p.interpretedBy === 'model' ? 'the language model (validated)' : p.interpretedBy === 'suggestion' ? 'a suggestion' : 'rules'}</p>
            {p.answer && <p className="mt-2 text-sm">{p.answer}</p>}
            {p.changes.length > 0 && (
              <>
                <p className="mt-2 text-sm font-medium">Proposed change to “{s.hypotheses.find((h) => h.id === p.baseHypothesisId)?.label}” – nothing has been recalculated yet:</p>
                <ul className="mt-1 list-disc pl-5 text-sm">{p.changes.map((c, i) => <li key={i}>{c.description}</li>)}</ul>
              </>
            )}
            {p.rejected.length > 0 && <ul className="mt-2 list-disc pl-5 text-xs text-muted-foreground">{p.rejected.map((r) => <li key={r}>Not applied: {r}</li>)}</ul>}
            <div className="mt-3 flex gap-2">
              {p.changes.length > 0 && (
                <Button disabled={apply.isPending} onClick={() => apply.mutate(p.id, { onError })}>
                  {apply.isPending ? <Loader2 className="animate-spin" /> : <Check />} Apply and recalculate
                </Button>
              )}
              <Button variant="ghost" disabled={discard.isPending} onClick={() => discard.mutate(undefined, { onError })}>
                <X /> {p.changes.length > 0 ? 'Discard' : 'Dismiss'}
              </Button>
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ---------------------------------------------------------------- comparison

function CompareCard({ session: s }: { session: ResearchSession }) {
  const [a, setA] = useState(s.hypotheses[0].id)
  const [b, setB] = useState(s.hypotheses[s.hypotheses.length - 1].id)
  const cmp = useCompareHypotheses(s.id, a, b)
  const items = s.hypotheses.map((h) => ({ value: h.id, label: h.label }))
  const c = cmp.data
  const metrics = useMemo(() => c ? [
    { label: 'Expected return (est.)', a: c.a.result.expectedReturn, b: c.b.result.expectedReturn },
    { label: 'Volatility (est.)', a: c.a.result.volatility, b: c.b.result.volatility },
    { label: 'Largest weight', a: c.a.review?.concentration.largestWeight ?? null, b: c.b.review?.concentration.largestWeight ?? null },
    { label: 'Effective holdings', a: c.a.review?.concentration.effectiveHoldings ?? null, b: c.b.review?.concentration.effectiveHoldings ?? null, plain: true },
  ] : [], [c])
  return (
    <Card>
      <CardHeader><CardTitle className="flex items-center gap-2"><GitCompareArrows className="size-4" /> Compare alternatives</CardTitle></CardHeader>
      <CardContent className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-2">
          {[['A', a, setA], ['B', b, setB]].map(([label, value, set]) => (
            <Field key={label as string} label={`Hypothesis ${label}`}>
              <Select value={value as string} onValueChange={(v) => (set as (x: string) => void)(v as string)} items={items}>
                <SelectTrigger className="w-full" aria-label={`Hypothesis ${label}`}><SelectValue /></SelectTrigger>
                <SelectContent>{items.map((i) => <SelectItem key={i.value} value={i.value}>{i.label}</SelectItem>)}</SelectContent>
              </Select>
            </Field>
          ))}
        </div>
        {a === b ? <p className="text-sm text-muted-foreground">Choose two different hypotheses.</p> : cmp.error ? <ErrorState error={cmp.error} /> : !c ? <LoadingBlock height="h-32" /> : (
          <>
            <div className="overflow-x-auto rounded-xl border">
              <Table>
                <TableHeader><TableRow><TableHead /><TableHead className="text-right">A</TableHead><TableHead className="text-right">B</TableHead></TableRow></TableHeader>
                <TableBody>
                  {metrics.map((m) => (
                    <TableRow key={m.label}><TableCell>{m.label}</TableCell>
                      <TableCell className="text-right tabular">{m.plain ? m.a?.toFixed(1) ?? '—' : pct(m.a, 1)}</TableCell>
                      <TableCell className="text-right tabular">{m.plain ? m.b?.toFixed(1) ?? '—' : pct(m.b, 1)}</TableCell></TableRow>
                  ))}
                  {c.configChanges.map((x) => <TableRow key={x.field}><TableCell>{x.field}</TableCell><TableCell className="text-right">{x.a}</TableCell><TableCell className="text-right">{x.b}</TableCell></TableRow>)}
                </TableBody>
              </Table>
            </div>
            <div className="overflow-x-auto rounded-xl border">
              <Table>
                <TableHeader><TableRow><TableHead>Stock</TableHead><TableHead className="text-right">Weight A</TableHead><TableHead className="text-right">Weight B</TableHead><TableHead className="text-right">Change</TableHead></TableRow></TableHeader>
                <TableBody>
                  {c.weights.map((w) => (
                    <TableRow key={w.symbol}><TableCell className="font-medium">{w.symbol}</TableCell><TableCell className="text-right tabular">{pct(w.weightA, 1)}</TableCell>
                      <TableCell className="text-right tabular">{pct(w.weightB, 1)}</TableCell><TableCell className="text-right tabular">{pct(w.weightB - w.weightA, 1, true)}</TableCell></TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            <ul className="list-disc space-y-0.5 pl-5 text-xs text-muted-foreground">{c.notes.map((n) => <li key={n}>{n}</li>)}</ul>
          </>
        )}
      </CardContent>
    </Card>
  )
}

// ---------------------------------------------------------------- side panel

function BudgetCard({ session: s }: { session: ResearchSession }) {
  const b = s.budget
  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Session budget</CardTitle></CardHeader>
      <CardContent className="space-y-1 text-sm">
        <Row label="Tool calls" value={`${b.toolCalls} / ${b.maxToolCalls}`} />
        <Row label="Hypotheses" value={`${b.hypotheses} / ${b.maxHypotheses}`} />
        <Row label="Language-model tokens" value={`${b.llmTokens.toLocaleString()} / ${b.maxLlmTokens.toLocaleString()}`} />
        <p className="pt-2 text-xs text-muted-foreground">{s.assistant.note}</p>
      </CardContent>
    </Card>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return <div className="flex justify-between gap-2"><span className="text-muted-foreground">{label}</span><span className="tabular">{value}</span></div>
}

const statusStyle: Record<string, string> = {
  ok: 'border-gain/40', error: 'border-loss/40 text-loss', rejected: 'border-warn/50 text-warn', skipped: 'text-muted-foreground', denied: 'border-loss/40 text-loss',
}

function ActivityLog({ session: s }: { session: ResearchSession }) {
  const events = [...s.events].reverse()
  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Activity</CardTitle></CardHeader>
      <CardContent>
        <p className="mb-3 text-xs text-muted-foreground">Every tool call, model call and decision, newest first. Tools enforce their own permissions; the assistant cannot change data without your confirmation.</p>
        <ol aria-label="Activity log" className="max-h-[36rem] space-y-2 overflow-y-auto pr-1">
          {events.map((e, i) => (
            <li key={i} className="rounded-lg border p-2 text-xs">
              <div className="flex items-center justify-between gap-2">
                <span className="flex items-center gap-1 font-medium">
                  {e.kind === 'tool' ? <Wrench className="size-3" /> : e.kind === 'model' ? <Bot className="size-3" /> : e.kind === 'user' ? <MessageSquareText className="size-3" /> : <Cpu className="size-3" />}
                  {eventLabel(e)}
                </span>
                <Badge variant="outline" className={statusStyle[e.status]}>{e.status}</Badge>
              </div>
              <p className="mt-1 break-words text-muted-foreground">{e.summary}</p>
              <p className="mt-0.5 text-[11px] text-muted-foreground" title={dateTime(e.atUtc)}>{relativeTime(e.atUtc)}{e.durationMs > 0 && ` · ${Math.round(e.durationMs)} ms`}</p>
            </li>
          ))}
        </ol>
      </CardContent>
    </Card>
  )
}

function CompanyDialog({ sessionId, symbol, onClose }: { sessionId: string; symbol: string; onClose: () => void }) {
  const q = useCompanyContext(sessionId, symbol)
  const c = q.data
  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose() }}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Company context · {symbol}</DialogTitle>
          <DialogDescription>Background from external sources. It is shown as information, never used as an instruction or as a reason for a weight.</DialogDescription>
        </DialogHeader>
        {q.error ? <ErrorState error={q.error} onRetry={() => q.refetch()} /> : !c ? <LoadingBlock height="h-40" /> : (
          <div className="space-y-4 text-sm">
            {c.profile.description ? (
              <div>
                <p>{c.profile.description}</p>
                <p className="mt-1 text-xs text-muted-foreground">
                  Source: {c.profile.sourceUrl ? <a className="underline" href={c.profile.sourceUrl} target="_blank" rel="noreferrer">{c.profile.sourceName}</a> : c.profile.sourceName}
                  {c.profile.licence && ` · ${c.profile.licence}`}{c.profile.retrievedAtUtc && ` · retrieved ${dateTime(c.profile.retrievedAtUtc)}`}{c.profile.isStale && ' · stale'}
                </p>
              </div>
            ) : <p className="text-muted-foreground">{c.profile.message ?? 'No company description available.'}</p>}
            <div>
              <h3 className="mb-1 font-medium">Recent news</h3>
              {c.recentNews.length === 0 ? <p className="text-muted-foreground">No recent articles stored.</p> : (
                <ul className="space-y-2">
                  {c.recentNews.map((n) => (
                    <li key={n.id}>
                      <a className="font-medium hover:underline" href={n.url} target="_blank" rel="noreferrer">{n.headline}</a>
                      <div className="text-xs text-muted-foreground">{n.publisher} · {dateTime(n.publishedAtUtc)}{n.isSimulated && ' · SIMULATED'}</div>
                    </li>
                  ))}
                </ul>
              )}
            </div>
            <p className="text-xs text-muted-foreground">{c.note}</p>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
