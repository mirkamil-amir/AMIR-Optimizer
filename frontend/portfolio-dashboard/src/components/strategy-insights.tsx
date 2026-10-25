import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import type { Insight, InsightKind } from '@/lib/interpretation'
import { guideById } from '@/lib/strategy-guide'
import { Collapsible } from '@/components/common'

/**
 * Statements of one kind form one paragraph, in this order; the kind is named in plain text at the start (every statement
 * stays labelled as calculated, estimated, interpreted or a caution – without badges).
 */
const paragraphs: { kind: InsightKind; label: string }[] = [
  { kind: 'fact', label: 'Calculated from the data' },
  { kind: 'estimate', label: 'Estimates' },
  { kind: 'interpretation', label: 'What it means' },
  { kind: 'caution', label: 'Keep in mind' },
]

/** Further parts of the same box, e.g. "Inputs & methodology" and "Data notes"; a part without content is left out. */
export interface InsightSection { title: string; content: ReactNode }

/**
 * "What your result means": one folded box at the end of a result page. It starts with the interpretation built only from
 * the run's own numbers (grouped into paragraphs by kind), followed by the optional `sections` – how the run was computed
 * and the notes about its data – so the page has a single reference box instead of several. Links to the strategy guide.
 */
export function StrategyInsights({ insights, guideId, title = 'What your result means', sections = [], className }: {
  insights: Insight[]
  guideId?: string
  title?: string
  sections?: InsightSection[]
  className?: string
}) {
  const guide = guideId ? guideById(guideId) : undefined
  const parts = sections.filter((s) => s.content)
  if (insights.length === 0 && !guide && parts.length === 0) return null
  const howItWorks = guide && (
    <Button size="sm" variant="outline" render={<Link to={`/learn/${guide.id}`} />}>
      How {guide.name.toLowerCase().startsWith('from') ? 'it' : 'this strategy'} works
    </Button>
  )
  const summary = ['interpretation', ...parts.map((p) => p.title.toLowerCase())].join(' · ')
  return (
    <Collapsible title={title} summary={summary} actions={howItWorks} className={className}>
      <div className="space-y-3 leading-relaxed">
        <p className="text-muted-foreground">
          {guide ? <>About this strategy: {guide.tagline} </> : null}Generated from this run&apos;s numbers only.
        </p>
        {paragraphs.map(({ kind, label }) => {
          const texts = insights.filter((i) => i.kind === kind).map((i) => i.text)
          if (texts.length === 0) return null
          return (
            <p key={kind} data-kind={kind} className={kind === 'caution' ? 'text-warn' : undefined}>
              <span className="font-semibold">{label}. </span>{texts.join(' ')}
            </p>
          )
        })}
      </div>
      {parts.map((p) => (
        <section key={p.title} aria-label={p.title} className="mt-5 border-t pt-4">
          <h4 className="mb-2 font-semibold">{p.title}</h4>
          {p.content}
        </section>
      ))}
    </Collapsible>
  )
}

/** Notes about a run's data as a plain list (null when there are none, so the section is left out). */
export function DataNotesList({ items }: { items: string[] | null | undefined }) {
  if (!items?.length) return null
  return <ul className="list-disc space-y-0.5 pl-5 text-muted-foreground">{items.map((w) => <li key={w}>{w}</li>)}</ul>
}
