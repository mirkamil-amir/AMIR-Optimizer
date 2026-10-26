import { useMemo, useState, type ReactNode } from 'react'
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { num } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Rows-per-page choices of every paged list (stock chooser, Market Data). */
export const PAGE_SIZES = [25, 50, 100, 200] as const

export interface Column<T> {
  key: string
  header: ReactNode
  cell: (row: T) => ReactNode
  /** Value used for sorting; omit to make the column unsortable. */
  sortValue?: (row: T) => number | string | null | undefined
  align?: 'left' | 'right'
  className?: string
}

/**
 * Accessible sortable table (sort buttons in headers, keyboard-activatable rows). `maxRows`: about that many rows are
 * visible; the rest scroll inside the table under a header that stays in place. `pageSize`: the sorted rows are shown in
 * pages (initially of that size) with the shared `RangePager` below – first/previous/next/last and rows per page; a new
 * `pageResetKey` (e.g. the search text), another sort or another page size returns to page 1.
 */
export function DataTable<T>({
  rows, columns, rowKey, onRowClick, initialSort, empty, dense, caption, maxRows, pageSize, pageResetKey = '',
}: {
  rows: T[]
  columns: Column<T>[]
  rowKey: (row: T) => string
  onRowClick?: (row: T) => void
  initialSort?: { key: string; dir: 'asc' | 'desc' }
  empty?: ReactNode
  dense?: boolean
  caption?: string
  maxRows?: number
  pageSize?: number
  pageResetKey?: string
}) {
  const [sort, setSort] = useState(initialSort)
  // The page belongs to one search and one sort: either changing starts again at page 1 (no effect needed).
  const [paging, setPaging] = useState({ key: pageResetKey, sort: initialSort, page: 1 })
  const [size, setSize] = useState(pageSize ?? 0)
  const sorted = useMemo(() => {
    if (!sort) return rows
    const col = columns.find((c) => c.key === sort.key)
    if (!col?.sortValue) return rows
    const factor = sort.dir === 'asc' ? 1 : -1
    return [...rows].sort((a, b) => {
      const va = col.sortValue!(a)
      const vb = col.sortValue!(b)
      if (va === vb) return 0
      if (va === null || va === undefined) return 1
      if (vb === null || vb === undefined) return -1
      return (va < vb ? -1 : 1) * factor
    })
  }, [rows, columns, sort])

  if (rows.length === 0 && empty) return <>{empty}</>
  const totalPages = size ? Math.max(1, Math.ceil(sorted.length / size)) : 1
  const page = paging.key === pageResetKey && paging.sort === sort ? Math.min(paging.page, totalPages) : 1
  const visible = size ? sorted.slice((page - 1) * size, page * size) : sorted

  return (
    <div>
      <div className={cn('overflow-x-auto rounded-lg border', maxRows && 'overflow-y-auto')}
        style={maxRows ? { maxHeight: `calc(2.25rem + ${maxRows} * ${dense ? 2.3 : 2.85}rem)` } : undefined}>
        <Table containerClassName={maxRows ? 'overflow-visible' : undefined}>
          {caption && <caption className="sr-only">{caption}</caption>}
          <TableHeader className={maxRows ? 'sticky top-0 z-10 bg-popover' : undefined}>
            <TableRow className="bg-muted/40 hover:bg-muted/40">
              {columns.map((c) => {
                const active = sort?.key === c.key
                const ariaSort = active ? (sort!.dir === 'asc' ? 'ascending' : 'descending') : 'none'
                return (
                  <TableHead key={c.key} aria-sort={c.sortValue ? ariaSort : undefined} className={cn('h-9 text-xs', c.align === 'right' && 'text-right', c.className)}>
                    {c.sortValue ? (
                      <button
                        type="button"
                        className={cn('inline-flex items-center gap-1 hover:text-foreground', c.align === 'right' && 'flex-row-reverse')}
                        onClick={() => setSort(active && sort!.dir === 'desc' ? { key: c.key, dir: 'asc' } : { key: c.key, dir: 'desc' })}
                      >
                        {c.header}
                        {active ? sort!.dir === 'asc' ? <ArrowUp className="size-3" /> : <ArrowDown className="size-3" /> : <ArrowUpDown className="size-3 opacity-40" />}
                      </button>
                    ) : (
                      c.header
                    )}
                  </TableHead>
                )
              })}
            </TableRow>
          </TableHeader>
          <TableBody>
            {visible.map((row) => (
              <TableRow
                key={rowKey(row)}
                className={cn(onRowClick && 'cursor-pointer')}
                onClick={onRowClick ? () => onRowClick(row) : undefined}
                onKeyDown={onRowClick ? (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onRowClick(row) } } : undefined}
                tabIndex={onRowClick ? 0 : undefined}
              >
                {columns.map((c) => (
                  <TableCell key={c.key} className={cn('tabular', dense ? 'py-1.5' : 'py-2.5', c.align === 'right' && 'text-right', c.className)}>
                    {c.cell(row)}
                  </TableCell>
                ))}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
      {size > 0 && (
        <div className="mt-3 flex justify-end">
          <RangePager page={page} pageSize={size} total={sorted.length} totalPages={totalPages}
            onPage={(p) => setPaging({ key: pageResetKey, sort, page: p })}
            onPageSize={(n) => { setSize(n); setPaging({ key: pageResetKey, sort, page: 1 }) }} />
        </div>
      )}
    </div>
  )
}

export function Pager({ page, totalPages, onChange }: { page: number; totalPages: number; onChange: (p: number) => void }) {
  if (totalPages <= 1) return null
  return (
    <div className="mt-3 flex items-center justify-end gap-2 text-sm text-muted-foreground">
      <Button variant="outline" size="icon-sm" aria-label="Previous page" disabled={page <= 1} onClick={() => onChange(page - 1)}>
        <ChevronLeft />
      </Button>
      <span className="tabular">Page {page} of {totalPages}</span>
      <Button variant="outline" size="icon-sm" aria-label="Next page" disabled={page >= totalPages} onClick={() => onChange(page + 1)}>
        <ChevronRight />
      </Button>
    </div>
  )
}

/** Paging for a large list: position ("51–100 of 2,158"), first/previous/next/last and the number of rows per page. */
export function RangePager({ page, pageSize, total, totalPages, onPage, onPageSize }: {
  page: number; pageSize: number; total: number; totalPages: number; onPage: (p: number) => void; onPageSize: (n: number) => void
}) {
  if (total === 0) return null
  const from = (page - 1) * pageSize + 1
  const to = Math.min(total, page * pageSize)
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-muted-foreground">
      <div className="flex items-center gap-2">
        <span>Rows per page</span>
        <Select value={String(pageSize)} onValueChange={(v) => onPageSize(Number(v))} items={PAGE_SIZES.map((n) => ({ value: String(n), label: String(n) }))}>
          <SelectTrigger size="sm" className="w-20" aria-label="Rows per page"><SelectValue /></SelectTrigger>
          <SelectContent>{PAGE_SIZES.map((n) => <SelectItem key={n} value={String(n)}>{n}</SelectItem>)}</SelectContent>
        </Select>
      </div>
      <div className="flex items-center gap-1">
        <span className="mr-2 tabular">{num(from, 0)}–{num(to, 0)} of {num(total, 0)}</span>
        <Button variant="outline" size="icon-sm" aria-label="First page" disabled={page <= 1} onClick={() => onPage(1)}><ChevronsLeft /></Button>
        <Button variant="outline" size="icon-sm" aria-label="Previous page" disabled={page <= 1} onClick={() => onPage(page - 1)}><ChevronLeft /></Button>
        <span className="px-1 tabular">Page {page} of {totalPages}</span>
        <Button variant="outline" size="icon-sm" aria-label="Next page" disabled={page >= totalPages} onClick={() => onPage(page + 1)}><ChevronRight /></Button>
        <Button variant="outline" size="icon-sm" aria-label="Last page" disabled={page >= totalPages} onClick={() => onPage(totalPages)}><ChevronsRight /></Button>
      </div>
    </div>
  )
}
