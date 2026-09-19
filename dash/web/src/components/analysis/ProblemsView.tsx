

export type ProblemEntry = {
  severity: 'error' | 'warning'
  code: string
  message: string
  location?: string
  spanId?: string
}

export function ProblemsView({ problems }: { problems: ProblemEntry[] | null | undefined }) {
  if (!problems || problems.length === 0) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No problems.</div>
  }

  return (
    <div className="rounded-md border border-border bg-card p-4">
      <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">
        Problems — {problems.length}
      </h3>
      <div className="flex flex-col gap-2">
        {problems.map((p, i) => (
          <div
            key={i}
            data-testid="problem-row"
            className={`rounded border px-3 py-2 text-sm ${p.severity === 'error' ? 'border-red-200 bg-red-50 dark:border-red-800 dark:bg-red-950/30' : 'border-amber-200 bg-amber-50 dark:border-amber-800 dark:bg-amber-950/30'}`}
          >
            <div className="flex items-center gap-2">
              <span
                className={`rounded px-1.5 py-0.5 text-[10px] font-medium uppercase ${p.severity === 'error' ? 'bg-red-200 text-red-800 dark:bg-red-800 dark:text-red-100' : 'bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-100'}`}
              >
                {p.severity}
              </span>
              <span className="font-mono text-xs font-medium">{p.code}</span>
              {p.location && <span className="text-xs text-tertiary-foreground">at {p.location}</span>}
              {p.spanId && <span className="font-mono text-xs text-tertiary-foreground">{p.spanId}</span>}
            </div>
            <p className="mt-1 text-xs">{p.message}</p>
          </div>
        ))}
      </div>
    </div>
  )
}
