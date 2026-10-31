import { useDeferredValue, useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { EmptyState, PageHeader } from '@/components/common'
import { glossary, glossaryCategories } from '@/lib/glossary'
import { guideById, strategyGuides, type StrategyGuide } from '@/lib/strategy-guide'
import { cn } from '@/lib/utils'

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
        actions={<Button size="sm" variant="outline" render={<Link to="/learn/glossary" />}>Open Lexicon</Button>}
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

/**
 * One strategy as a page to read from top to bottom: the same 12 topics for every strategy, in reading order (the idea
 * first, the formulas after the example), as headings of one article on a calm surface – nothing to unfold. A table of
 * contents follows the reader on wide screens; previous/next lead through the guides of the same family.
 */
export function StrategyGuidePage() {
  const { strategyId } = useParams()
  const g = strategyId ? guideById(strategyId) : undefined
  const sections = g ? guideSections(g) : []
  const active = useActiveSection(sections.map((s) => s.id))
  if (!g) return <EmptyState title="Strategy not found" action={<Button render={<Link to="/learn" />}>All strategies</Button>} />

  const family = families.find((f) => f.id === g.family)
  const siblings = strategyGuides.filter((x) => x.family === g.family)
  const at = siblings.findIndex((x) => x.id === g.id)
  const prev = siblings[at - 1]
  const next = siblings[at + 1]
  const related = g.related.map((id) => guideById(id)).filter((r): r is StrategyGuide => Boolean(r))

  return (
    <>
      <Button size="sm" variant="ghost" className="mb-2 -ml-2" render={<Link to="/learn" />}>Back to Codex</Button>
      <div className="grid justify-center gap-8 lg:grid-cols-[minmax(0,50rem)_14rem]">
        <article className="map-surface min-w-0 rounded-2xl px-5 py-6 sm:px-10 sm:py-8" aria-labelledby="guide-title">
          <header className="mb-8 max-w-[68ch] space-y-3 border-b pb-6">
            <p className="text-xs font-medium tracking-wide text-muted-foreground uppercase">{family?.title} · about {readingMinutes(g)} min read</p>
            <h1 id="guide-title" className="heading-display text-2xl sm:text-3xl">{g.name}</h1>
            <p className="text-lg leading-relaxed text-muted-foreground">{g.tagline}</p>
            <TryIt g={g} />
          </header>

          {/* On phones the contents sit above the text (no side column). */}
          <nav aria-label="Contents" className="mb-8 max-w-[68ch] lg:hidden">
            <h2 className="mb-2 text-xs font-semibold tracking-wide text-muted-foreground uppercase">Contents</h2>
            <ol className="grid gap-1 text-sm sm:grid-cols-2">
              {sections.map((s) => <li key={s.id}><a href={`#${s.id}`} className="text-muted-foreground hover:text-foreground">{s.title}</a></li>)}
            </ol>
          </nav>

          <div className="max-w-[68ch] space-y-10 text-[0.95rem] leading-7">
            {sections.map((s) => (
              <section key={s.id} id={s.id} aria-labelledby={`${s.id}-title`}>
                <h2 id={`${s.id}-title`} className="heading-display mb-3 text-xl">{s.title}</h2>
                {s.body}
              </section>
            ))}
          </div>

          <footer className="mt-12 max-w-[68ch] space-y-6 border-t pt-6">
            {related.length > 0 && (
              <div>
                <h2 className="mb-2 text-sm font-semibold">Read next</h2>
                <ul className="flex flex-wrap gap-2">
                  {related.map((r) => <li key={r.id}><Button size="sm" variant="outline" render={<Link to={`/learn/${r.id}`} />}>{r.name}</Button></li>)}
                </ul>
              </div>
            )}
            <nav aria-label="More guides" className="grid gap-3 sm:grid-cols-2">
              {prev ? <PagerLink to={prev} label="Previous" /> : <span />}
              {next ? <PagerLink to={next} label="Next" align="right" /> : <span />}
            </nav>
          </footer>
        </article>

        <aside className="hidden lg:sticky lg:top-32 lg:block lg:self-start">
          <nav aria-label="On this page" className="map-surface space-y-2 rounded-xl px-4 py-4 text-sm">
            <h2 className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">On this page</h2>
            <ol className="space-y-1 border-l">
              {sections.map((s) => (
                <li key={s.id}>
                  <a href={`#${s.id}`} aria-current={active === s.id ? 'location' : undefined}
                    className={cn('-ml-px block border-l-2 py-0.5 pl-3', active === s.id ? 'border-foreground font-medium text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground')}>
                    {s.title}
                  </a>
                </li>
              ))}
            </ol>
            <Button size="sm" variant="outline" className="mt-2 w-full" title={g.appLink.label} render={<Link to={g.appLink.to} />}>Try it</Button>
          </nav>
        </aside>
      </div>
    </>
  )
}

/** The 12 topics in reading order: what and why → how → example → formulas → using it → limits. */
function guideSections(g: StrategyGuide): { id: string; title: string; body: React.ReactNode }[] {
  return [
    { id: 'what', title: 'What is this strategy?', body: <p>{g.what}</p> },
    {
      id: 'objective', title: 'The objective',
      body: <p className="rounded-lg border-l-2 border-series-1 bg-muted/40 px-4 py-3">{g.objective}</p>,
    },
    { id: 'theory', title: 'The theory', body: <List items={g.theory} /> },
    { id: 'how', title: 'How it works, step by step', body: <List items={g.how} ordered /> },
    {
      id: 'example', title: 'Worked example',
      body: (
        <div className="space-y-4">
          <p>{g.example.intro}</p>
          {g.example.table && (
            <div className="overflow-x-auto rounded-lg border bg-background">
              <table className="w-full text-sm">
                <caption className="sr-only">Worked example for {g.name}</caption>
                <thead className="bg-muted/40"><tr>{g.example.table.columns.map((c) => <th key={c} scope="col" className="px-3 py-2 text-left text-xs font-medium whitespace-nowrap">{c}</th>)}</tr></thead>
                <tbody>{g.example.table.rows.map((r) => <tr key={r.join('|')} className="border-t">{r.map((c, i) => <td key={i} className="px-3 py-2 tabular whitespace-nowrap">{c}</td>)}</tr>)}</tbody>
              </table>
            </div>
          )}
          <List items={g.example.steps} ordered />
          <p className="rounded-lg border-l-2 border-series-1 bg-muted/40 px-4 py-3"><strong>Takeaway:</strong> {g.example.takeaway}</p>
        </div>
      ),
    },
    {
      id: 'math', title: 'The mathematics',
      body: (
        <div className="space-y-5">
          <p className="text-muted-foreground">For readers who want the formulas; everything above can be understood without them.</p>
          <ul className="space-y-4">
            {g.math.map((m) => (
              <li key={m.formula}>
                <code className="block overflow-x-auto rounded-md bg-muted px-4 py-3 font-mono text-sm whitespace-pre">{m.formula}</code>
                <span className="mt-1 block text-sm text-muted-foreground">{m.caption}</span>
              </li>
            ))}
          </ul>
          <div>
            <h3 className="mb-2 text-sm font-semibold">What the symbols mean</h3>
            <dl className="grid gap-x-6 gap-y-1.5 text-sm sm:grid-cols-[minmax(3rem,auto)_1fr]">
              {g.variables.map(([v, d]) => (
                <div key={v} className="contents">
                  <dt className="font-mono font-medium">{v}</dt>
                  <dd className="text-muted-foreground">{d}</dd>
                </div>
              ))}
            </dl>
          </div>
        </div>
      ),
    },
    { id: 'inputs', title: 'Required inputs', body: <List items={g.inputs} /> },
    { id: 'when', title: 'When to use it', body: <List items={g.whenToUse} /> },
    { id: 'reading', title: 'How to read the portfolio', body: <List items={g.interpretation} /> },
    {
      id: 'results', title: 'Interpreting your own results',
      body: (
        <div className="space-y-4">
          <List items={g.results} />
          <TryIt g={g} />
        </div>
      ),
    },
    {
      id: 'pros-cons', title: 'Advantages and limitations',
      body: (
        <div className="grid gap-6 sm:grid-cols-2">
          <div><h3 className="mb-1 text-sm font-semibold">Advantages</h3><List items={g.advantages} /></div>
          <div><h3 className="mb-1 text-sm font-semibold">Limitations</h3><List items={g.limitations} /></div>
        </div>
      ),
    },
    {
      id: 'caveats', title: 'Important caveats',
      body: <div className="rounded-lg border border-warn/40 bg-warn/5 px-4 py-3"><List items={g.caveats} /></div>,
    },
  ]
}

/** About 200 words a minute over every text of the guide (rounded up, at least one minute). */
function readingMinutes(g: StrategyGuide): number {
  const texts = [g.what, g.objective, ...g.theory, ...g.how, ...g.inputs, ...g.interpretation, ...g.whenToUse, ...g.advantages, ...g.limitations,
    g.example.intro, ...g.example.steps, g.example.takeaway, ...g.results, ...g.caveats, ...g.math.map((m) => m.caption), ...g.variables.map(([, d]) => d)]
  const words = texts.join(' ').split(/\s+/).filter(Boolean).length
  return Math.max(1, Math.ceil(words / 200))
}

/**
 * The section being read: the last one whose top has passed a line a third down the window, or the last section once
 * the page is scrolled to the bottom (a short final section never reaches that line).
 */
function useActiveSection(ids: string[]): string | undefined {
  const [active, setActive] = useState<string | undefined>(undefined)
  const key = ids.join(',')
  useEffect(() => {
    if (!key) return
    const elements = key.split(',').map((id) => document.getElementById(id)).filter((e): e is HTMLElement => Boolean(e))
    let frame = 0
    const update = () => {
      frame = 0
      const atBottom = window.innerHeight + window.scrollY >= document.documentElement.scrollHeight - 4
      const passed = elements.filter((e) => e.getBoundingClientRect().top <= window.innerHeight / 3)
      setActive((atBottom ? elements.at(-1) : passed.at(-1) ?? elements[0])?.id)
    }
    const onScroll = () => { if (!frame) frame = requestAnimationFrame(update) }
    update()
    window.addEventListener('scroll', onScroll, { passive: true })
    window.addEventListener('resize', onScroll)
    return () => {
      window.removeEventListener('scroll', onScroll)
      window.removeEventListener('resize', onScroll)
      if (frame) cancelAnimationFrame(frame)
    }
  }, [key])
  return active ?? ids[0]
}

/** The guide's link into the app; its label is a sentence, so the button wraps instead of running off narrow screens. */
function TryIt({ g }: { g: StrategyGuide }) {
  return <Button size="sm" className="h-auto max-w-full py-1.5 text-left whitespace-normal" render={<Link to={g.appLink.to} />}>{g.appLink.label}</Button>
}

function PagerLink({ to, label, align = 'left' }: { to: StrategyGuide; label: string; align?: 'left' | 'right' }) {
  return (
    <Link to={`/learn/${to.id}`} className={cn('rounded-xl border bg-background px-4 py-3 transition-colors hover:border-foreground/30 focus-visible:ring-2 focus-visible:ring-ring/50 focus-visible:outline-none', align === 'right' && 'text-right')}>
      <span className="block text-xs text-muted-foreground">{label}</span>
      <span className="block font-medium">{to.name}</span>
    </Link>
  )
}

export function GlossaryPage() {
  const [text, setText] = useState('')
  const query = useDeferredValue(text.trim().toLowerCase())
  const entries = useMemo(() => glossary.filter((e) => !query || e.term.toLowerCase().includes(query) || e.definition.toLowerCase().includes(query)), [query])
  return (
    <>
      <Button size="sm" variant="ghost" className="mb-2 -ml-2" render={<Link to="/learn" />}>Back to Codex</Button>
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
    <Tag className={cn(ordered ? 'list-decimal' : 'list-disc', 'space-y-2 pl-5 marker:text-muted-foreground')}>
      {items.map((i) => <li key={i}>{i}</li>)}
    </Tag>
  )
}
