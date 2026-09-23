

export type QuarantineEntry = {
  spanId: string
  namespaces: string[]
  reason: string
}

export function QuarantineView({ entries }: { entries: QuarantineEntry[] | null | undefined }) {
  if (!entries || entries.length === 0) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No quarantined spans.</div>
  }

  return (
    <div className="rounded-md border border-border bg-card p-4">
      <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">
        Quarantine — {entries.length} span{entries.length === 1 ? '' : 's'}
      </h3>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr>
              <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Span ID</th>
              <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Namespaces</th>
              <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Reason</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((e) => (
              <tr key={e.spanId} className="border-t border-border" data-testid="quarantine-row">
                <td className="py-2 font-mono text-xs">{e.spanId}</td>
                <td className="py-2 font-mono text-xs">{e.namespaces.join(', ')}</td>
                <td className="py-2 text-xs">{e.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="mt-3 text-xs text-tertiary-foreground">
        Counts and namespaces needed to write the missing adapter (R6.3).
      </p>
    </div>
  )
}
