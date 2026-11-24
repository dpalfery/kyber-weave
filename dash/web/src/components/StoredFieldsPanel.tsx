import { useState } from 'react'
import { QueryClient, QueryClientProvider, useQueries, useQuery } from '@tanstack/react-query'
import { cn } from '../lib/utils.js'
import {
  fetchStoredTableRowsPage,
  fetchStoredFields,
  isNotFoundError,
  type StoredFieldRow,
  type StoredTableFields,
} from '../lib/kyberApi.js'

/**
 * Stored-fields inspection. The engine decides what the relational store
 * persisted; this panel only lays the catalog-driven view out. The inventory
 * (tables, columns, row counts) loads at mount; a table's rows load through
 * the rows route only when the viewer opens that table, 25 at a time. Every
 * reachable column renders labeled `table.column`; a figure the store never
 * measured renders as "not reported" — never as `0` or `""`.
 */
export type StoredFieldsPanelProps = {
  /** The inspected session's key. Absent means the view is not addressable. */
  sessionKey?: string | null
  className?: string
}

/** The inventory entry one section lays out: catalog columns and a row count, no rows. */
type StoredTableInventory = StoredTableFields

/**
 * Formats one stored cell for display. A measured figure — including a
 * measured `0` — renders verbatim; a cell the store never measured resolves
 * to `undefined` so the caller prints "not reported" instead of `0` or `""`.
 */
export function formatStoredFieldValue(cell: StoredFieldRow['cells'][number]): string | undefined {
  if (typeof cell.textValue === 'string') return cell.textValue
  if (typeof cell.integerValue === 'number' && Number.isFinite(cell.integerValue)) {
    return String(cell.integerValue)
  }
  if (typeof cell.realValue === 'number' && Number.isFinite(cell.realValue)) {
    return String(cell.realValue)
  }
  return undefined
}

/** An absent figure, stated as such — never a zero or an empty string. */
function notReported(label: string) {
  return (
    <span className="italic text-tertiary-foreground" data-testid={`stored-fields-not-reported-${label}`}>
      not reported
    </span>
  )
}

