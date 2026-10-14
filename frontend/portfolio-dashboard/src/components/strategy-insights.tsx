import { Link } from 'react-router'
import { AlertTriangle, BookOpen, Calculator, FlaskConical, Lightbulb } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import type { Insight, InsightKind } from '@/lib/interpretation'
import { guideById } from '@/lib/strategy-guide'
import { cn } from '@/lib/utils'

const kinds: Record<InsightKind, { label: string; icon: React.ReactNode; className: string; title: string }> = {
  fact: { label: 'Calculated', icon: <Calculator className="size-3" />, className: 'border-border text-foreground', title: 'A number the system calculated from the data and settings.' },
  estimate: { label: 'Estimate', icon: <FlaskConical className="size-3" />, className: 'border-series-7/50 text-foreground', title: 'A statistical estimate – uncertain, not a forecast.' },
  interpretation: { label: 'What it means', icon: <Lightbulb className="size-3" />, className: 'border-series-1/50 text-foreground', title: 'An educational reading of the numbers.' },
  caution: { label: 'Caution', icon: <AlertTriangle className="size-3" />, className: 'border-warn/50 text-warn', title: 'A limitation you should keep in mind.' },
}

/**
 * "What your result means": interpretation built only from the run's own numbers, with every statement labelled as a
 * calculated fact, a model estimate, an educational interpretation or a caution. Links to the strategy guide.
 */
export function StrategyInsights({ insights, guideId, title = 'What your result means', className }: {
  insights: Insight[]
  guideId?: string
  title?: string
  className?: string
}) {
  const guide = guideId ? guideById(guideId) : undefined
  if (insights.length === 0 && !guide) return null
  return (
    <Card className={className}>
      <CardHeader className="flex flex-row items-start justify-between gap-3">
        <div>
          <CardTitle className="text-base">{title}</CardTitle>
          <CardDescription>
            {guide ? <>About this strategy: {guide.tagline} </> : null}
            Statements are generated from this run&apos;s numbers and labelled by type.
          </CardDescription>
        </div>
        {guide && (
          <Button size="sm" variant="outline" render={<Link to={`/learn/${guide.id}`} />}>
            <BookOpen /> How {guide.name.toLowerCase().startsWith('from') ? 'it' : 'this strategy'} works
          </Button>
        )}
      </CardHeader>
      <CardContent>
        <ul className="space-y-2.5">
          {insights.map((i, n) => {
            const k = kinds[i.kind]
            return (
              <li key={n} className="flex gap-2.5 text-sm leading-relaxed">
                <Badge variant="outline" title={k.title} className={cn('mt-0.5 h-5 shrink-0 gap-1 px-1.5 text-[10px]', k.className)}>
                  {k.icon}{k.label}
                </Badge>
                <span>{i.text}</span>
              </li>
            )
          })}
        </ul>
      </CardContent>
    </Card>
  )
}
