/** Matrix Pilot's model list: the primary, then fallbacks used only while no model of a cycle has answered. */
export interface ModelList { provider: string; model: string; fallbacks?: { provider: string; model: string }[] }

const one = (provider: string, model: string) =>
  provider === 'Mock' ? `${provider} ${model} (test script – not an AI)` : `${provider} ${model}`

/** "Gemini gemini-3.5-flash, then Groq llama-…" (a Mock model is labelled as a test script). */
export function modelListText(m: ModelList, isTest?: boolean): string {
  const primary = isTest && m.provider !== 'Mock' ? `${m.provider} ${m.model} (test script – not an AI)` : one(m.provider, m.model)
  return [primary, ...(m.fallbacks ?? []).map((f) => one(f.provider, f.model))].join(', then ')
}

/** The fallbacks as the server stores and compares them: `Provider:model;Provider:model` (empty = none). */
export function fallbackKey(m: ModelList): string {
  return (m.fallbacks ?? []).map((f) => `${f.provider}:${f.model}`).join(';')
}
