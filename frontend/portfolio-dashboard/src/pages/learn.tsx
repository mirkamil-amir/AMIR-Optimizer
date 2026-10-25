import { useDeferredValue, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { EmptyState, PageHeader } from '@/components/common'
import { glossary, glossaryCategories } from '@/lib/glossary'
import { guideById, strategyGuides, type StrategyGuide } from '@/lib/strategy-guide'

const families: { id: StrategyGuide['family']; title: string; description: string }[] = [
  { id: 'optimization', title: 'Mathematical optimization', description: 'Weights from estimated returns, volatilities and correlations (mean-variance theory).' },
  { id: 'machine-learning', title: 'Machine-learning models', description: 'Models that learn from past features and outcomes to rank stocks.' },
  { id: 'evaluation', title: 'From signals to portfolios, and fair testing', description: 'How predictions become weights and how strategies are compared honestly.' },
]

export function LearnIndexPage() {
  return (
    <>
      <PageHeader
        title="Strategy guide"
        info="How every strategy in this app works: the idea, the theory, the mathematics, a worked example and how to read your own results. Start with equal weight and minimum variance, then compare them with the return-seeking and ML strategies."
        actions={<Button size="sm" variant="outline" render={<Link to="/learn/glossary" />}>Glossary</Button>}
      />
      <div className="space-y-8">
        {families.map((f) => (
          <section key={f.id} aria-labelledby={`family-${f.id}`}>
            <h2 id={`family-${f.id}`} className="mb-1 flex items-center gap-2 text-base font-semibold">{f.title}</h2>
            <p className="mb-3 text-sm text-muted-foreground">{f.description}</p>
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
              {strategyGuides.filter((g) => g.family === f.id).map((g) => (
                <Link key={g.id} to={`/learn/${g.id}`} className="group rounded-xl focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none">
                  <Card className="h-full transition-colors group-hover:border-foreground/30">
                    <CardHeader>
                      <CardTitle className="text-sm">{g.name}</CardTitle>
                      <CardDescription>{g.tagline}</CardDescription>
                    </CardHeader>
                    <CardContent className="text-xs text-muted-foreground">
                      <span className="inline-flex items-center gap-1 font-medium text-foreground">Read the guide </span>
                    </CardContent>
                  </Card>
                </Link>
              ))}
            </div>
          </section>
        ))}
      </div>
    </>
  )
}

/** One strategy, always in the same 12-part structure; sections are collapsible to keep the page scannable. */
export function StrategyGuidePage() {
  const { strategyId } = useParams()
  const g = strategyId ? guideById(strategyId) : undefined
  if (!g) return <EmptyState title="Strategy not found" action={<Button render={<Link to="/learn" />}>All strategies</Button>} />

  const sections: { title: string; body: React.ReactNode; open?: boolean }[] = [
    { title: 'What is this strategy?', body: <p>{g.what}</p>, open: true },
    { title: 'The theory', body: <List items={g.theory} /> },
    { title: 'The objective', body: <p>{g.objective}</p>, open: true },
    {
      title: 'The mathematics',
      body: (
        <div className="space-y-3">
          <ul className="space-y-2">
            {g.math.map((m) => (
              <li key={m.formula}>
                <code className="block overflow-x-auto rounded-md bg-muted px-3 py-2 font-mono text-sm whitespace-pre">{m.formula}</code>
                <span className="text-xs text-muted-foreground">{m.caption}</span>
              </li>
            ))}
          </ul>
          <div>
            <h4 className="mb-1 text-xs font-semibold tracking-wide text-muted-foreground uppercase">Variables</h4>
            <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[minmax(4rem,auto)_1fr]">
              {g.variables.map(([v, d]) => (
                <div key={v} className="contents">
                  <dt className="font-mono">{v}</dt>
                  <dd className="text-muted-foreground">{d}</dd>
                </div>
              ))}
            </dl>
          </div>
        </div>
      ),
    },
    { title: 'How it works, step by step', body: <List items={g.how} ordered /> },
    { title: 'Required inputs', body: <List items={g.inputs} /> },
    { title: 'How to read the portfolio', body: <List items={g.interpretation} />, open: true },
    { title: 'When to use it', body: <List items={g.whenToUse} /> },
    {
      title: 'Advantages and limitations',
      body: (
        <div className="grid gap-4 md:grid-cols-2">
          <div><h4 className="mb-1 text-sm font-medium">Advantages</h4><List items={g.advantages} /></div>
          <div><h4 className="mb-1 text-sm font-medium">Limitations</h4><List items={g.limitations} /></div>
        </div>
      ),
    },
    {
      title: 'Worked example',
      body: (
        <div className="space-y-3">
          <p className="text-sm">{g.example.intro}</p>
          {g.example.table && (
            <div className="overflow-x-auto rounded-lg border">
              <table className="w-full text-sm">
                <caption className="sr-only">Worked example for {g.name}</caption>
                <thead className="bg-muted/40"><tr>{g.example.table.columns.map((c) => <th key={c} scope="col" className="px-3 py-1.5 text-left text-xs font-medium">{c}</th>)}</tr></thead>
                <tbody>{g.example.table.rows.map((r) => <tr key={r.join('|')} className="border-t">{r.map((c, i) => <td key={i} className="px-3 py-1.5 tabular">{c}</td>)}</tr>)}</tbody>
              </table>
            </div>
          )}
          <List items={g.example.steps} ordered />
          <p className="rounded-md border-l-2 border-series-1 bg-muted/40 px-3 py-2 text-sm"><strong>Takeaway:</strong> {g.example.takeaway}</p>
        </div>
      ),
      open: true,
    },
    {
      title: 'Interpreting your own results',
      body: (
        <div className="space-y-3">
          <List items={g.results} />
          <Button size="sm" render={<Link to={g.appLink.to} />}>{g.appLink.label} </Button>
        </div>
      ),
    },
    { title: 'Important caveats', body: <List items={g.caveats} />, open: true },
  ]

  return (
    <>
      <Button size="sm" variant="ghost" className="mb-2 -ml-2" render={<Link to="/learn" />}>Strategy guide</Button>
      <PageHeader title={g.name} description={g.tagline} actions={<Button size="sm" render={<Link to={g.appLink.to} />}>Try it </Button>} />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_220px]">
        <div className="space-y-2">
          {sections.map((s, i) => (
            <details key={s.title} id={`section-${i + 1}`} open={s.open} className="group rounded-xl border bg-background px-4 py-3 [&_summary::-webkit-details-marker]:hidden">
              <summary className="flex cursor-pointer list-none items-center gap-3 font-medium focus-visible:outline-none">
                <span className="grid size-6 shrink-0 place-items-center rounded-full bg-muted text-xs tabular">{i + 1}</span>
                <span className="flex-1">{s.title}</span>
                <span className="text-xs text-muted-foreground group-open:hidden">Show</span>
                <span className="hidden text-xs text-muted-foreground group-open:inline">Hide</span>
              </summary>
              <div className="mt-3 text-sm leading-relaxed">{s.body}</div>
            </details>
          ))}
        </div>
        <aside className="space-y-3 lg:sticky lg:top-20 lg:self-start">
          <Card className="py-4">
            <CardContent className="space-y-2 px-4 text-sm">
              <h2 className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">On this page</h2>
              <ol className="space-y-1">
                {sections.map((s, i) => <li key={s.title}><a href={`#section-${i + 1}`} className="text-muted-foreground hover:text-foreground">{i + 1}. {s.title}</a></li>)}
              </ol>
            </CardContent>
          </Card>
          {g.related.length > 0 && (
            <Card className="py-4">
              <CardContent className="space-y-2 px-4 text-sm">
                <h2 className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">Related</h2>
                {g.related.map((id) => guideById(id)).filter(Boolean).map((r) => (
                  <Link key={r!.id} to={`/learn/${r!.id}`} className="block text-muted-foreground hover:text-foreground">{r!.name}</Link>
                ))}
              </CardContent>
            </Card>
          )}
        </aside>
      </div>
    </>
  )
}