function StoredTableSection({ sessionKey, table }: { sessionKey: string; table: StoredTableInventory }) {
  const [open, setOpen] = useState(false)
  const [pagesToLoad, setPagesToLoad] = useState(1)
  const columns = Array.isArray(table.columnNames) ? table.columnNames : []
  const missing = Array.isArray(table.missingParentKeys) ? table.missingParentKeys : []
  const hasRows = typeof table.rowCount === 'number' && table.rowCount > 0

  return (
    <section data-testid={`stored-fields-table-${table.tableName}`} className="space-y-2">
      <div className="flex flex-wrap items-baseline gap-2">
        <h3 className="font-mono text-xs font-semibold text-foreground">{table.tableName}</h3>
        {hasRows && (
          <button
            type="button"
            data-testid={`stored-fields-table-toggle-${table.tableName}`}
            aria-expanded={open}
            onClick={() => setOpen((current) => !current)}
            className="rounded border border-border px-1.5 py-0.5 text-[11px] text-muted-foreground hover:bg-interactive-secondary"
          >
            {open ? 'Hide rows' : 'Show rows'}
          </button>
        )}
        <span className="text-[11px] text-tertiary-foreground">
          {table.rowCount === 0
            ? 'no rows stored'
            : `${table.rowCount} row${table.rowCount === 1 ? '' : 's'}`}
        </span>
      </div>

      <div className="overflow-x-auto rounded-md border border-border">
        <table className="w-full text-left text-xs">
          <thead>
            <tr className="border-b border-border bg-interactive-secondary/40 text-[10.5px] font-semibold uppercase tracking-wider text-tertiary-foreground">
              {columns.map((column) => (
                <th
                  key={column}
                  data-testid={`stored-field-label-${table.tableName}.${column}`}
                  className="px-2.5 py-1.5 font-mono normal-case tracking-normal"
                >
                  {table.tableName}.{column}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-border/40">
            {open && hasRows && (
              <StoredTableRows
                sessionKey={sessionKey}
                table={table}
                pagesToLoad={pagesToLoad}
                onLoadMore={() => setPagesToLoad((pages) => pages + 1)}
              />
            )}
          </tbody>
        </table>
      </div>

      {hasRows && !open && (
        <p data-testid={`stored-fields-closed-${table.tableName}`} className="text-xs text-tertiary-foreground">
          Rows load when the table is opened.
        </p>
      )}

      {table.rowCount === 0 && (
        <div
          data-testid={`stored-fields-empty-${table.tableName}`}
          className="rounded-md border border-border bg-card px-3 py-2 text-xs text-muted-foreground"
        >
          {missing.length > 0 ? (
            <ul className="list-disc list-inside space-y-0.5">
              {missing.map((key) => (
                <li key={key}>
                  <span className="font-mono text-foreground">{key}</span>
                  {' — '}
                  {notReported(`${table.tableName}.${key}`)}
                </li>
              ))}
            </ul>
          ) : (
            <p>
              No rows stored in <span className="font-mono">{table.tableName}</span> —{' '}
              {notReported(table.tableName)}
            </p>
          )}
        </div>
      )}
    </section>
  )
}

/**
 * One opened table's rows, 25 to a page: every page the viewer asked for is
 * its own query, and the pages accumulate in order, so loading the next page
 * never blanks the rows on screen.
 */
function StoredTableRows({
  sessionKey,
  table,
  pagesToLoad,
  onLoadMore,
}: {
  sessionKey: string
  table: StoredTableInventory
  pagesToLoad: number
  onLoadMore: () => void
}) {
  const columnCount = Math.max(1, table.columnNames.length)
  const queries = useQueries({
    queries: Array.from({ length: pagesToLoad }, (_, i) => i + 1).map((page) => ({
      queryKey: ['kyber-stored-fields-rows', sessionKey, table.tableName, page],
      queryFn: () => fetchStoredTableRowsPage(sessionKey, table.tableName, page),
      retry: false,
    })),
  })

  const first = queries[0]
  if (first.isPending) {
    return (
      <tr>
        <td colSpan={columnCount}>
          <div
            data-testid={`stored-fields-loading-rows-${table.tableName}`}
            role="status"
            aria-live="polite"
            className="px-2.5 py-2 text-xs text-tertiary-foreground"
          >
            Loading rows…
          </div>
        </td>
      </tr>
    )
  }
  if (first.isError || first.data === undefined) {
    return (
      <tr>
        <td colSpan={columnCount}>
          <div
            data-testid={`stored-fields-rows-error-${table.tableName}`}
            role="status"
            aria-live="polite"
            className="px-2.5 py-2 text-xs text-destructive-foreground"
          >
            Rows for <span className="font-mono">{table.tableName}</span> could not be loaded.{' '}
            <button
              type="button"
              data-testid={`stored-fields-rows-retry-${table.tableName}`}
              onClick={() => void first.refetch()}
              className="underline"
            >
              Retry
            </button>
          </div>
        </td>
      </tr>
    )
  }

  const rows: StoredFieldRow[] = []
  let totalRows = 0
  let laterPageFailed = false
  for (const query of queries) {
    if (query.isError) {
      laterPageFailed = true
      continue
    }
    if (query.data) {
      rows.push(...query.data.rows)
      totalRows = query.data.totalRows
    }
  }
  const anyPending = queries.some((query) => query.isPending)

  return (
    <>
      {rows.map((row, rowIndex) => (
        <tr key={rowIndex} data-testid={`stored-field-row-${table.tableName}-${rowIndex}`}>
          {(Array.isArray(row.cells) ? row.cells : []).map((cell, cellIndex) => {
            const value = formatStoredFieldValue(cell)
            const label = `${table.tableName}.${cell.columnName}`
            return (
              <td
                key={`${cell.columnName}-${cellIndex}`}
                data-testid={`stored-field-value-${label}`}
                className="px-2.5 py-1.5 font-mono text-[11px] text-foreground break-all"
              >
                {value === undefined ? notReported(label) : value}
              </td>
            )
          })}
        </tr>
      ))}
      {laterPageFailed && (
        <tr>
          <td colSpan={columnCount}>
            <div
              data-testid={`stored-fields-rows-error-${table.tableName}`}
              role="status"
              aria-live="polite"
              className="px-2.5 py-2 text-xs text-destructive-foreground"
            >
              Later rows of <span className="font-mono">{table.tableName}</span> could not be loaded.{' '}
              <button
                type="button"
                data-testid={`stored-fields-rows-retry-${table.tableName}`}
                onClick={() => {
                  for (const query of queries) {
                    if (query.isError) void query.refetch()
                  }
                }}
                className="underline"
              >
                Retry
              </button>
            </div>
          </td>
        </tr>
      )}
      {anyPending && rows.length > 0 && (
        <tr>
          <td colSpan={columnCount}>
            <div
              data-testid={`stored-fields-loading-rows-${table.tableName}`}
              role="status"
              aria-live="polite"
              className="px-2.5 py-2 text-xs text-tertiary-foreground"
            >
              Loading rows…
            </div>
          </td>
        </tr>
      )}
      {!anyPending && !laterPageFailed && totalRows > rows.length && (
        <tr>
          <td colSpan={columnCount}>
            <button
              type="button"
              data-testid={`stored-fields-more-${table.tableName}`}
              onClick={onLoadMore}
              className="px-2.5 py-2 text-xs text-muted-foreground underline"
            >
              Load more rows ({totalRows - rows.length} more)
            </button>
          </td>
        </tr>
      )}
    </>
  )
}

/**
 * The panel carries its own query client so it mounts anywhere — including
 * characterization harnesses that render the dashboard with no ambient
 * provider — instead of throwing `No QueryClient set`. The client is owned
 * per panel instance, so one panel's cached view never leaks into another's.
 */
export function StoredFieldsPanel({ sessionKey, className }: StoredFieldsPanelProps) {
  const [client] = useState(
    () => new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } }),
  )
  const rootClass = cn('rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs', className)

  if (sessionKey === undefined || sessionKey === null || sessionKey === '') {
    return (
      <div data-testid="stored-fields-panel" className={rootClass}>
        <div data-testid="stored-fields-no-session" className="text-xs text-tertiary-foreground">
          No session key — stored fields are not addressable.
        </div>
      </div>
    )
  }

  return (
    <QueryClientProvider client={client}>
      <StoredFieldsLoader sessionKey={sessionKey} className={rootClass} />
    </QueryClientProvider>
  )
}

