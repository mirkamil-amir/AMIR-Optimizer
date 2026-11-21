import { describe, expect, it } from 'vitest'
import { fallbackKey, modelListText } from '@/lib/pilot-models'

describe('pilot model list', () => {
  it('names the primary alone when there are no fallbacks', () => {
    expect(modelListText({ provider: 'Gemini', model: 'gemini-3.5-flash' })).toBe('Gemini gemini-3.5-flash')
    expect(fallbackKey({ provider: 'Gemini', model: 'gemini-3.5-flash', fallbacks: [] })).toBe('')
  })

  it('lists the fallbacks in order and builds the server key', () => {
    const m = { provider: 'Gemini', model: 'g', fallbacks: [{ provider: 'Groq', model: 'meta/llama:free' }, { provider: 'Mistral', model: 'm' }] }
    expect(modelListText(m)).toBe('Gemini g, then Groq meta/llama:free, then Mistral m')
    expect(fallbackKey(m)).toBe('Groq:meta/llama:free;Mistral:m')
  })

  it('labels a test model as not an AI', () => {
    expect(modelListText({ provider: 'Mock', model: 'mock-model' })).toBe('Mock mock-model (test script – not an AI)')
    expect(modelListText({ provider: 'X', model: 'y' }, true)).toBe('X y (test script – not an AI)')
  })
})
