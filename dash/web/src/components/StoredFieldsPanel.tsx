import { useState } from 'react'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { cn } from '../lib/utils.js'
import { fetchStoredFields, isNotFoundError, type StoredFieldCell, type StoredTableFields } from '../lib/kyberApi.js'

/**
 * Stored-fields inspection. The engine decides what the relational store
 * persisted; this panel only lays the catalog-driven view out. Every
 * reachable column renders labeled `table.column`; a row the store never
 * held renders as "not reported" — never as `0` or `""`.
 */
export type StoredFieldsPanelProps = {
  /** The inspected session's key. Absent means the view is not addressable. */
  sessionKey?: string | null
  className?: string
}

/**
 * Formats one stored cell for display. A measured figure — including a
 * measured `0` — renders verbatim; a cell the store never measured resolves
 * to `undefined` so the caller prints "not reported" instead of `0` or `""`.
 */
export function formatStoredFieldValue(cell: StoredFieldCell): string | undefined {
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

function StoredTableSection({ table }: { table: StoredTableFields }) {
  const rows = Array.isArray(table.rows) ? table.rows : []
  const columns = Array.isArray(table.columnNames) ? table.columnNames : []
  const missing = Array.isArray(table.missingParentKeys) ? table.missingParentKeys : []

  return (
    <section data-testid={`stored-fields-table-${table.tableName}`} className="space-y-2">
      <div className="flex flex-wrap items-baseline gap-2">
        <h3 className="font-mono text-xs font-semibold text-foreground">{table.tableName}</h3>
        <span className="text-[11px] text-tertiary-foreground">
          {rows.length === 0
            ? 'no rows stored'
            : `${rows.length} row${rows.length === 1 ? '' : 's'}`}
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
          </tbody>
        </table>
      </div>

      {rows.length === 0 && (
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
        <div data-testid="stored-fields-loading" className="text-xs text-tertiary-foreground">
          Loading stored fields…
        </div>
      </div>
    )
  }

  if (isError || data === undefined) {
    return (
      <div data-testid="stored-fields-panel" className={className}>
        {isNotFoundError(error) ? (
          <div data-testid="stored-fields-not-found" className="text-xs text-tertiary-foreground">
            No stored fields were projected for this session.
          </div>
        ) : (
          <div data-testid="stored-fields-error" className="text-xs text-tertiary-foreground">
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
          What the relational store persisted for this session, column by column. Absent rows are
          reported as such — never as zero or empty.
        </p>
      </div>
      {tables.length === 0 ? (
        <div data-testid="stored-fields-no-tables" className="text-xs text-tertiary-foreground">
          No stored fields were projected for this session.
        </div>
      ) : (
        tables.map((table) => <StoredTableSection key={table.tableName} table={table} />)
      )}
    </div>
  )
}
