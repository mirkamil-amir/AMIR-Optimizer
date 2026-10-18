import { describe, expect, it } from 'vitest'
import { validateDraft, type PortfolioDraft } from './create-portfolio'

const draft = (over: Partial<PortfolioDraft> = {}): PortfolioDraft => ({ name: 'Core', description: '', currency: 'USD', capital: '100000', maxWeight: '', ...over })

describe('Portfolio Creation validation', () => {
  it('accepts a complete draft', () => {
    expect(validateDraft(draft())).toEqual({})
    expect(validateDraft(draft({ maxWeight: '25', description: 'x'.repeat(500) }))).toEqual({})
  })

  it('requires a name of at most 100 characters', () => {
    expect(validateDraft(draft({ name: '   ' })).name).toBe('Enter a name.')
    expect(validateDraft(draft({ name: 'x'.repeat(101) })).name).toBe('At most 100 characters.')
  })

  it('requires starting cash between 100 and 1,000,000,000', () => {
    expect(validateDraft(draft({ capital: '' })).capital).toBe('Enter an amount.')
    expect(validateDraft(draft({ capital: 'abc' })).capital).toBe('Enter an amount.')
    expect(validateDraft(draft({ capital: '99.99' })).capital).toBe('At least 100.')
    expect(validateDraft(draft({ capital: '1000000001' })).capital).toBe('At most 1,000,000,000.')
  })

  it('checks the optional weight limit and description length', () => {
    expect(validateDraft(draft({ maxWeight: '0' })).maxWeight).toBeDefined()
    expect(validateDraft(draft({ maxWeight: '101' })).maxWeight).toBeDefined()
    expect(validateDraft(draft({ description: 'x'.repeat(501) })).description).toBeDefined()
  })
})
