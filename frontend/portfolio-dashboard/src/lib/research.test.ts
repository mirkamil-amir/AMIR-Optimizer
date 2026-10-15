import { describe, expect, it } from 'vitest'
import { briefRows, isEligible, originLabel } from './research'
import type { Hypothesis, ResearchBrief, ResearchCandidate } from './types'

const brief: ResearchBrief = {
  question: 'low risk US', path: 'build-portfolio', market: 'US', index: 'SP500', excludeSectors: ['Energy'], excludeSymbols: [],
  riskPreference: 'low', lossCapacity: null, horizonYears: null, objective: 'MinimumVariance', maxWeight: 0.25, candidateCount: 10, lookbackDays: 504,
  origins: {
    market: { source: 'user', reason: 'you wrote “US”' },
    objective: { source: 'assumption', reason: 'low risk → minimum variance' },
  },
  missingInformation: [], dataRequirements: [], intendedOutput: '',
}

describe('research helpers', () => {
  it('separates what the user stated from what the system assumed', () => {
    const rows = briefRows(brief)
    expect(rows.find((r) => r.key === 'market')).toMatchObject({ value: 'United States (S&P 500)', origin: 'user' })
    expect(rows.find((r) => r.key === 'objective')).toMatchObject({ value: 'Minimum variance', origin: 'assumption' })
    expect(rows.find((r) => r.key === 'lossCapacity')?.value).toBe('not stated')
    expect(rows.find((r) => r.key === 'maxWeight')?.value).toBe('25 %')
  })

  it('only screened stocks that meet the data requirements are eligible', () => {
    const c = (included: boolean, reason: string) => ({ included, reason }) as ResearchCandidate
    expect(isEligible(c(true, 'Meets the data requirements; selected'))).toBe(true)
    expect(isEligible(c(false, 'Meets the data requirements but ranked below'))).toBe(true)
    expect(isEligible(c(false, 'Only 12 stored daily prices'))).toBe(false)
    expect(isEligible(c(false, 'Sector Energy excluded by you.'))).toBe(false)
  })

  it('labels hypothesis origins', () => {
    expect(originLabel({ origin: 'baseline' } as Hypothesis)).toBe('Baseline')
    expect(originLabel({ origin: 'refinement of “X”: cap 20%' } as Hypothesis)).toBe('Refined: “X”: cap 20%')
  })
})
