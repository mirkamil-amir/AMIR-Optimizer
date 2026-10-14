import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api, errorMessage, onUnauthorized } from './api'

type Call = { url: string; init: RequestInit }

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('api client', () => {
  let calls: Call[]
  let responses: Response[]

  beforeEach(() => {
    calls = []
    responses = []
    vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init })
      const next = responses.shift()
      if (!next) throw new Error(`unexpected request ${url}`)
      return next
    }))
  })
  afterEach(() => vi.unstubAllGlobals())

  const header = (c: Call, name: string) => (c.init.headers as Record<string, string>)[name]

  it('sends GET requests without an antiforgery token', async () => {
    responses.push(json(200, { ok: true }))
    await expect(api.get('/api/v1/stocks')).resolves.toEqual({ ok: true })
    expect(calls).toHaveLength(1)
    expect(header(calls[0], 'X-XSRF-TOKEN')).toBeUndefined()
    expect(calls[0].init.credentials).toBe('same-origin')
  })

  it('attaches the antiforgery token from the cookie to unsafe methods', async () => {
    document.cookie = 'XSRF-TOKEN=cookie-token'
    responses.push(json(200, {}))
    await api.post('/api/v1/portfolios', { name: 'x' })
    expect(calls).toHaveLength(1)
    expect(header(calls[0], 'X-XSRF-TOKEN')).toBe('cookie-token')
    expect(calls[0].init.body).toBe(JSON.stringify({ name: 'x' }))
  })

  it('fetches a token when no cookie exists', async () => {
    responses.push(json(200, { token: 'fetched' }), json(200, {}))
    await api.delete('/api/v1/ml/experiments/1')
    expect(calls[0].url).toBe('/api/v1/auth/antiforgery')
    expect(header(calls[1], 'X-XSRF-TOKEN')).toBe('fetched')
  })

  it('refreshes the token and retries once after an antiforgery rejection', async () => {
    document.cookie = 'XSRF-TOKEN=stale'
    responses.push(
      json(400, { title: 'Antiforgery token validation failed' }),
      json(200, { token: 'fresh' }),
      json(200, { done: true }),
    )
    await expect(api.post('/api/v1/optimizations', {})).resolves.toEqual({ done: true })
    expect(header(calls[2], 'X-XSRF-TOKEN')).toBe('fresh')
  })

  it('does not retry an antiforgery rejection twice', async () => {
    document.cookie = 'XSRF-TOKEN=stale'
    responses.push(
      json(400, { title: 'Antiforgery token validation failed' }),
      json(200, { token: 'fresh' }),
      json(400, { title: 'Antiforgery token validation failed' }),
    )
    await expect(api.post('/api/v1/optimizations', {})).rejects.toMatchObject({ status: 400 })
    expect(calls).toHaveLength(3)
  })

  it('maps Problem Details to ApiError with field errors', async () => {
    responses.push(json(422, { title: 'Validation failed', detail: 'Bad input.', errors: { symbols: ['Select at least 2.'] } }))
    const error = await api.get('/api/v1/x').catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(422)
    expect(errorMessage(error)).toBe('Bad input. Select at least 2.')
  })

  it('notifies listeners on 401 from data endpoints but not from auth endpoints', async () => {
    const listener = vi.fn()
    const unsubscribe = onUnauthorized(listener)
    responses.push(json(401, { title: 'Unauthorized' }), json(401, { title: 'Unauthorized' }))
    await api.get('/api/v1/portfolios').catch(() => undefined)
    await api.get('/api/v1/auth/me').catch(() => undefined)
    expect(listener).toHaveBeenCalledTimes(1)
    unsubscribe()
  })

  it('reports an unreachable backend as a network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch') }))
    const error = await api.get('/api/v1/x').catch((e: unknown) => e)
    expect(error).toMatchObject({ status: 0, title: 'Network error' })
  })

  it('refreshes the antiforgery token after signing in', async () => {
    document.cookie = 'XSRF-TOKEN=anonymous'
    responses.push(json(200, { email: 'a@b.c' }), json(200, { token: 'session-bound' }))
    await api.post('/api/v1/auth/login', { email: 'a@b.c', password: 'x' })
    expect(calls[1].url).toBe('/api/v1/auth/antiforgery')
  })
})