function StoredFieldsLoader({ sessionKey, className }: { sessionKey: string; className?: string }) {
  const { data, isError, error } = useQuery({
    queryKey: ['kyber-stored-fields', sessionKey],
    queryFn: () => fetchStoredFields(sessionKey),
    retry: false,
  })

  // SSR and a still-in-flight observer both arrive here with no data and no
  // error; isLoading is false on the server when fetchStatus is idle.
  if (!isError && data === undefined) {
    return (
      <div data-testid="stored-fields-panel" className={className}>
        <div data-testid="stored-fields-loading" role="status" aria-live="polite" className="text-xs text-tertiary-foreground">
          Loading stored fields…
        </div>
      </div>
    )
  }

  if (isError || data === undefined) {
    return (
      <div data-testid="stored-fields-panel" className={className}>
        {isNotFoundError(error) ? (
          <div data-testid="stored-fields-not-found" role="status" aria-live="polite" className="text-xs text-tertiary-foreground">
            No stored fields were projected for this session.
          </div>
        ) : (
          <div data-testid="stored-fields-error" role="status" aria-live="polite" className="text-xs text-tertiary-foreground">
            Stored fields could not be loaded.
          </div>
        )}
      </div>
    )
  }

  const tables = Array.isArray(data.tables) ? data.tables : []

  return (
    <div data-testid="stored-fields-panel" className={cn(className, 'space-y-4')}>
      <div>
        <h2 className="text-xs font-semibold uppercase tracking-wider text-heading">Stored Fields</h2>
        <p className="mt-0.5 text-xs text-muted-foreground">
          What the relational store persisted for this session, column by column. Rows load per
          table when opened; absent figures are reported as such — never as zero or empty.
        </p>
      </div>
      {tables.length === 0 ? (
        <div data-testid="stored-fields-no-tables" className="text-xs text-tertiary-foreground">
          No stored fields were projected for this session.
        </div>
      ) : (
        tables.map((table) => <StoredTableSection key={table.tableName} sessionKey={sessionKey} table={table} />)
      )}
    </div>
  )
}
