import * as React from 'react'
import { NotMeasurable } from './NotMeasurable.js'
import { DEFAULT_DERIVED_MODEL } from './DerivedCaveat.js'

export type RankedTool = {
  name: string
  server?: string
  cost: number
  invoked: boolean
}

export type UnusedSchemaRange = {
  tokenResidencies: number
  floor: number
  ceiling: number
  currency?: string
}

export type SchemaCostAnalysis =
  | {
      measurable: true
      ranked: RankedTool[]
      neverInvoked: RankedTool[]
      byServer: Map<string, number> | Record<string, number>
      unusedRange: UnusedSchemaRange
      turns: number
      /** When true, tokenResidencies are derived via o200k_base */
      derived?: boolean
      derivedModel?: string
    }
  | {
      measurable: false
      invocationCount: number
      reason?: 'declared_not_measurable'
    }

function fmtTokens(n: number): string {
  if (n >= 1e9) return (n / 1e9).toFixed(2) + 'B'
  if (n >= 1e6) return (n / 1e6).toFixed(1) + 'M'
  if (n >= 1e3) return (n / 1e3).toFixed(1) + 'K'
  return String(Math.round(n))
}

function fmtCurrency(n: number, currency = 'USD'): string {
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(n)
  } catch {
    return `$${n.toFixed(2)}`
  }
}

export function SchemaView({ analysis }: { analysis: SchemaCostAnalysis | null | undefined }) {
  if (!analysis) {
    return <div className="py-8 text-center text-sm text-tertiary-foreground">No schema data.</div>
  }

  if (!analysis.measurable) {
    return (
      <div className="rounded-md border border-border bg-card px-4 py-6">
        <div className="text-sm">
          <NotMeasurable reason={analysis.reason ?? `invocations: ${analysis.invocationCount}`} />
        </div>
        <p className="mt-2 text-xs text-tertiary-foreground">
          Schema cost ranking is not measurable — the source reports invocations but no definitions.
        </p>
      </div>
    )
  }

  const byServerEntries =
    analysis.byServer instanceof Map
      ? [...analysis.byServer.entries()]
      : Object.entries(analysis.byServer as Record<string, number>)
  const derivedModel = analysis.derivedModel ?? DEFAULT_DERIVED_MODEL
  const isDerived = analysis.derived ?? true // schema tokens are always derived (R4.6)

  return (
    <div className="flex flex-col gap-4">
      {isDerived && (
        <div className="text-xs text-tertiary-foreground" data-testid="derived-caveat">
          Token counts are lower bound (model: {derivedModel})
        </div>
      )}

      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">
          Ranking by resident cost
        </h3>
        {analysis.ranked.length === 0 ? (
          <p className="py-4 text-center text-sm text-tertiary-foreground">No tools offered.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Tool</th>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Server</th>
                <th className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">Cost</th>
                <th className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">Invoked</th>
              </tr>
            </thead>
            <tbody>
              {analysis.ranked.map((t) => (
                <tr key={`${t.server ?? 'builtin'}:${t.name}`} className="border-t border-border">
                  <td className="py-2 font-mono text-xs">{t.name}</td>
                  <td className="py-2 text-xs text-tertiary-foreground">{t.server ?? '— builtin'}</td>
                  <td className="py-2 text-right tabular-nums">
                    {fmtTokens(t.cost)}
                    {isDerived && <span className="ml-1 text-xs text-tertiary-foreground">*</span>}
                  </td>
                  <td className="py-2 text-right">{t.invoked ? 'yes' : 'no'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">Never invoked</h3>
        {analysis.neverInvoked.length === 0 ? (
          <p className="text-sm text-tertiary-foreground">All offered tools were invoked.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Tool</th>
                <th className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">Cost</th>
              </tr>
            </thead>
            <tbody>
              {analysis.neverInvoked.map((t) => (
                <tr key={t.name} className="border-t border-border">
                  <td className="py-2 font-mono text-xs">{t.name}</td>
                  <td className="py-2 text-right tabular-nums">
                    {fmtTokens(t.cost)} {isDerived && <span className="text-xs text-tertiary-foreground">*</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">By MCP server</h3>
        {byServerEntries.length === 0 ? (
          <p className="text-sm text-tertiary-foreground">No server grouping.</p>
        ) : (
          <div className="flex flex-col gap-2">
            {byServerEntries.map(([server, cost]) => (
              <div key={server} className="flex items-center justify-between text-sm">
                <span className="font-mono text-xs">{server}</span>
                <span className="tabular-nums">
                  {fmtTokens(cost as number)}
                  {isDerived && <span className="ml-1 text-xs text-tertiary-foreground">*</span>}
                </span>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">Unused range</h3>
        <div className="flex flex-col gap-1 text-sm">
          <div className="flex justify-between">
            <span className="text-tertiary-foreground">Token residencies</span>
            <span className="tabular-nums" data-testid="token-residencies">
              {fmtTokens(analysis.unusedRange.tokenResidencies)}{' '}
              {isDerived && <span className="text-xs text-tertiary-foreground">lower bound (model: {derivedModel})</span>}
            </span>
          </div>
          <div className="flex justify-between">
            <span className="text-tertiary-foreground">Floor (cache-read)</span>
            <span className="tabular-nums">{analysis.unusedRange.currency ? fmtCurrency(analysis.unusedRange.floor, analysis.unusedRange.currency) : fmtTokens(analysis.unusedRange.floor)}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-tertiary-foreground">Ceiling (fresh)</span>
            <span className="tabular-nums">{analysis.unusedRange.currency ? fmtCurrency(analysis.unusedRange.ceiling, analysis.unusedRange.currency) : fmtTokens(analysis.unusedRange.ceiling)}</span>
          </div>
        </div>
      </div>
    </div>
  )
}
