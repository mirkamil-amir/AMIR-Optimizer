import { describe, expect, it } from 'vitest'
// The explanations must cover exactly the server's feature catalogue (a new or renamed feature needs a text).
import catalog from '../../../../src/PortfolioOptimizer.MachineLearning/Features/FeatureCatalog.cs?raw'
import { featureMeanings } from './ml-features'

const names = [...catalog.matchAll(/new\("([a-z0-9_]+)", FeatureGroup\./g)].map((m) => m[1])

describe('ML feature meanings', () => {
  it('explain every feature of the catalogue and nothing else', () => {
    expect(names).toHaveLength(20)
    expect(Object.keys(featureMeanings).sort()).toEqual([...names].sort())
  })

  it('are complete for every feature', () => {
    for (const [name, m] of Object.entries(featureMeanings))
      for (const text of [m.label, m.meaning, m.reading, m.why]) expect(text.length, name).toBeGreaterThan(10)
  })
})
