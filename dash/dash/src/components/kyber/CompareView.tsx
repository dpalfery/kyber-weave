
import { NOT_MEASURABLE } from './NotMeasurable'

export type MetricCell = {
  measurable: boolean
  availability: string
  value?: number
  basis?: string
  currency?: string
  render: string
}

export type MetricRow = {
  metric: string
  kind: 'per_turn' | 'total'
  label: string
  unit: string
  cells: Record<string, MetricCell>
}

export type ComparisonTable = {
  harnesses: string[]
  rows: MetricRow[]
  problems: Array<{ severity: string; code: string; message: string }>
}

export function CompareView({ table }: { table: ComparisonTable | null | undefined }) {
  if (!table) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No comparison data.</div>
  }

  const perTurnRows = table.rows.filter((r) => r.kind === 'per_turn')
  const totalRows = table.rows.filter((r) => r.kind === 'total')

  const renderCell = (cell: MetricCell | undefined) => {
    if (!cell) return <span className="text-tertiary-foreground">—</span>
    if (!cell.measurable) {
      // R10.2: never zero, the pinned phrase
      return (
        <span data-testid="not-measurable" className="text-tertiary-foreground">
          {NOT_MEASURABLE}
        </span>
      )
    }
    // Include derived caveat inline when availability is derived
    const isDerived = cell.availability === 'derived'
    return (
      <span className="tabular-nums">
        {cell.render}
        {isDerived && cell.value !== undefined && (
          <span className="ml-1 text-xs text-tertiary-foreground" data-testid="derived-caveat">
            lower bound
          </span>
        )}
      </span>
    )
  }

  const TableBlock = ({ rows, title }: { rows: MetricRow[]; title: string }) => (
    <div className="rounded-md border border-border bg-card p-4">
      <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">{title}</h3>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr>
              <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Metric</th>
              {table.harnesses.map((h) => (
                <th key={h} className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.metric} className="border-t border-border">
                <td className="py-2 pr-4">
                  <div className="font-medium">{row.label}</div>
                  <div className="text-xs text-tertiary-foreground">{row.unit}</div>
                </td>
                {table.harnesses.map((h) => (
                  <td key={h} className="py-2 text-right">
                    {renderCell(row.cells[h])}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )

  return (
    <div className="flex flex-col gap-4">
      <TableBlock rows={perTurnRows} title="Per-turn ratios (lead)" />
      <TableBlock rows={totalRows} title="Totals (trail)" />
      {table.problems.length > 0 && (
        <div className="rounded-md border border-amber-300 bg-amber-50 p-4 dark:border-amber-700 dark:bg-amber-950/30">
          <h3 className="mb-2 text-xs font-semibold uppercase tracking-wider text-amber-800 dark:text-amber-200">Problems</h3>
          <ul className="flex list-disc flex-col gap-1 pl-5 text-sm text-amber-900 dark:text-amber-100">
            {table.problems.map((p, i) => (
              <li key={i}>
                [{p.code}] {p.message}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
