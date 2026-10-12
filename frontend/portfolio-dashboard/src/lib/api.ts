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

async function request<T>(method: string, path: string, body?: unknown, headers?: Record<string, string>, signal?: AbortSignal): Promise<T> {
  let res: Response
  try {
    res = await fetch(`${BASE}${path}`, {
      method,
      headers: { Accept: 'application/json', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...headers },
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal,
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new ApiError(0, 'Network error', 'The API is unreachable. Is the backend running on port 5080?')
  }

  if (res.status === 204) return undefined as T
  const text = await res.text()
  const json = text ? safeParse(text) : undefined
  if (!res.ok) {
    const p = (json ?? {}) as { title?: string; detail?: string; errors?: Record<string, string[]> }
    throw new ApiError(res.status, p.title ?? res.statusText, p.detail, p.errors)
  }
  return json as T
}

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