export function GlossaryPage() {
  const [text, setText] = useState('')
  const query = useDeferredValue(text.trim().toLowerCase())
  const entries = useMemo(() => glossary.filter((e) => !query || e.term.toLowerCase().includes(query) || e.definition.toLowerCase().includes(query)), [query])
  return (
    <>
      <Button size="sm" variant="ghost" className="mb-2 -ml-2" render={<Link to="/learn" />}>Strategy guide</Button>
      <PageHeader title="Glossary" info="Finance and machine-learning terms used in this app, in plain language." />
      <div className="relative mb-4 max-w-md">
        <Input value={text} onChange={(e) => setText(e.target.value)} placeholder="Search terms, e.g. Sharpe, leakage" aria-label="Search glossary" />
      </div>
      {entries.length === 0 && <EmptyState title="No matching term" />}
      <div className="space-y-6">
        {glossaryCategories.map((c) => {
          const items = entries.filter((e) => e.category === c)
          if (items.length === 0) return null
          return (
            <section key={c} aria-labelledby={`cat-${c}`}>
              <h2 id={`cat-${c}`} className="mb-2 text-sm font-semibold">{c}</h2>
              <dl className="grid gap-3 md:grid-cols-2">
                {items.map((e) => (
                  <div key={e.id} id={e.id} className="rounded-xl border bg-background p-3">
                    <dt className="font-medium">{e.term}</dt>
                    <dd className="mt-1 text-sm text-muted-foreground">{e.definition}</dd>
                    {e.example && <dd className="mt-1.5 text-xs"><Badge variant="outline" className="mr-1 h-4 px-1 text-[10px]">Example</Badge>{e.example}</dd>}
                  </div>
                ))}
              </dl>
            </section>
          )
        })}
      </div>
    </>
  )
}

function List({ items, ordered = false }: { items: string[]; ordered?: boolean }) {
  const Tag = ordered ? 'ol' : 'ul'
  return (
    <Tag className={ordered ? 'list-decimal space-y-1 pl-5' : 'list-disc space-y-1 pl-5'}>
      {items.map((i) => <li key={i}>{i}</li>)}
    </Tag>
  )
}
