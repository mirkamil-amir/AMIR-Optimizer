// Consistent number, currency, percentage and date formatting (en-US, values are fractions for %).

const currencyCache = new Map<string, Intl.NumberFormat>()

export function money(value: number | null | undefined, currency = 'USD', opts: { compact?: boolean; signed?: boolean; decimals?: number } = {}): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  const key = `${currency}|${opts.compact}|${opts.signed}|${opts.decimals}`
  let f = currencyCache.get(key)
  if (!f) {
    f = new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      notation: opts.compact ? 'compact' : 'standard',
      minimumFractionDigits: opts.compact ? 0 : (opts.decimals ?? 2),
      maximumFractionDigits: opts.compact ? 1 : (opts.decimals ?? 2),
      signDisplay: opts.signed ? 'exceptZero' : 'auto',
    })
    currencyCache.set(key, f)
  }
  return f.format(value)
}

export function pct(value: number | null | undefined, decimals = 2, signed = false): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return new Intl.NumberFormat('en-US', {
    style: 'percent',
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
    signDisplay: signed ? 'exceptZero' : 'auto',
  }).format(value)
}

export function num(value: number | null | undefined, decimals = 2): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return new Intl.NumberFormat('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals }).format(value)
}

/** Share quantities: whole numbers without decimals, fractional up to 4. */
export function qty(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 }).format(value)
}

export function compact(value: number): string {
  return new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(value)
}

/** ISO date (yyyy-MM-dd) → "Oct 9, 2026" without timezone shifting. */
export function date(iso: string | null | undefined): string {
  if (!iso) return '—'
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' })
}

export function shortDate(iso: string): string {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-US', { year: '2-digit', month: 'short', timeZone: 'UTC' })
}

/**
 * Axis label for a date in a series spanning `spanDays`: day and month for short spans ("Aug 12"), month and year otherwise
 * ("Aug 26") – month-only labels repeat on a three-month chart.
 */
export function axisDate(iso: string, spanDays: number): string {
  if (spanDays > 120) return shortDate(iso)
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

/** UTC timestamp → local date-time. */
export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('en-US', { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

export function relativeTime(iso: string | null | undefined): string {
  if (!iso) return 'never'
  const diff = (Date.now() - new Date(iso).getTime()) / 1000
  if (diff < 60) return 'just now'
  if (diff < 3600) return `${Math.floor(diff / 60)} min ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)} h ago`
  return `${Math.floor(diff / 86400)} d ago`
}

export function signClass(value: number | null | undefined): string {
  if (value === null || value === undefined || value === 0 || !Number.isFinite(value)) return 'text-foreground'
  return value > 0 ? 'text-gain' : 'text-loss'
}

export const objectiveLabels: Record<string, string> = {
  MinimumVariance: 'Minimum variance',
  MaximumSharpe: 'Maximum Sharpe ratio',
  TargetReturn: 'Target return',
  MaximumReturnForRisk: 'Max return for risk limit',
  EqualWeight: 'Equal weight',
}

export const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
