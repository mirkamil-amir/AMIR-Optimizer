/** Error carrying an RFC 9457 Problem Details payload from the API. */
export class ApiError extends Error {
  readonly status: number
  readonly title: string
  readonly detail?: string
  readonly errors?: Record<string, string[]>

  constructor(status: number, title: string, detail?: string, errors?: Record<string, string[]>) {
    super(detail ?? title)
    this.status = status
    this.title = title
    this.detail = detail
    this.errors = errors
  }

  /** Human-readable summary including field errors. */
  get description(): string {
    const fields = this.errors ? Object.values(this.errors).flat() : []
    return [this.detail, ...fields].filter(Boolean).join(' ') || this.title
  }
}

const BASE = import.meta.env.VITE_API_BASE_URL ?? ''
const XSRF_COOKIE = 'XSRF-TOKEN'
const XSRF_HEADER = 'X-XSRF-TOKEN'

/** Listeners notified when the session is missing or expired (HTTP 401), so the app can show the sign-in page. */
const unauthorizedListeners = new Set<() => void>()
export function onUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener)
  return () => unauthorizedListeners.delete(listener)
}

function readCookie(name: string): string | undefined {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`))
  return match ? decodeURIComponent(match[1]) : undefined
}

let tokenRequest: Promise<string> | null = null

/**
 * Antiforgery token for state-changing requests. The server issues it in a SameSite=Strict cookie bound to the current
 * session; it must be re-fetched after signing in or out.
 */
async function antiforgeryToken(refresh = false): Promise<string> {
  const existing = readCookie(XSRF_COOKIE)
  if (existing && !refresh) return existing
  tokenRequest ??= fetch(`${BASE}/api/v1/auth/antiforgery`, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
    .then(async (r) => {
      if (!r.ok) throw new ApiError(r.status, 'Could not obtain a security token')
      return ((await r.json()) as { token: string }).token
    })
    .finally(() => {
      tokenRequest = null
    })
  return tokenRequest
}

const UNSAFE = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

async function request<T>(method: string, path: string, body?: unknown, headers?: Record<string, string>, signal?: AbortSignal, retryToken?: string): Promise<T> {
  const requestHeaders: Record<string, string> = { Accept: 'application/json', ...headers }
  if (body !== undefined) requestHeaders['Content-Type'] = 'application/json'
  if (UNSAFE.has(method)) requestHeaders[XSRF_HEADER] = retryToken ?? await antiforgeryToken()
  let res: Response
  try {
    res = await fetch(`${BASE}${path}`, {
      method,
      credentials: 'same-origin',
      headers: requestHeaders,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal,
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new ApiError(0, 'Network error', 'The API is unreachable. Is the backend running?')
  }

  if (res.status === 204) {
    if (isSessionChange(path)) await antiforgeryToken(true)
    return undefined as T
  }
  const text = await res.text()
  const json = text ? safeParse(text) : undefined
  if (!res.ok) {
    const p = (json ?? {}) as { title?: string; detail?: string; errors?: Record<string, string[]> }
    // An expired/rotated token (e.g. after a server restart): fetch a fresh one and retry once.
    if (res.status === 400 && p.title?.startsWith('Antiforgery') && retryToken === undefined) {
      return request<T>(method, path, body, headers, signal, await antiforgeryToken(true))
    }
    if (res.status === 401 && !path.startsWith('/api/v1/auth/')) unauthorizedListeners.forEach((l) => l())
    throw new ApiError(res.status, p.title ?? res.statusText, p.detail, p.errors)
  }
  if (isSessionChange(path)) await antiforgeryToken(true)
  return json as T
}

const isSessionChange = (path: string) => path.endsWith('/auth/login') || path.endsWith('/auth/logout')

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text)
  } catch {
    return undefined
  }
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => request<T>('GET', path, undefined, undefined, signal),
  post: <T>(path: string, body?: unknown, headers?: Record<string, string>) => request<T>('POST', path, body ?? {}, headers),
  put: <T>(path: string, body: unknown) => request<T>('PUT', path, body),
  patch: <T>(path: string, body: unknown) => request<T>('PATCH', path, body),
  delete: <T>(path: string) => request<T>('DELETE', path),
}

export function errorMessage(e: unknown): string {
  if (e instanceof ApiError) return e.description
  if (e instanceof Error) return e.message
  return 'Unexpected error'
}
