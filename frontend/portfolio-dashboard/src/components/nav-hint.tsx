import type { ReactElement } from 'react'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'

/**
 * Shows a navigation entry's plain name (`hint`) on hover/focus; the entry itself is the trigger, unmarked. The delay
 * keeps tips from flashing while the pointer sweeps along the magnifying sidebar or tab bar. Screen readers get the same
 * text from the link's `aria-description`.
 */
export function NavHint({ hint, side, children }: { hint?: string; side: 'right' | 'bottom'; children: ReactElement }) {
  if (!hint) return children
  return (
    <Tooltip>
      <TooltipTrigger delay={500} render={children} />
      <TooltipContent side={side}>{hint}</TooltipContent>
    </Tooltip>
  )
}
