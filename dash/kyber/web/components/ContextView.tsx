import * as React from 'react'
import { NotMeasurable } from './NotMeasurable.js'
import { DerivedCaveat, DEFAULT_DERIVED_MODEL } from './DerivedCaveat.js'

export type ContextBucketKey =
  | 'system_prompt'
  | 'tool_definitions'
  | 'instruction_context'
  | 'conversation_history'
  | 'tool_result_content'

export type TurnPressure = {
  index: number
  buckets: Record<ContextBucketKey, number>
  toolDefinitionsByServer: Map<string, number> | Record<string, number>
  builtinToolDefinitionTokens: number
  strippedInstructionBlocks: { count: number; tokens: number }
  bucketedTokens: number
  residual: { tokens: number; attribution: 'tokenizer_drift' | 'unattributed' }
  headroom: number
  pressure: number
  accumulationRate: number
  freshInput: number
  freshInputJump?: { previous: number; factor: number }
}

export type ContextAnalysis =
  | {
      measurable: true
      contextLimit: number
      turns: TurnPressure[]
      residualTotal: number
      derivedCounts: boolean
      freshJumpFactor: number
      flaggedTurns: number[]
      sessionAccumulationRate: number
      /** Model name when derivedCounts true, for caveat rendering */
      derivedModel?: string
    }
  | {
      measurable: false
      reason: 'no_message_structure' | 'declared_not_measurable'
      turns: number
      contextLimit: number
    }

function fmtTokens(n: number): string {
  if (n >= 1e9) return (n / 1e9).toFixed(2) + 'B'
  if (n >= 1e6) return (n / 1e6).toFixed(1) + 'M'
  if (n >= 1e3) return (n / 1e3).toFixed(1) + 'K'
  return String(Math.round(n))
}

const BUCKET_LABELS: Record<ContextBucketKey, string> = {
  system_prompt: 'System prompt',
  tool_definitions: 'Tool definitions',
  instruction_context: 'Instruction context',
  conversation_history: 'Conversation history',
  tool_result_content: 'Tool results',
}

const BUCKET_COLORS: Record<ContextBucketKey, string> = {
  system_prompt: 'bg-chart-1',
  tool_definitions: 'bg-chart-2',
  instruction_context: 'bg-chart-3',
  conversation_history: 'bg-chart-4',
  tool_result_content: 'bg-chart-5',
}

function Heatmap({ turns }: { turns: TurnPressure[] }) {
  const keys: ContextBucketKey[] = [
    'system_prompt',
    'tool_definitions',
    'instruction_context',
    'conversation_history',
    'tool_result_content',
  ]
  const max = Math.max(1, ...turns.flatMap((t) => keys.map((k) => t.buckets[k])))

  return (
    <div data-testid="context-heatmap" className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead>
          <tr>
            <th className="py-1 pr-2 text-left text-[11px] font-medium text-tertiary-foreground">Turn</th>
            {keys.map((k) => (
              <th key={k} className="px-1 py-1 text-center text-[11px] font-medium text-tertiary-foreground">
                {BUCKET_LABELS[k]}
              </th>
            ))}
            <th className="px-1 py-1 text-center text-[11px] font-medium text-tertiary-foreground">Pressure</th>
          </tr>
        </thead>
        <tbody>
          {turns.map((t) => (
            <tr key={t.index} className="border-t border-border">
              <td className="py-1.5 pr-2 tabular-nums">{t.index}</td>
              {keys.map((k) => {
                const v = t.buckets[k]
                const intensity = v / max
                return (
                  <td key={k} className="px-1 py-1 text-center">
                    <span
                      className="inline-block rounded px-1.5 py-0.5 tabular-nums text-foreground"
                      style={{
                        backgroundColor: `color-mix(in srgb, var(--chart-${keys.indexOf(k) + 1}) ${Math.round(intensity * 70 + 10)}%, transparent)`,
                      }}
                    >
                      {fmtTokens(v)}
                    </span>
                  </td>
                )
              })}
              <td className="px-1 py-1 text-center tabular-nums">{(t.pressure * 100).toFixed(1)}%</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function ContextView({ analysis }: { analysis: ContextAnalysis | null | undefined }) {
  if (!analysis) {
    return (
      <div className="py-8 text-center text-sm text-tertiary-foreground">No context data.</div>
    )
  }

  if (!analysis.measurable) {
    return (
      <div className="rounded-md border border-border bg-card px-4 py-6">
        <div className="text-sm">
          <NotMeasurable reason={analysis.reason === 'declared_not_measurable' ? 'declared not measurable' : 'no message structure'} />
        </div>
        <p className="mt-2 text-xs text-tertiary-foreground">
          Context composition is not measurable for this source — it cannot supply message structure.
        </p>
      </div>
    )
  }

  const derivedModel = analysis.derivedModel ?? DEFAULT_DERIVED_MODEL
  const totalInput = analysis.turns.reduce((s, t) => s + t.bucketedTokens + t.residual.tokens, 0)

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2 text-xs text-tertiary-foreground">
        <span>
          Window: {fmtTokens(analysis.contextLimit)} tokens
        </span>
        <span>·</span>
        <span>
          Residual: {fmtTokens(analysis.residualTotal)} ({analysis.turns[0]?.residual.attribution})
        </span>
        {analysis.derivedCounts && (
          <>
            <span>·</span>
            <DerivedCaveat model={derivedModel} />
          </>
        )}
        {analysis.flaggedTurns.length > 0 && (
          <>
            <span>·</span>
            <span className="text-amber-600">Flagged turns: {analysis.flaggedTurns.join(', ')}</span>
          </>
        )}
      </div>

      {/* Bucket totals */}
      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">Buckets</h3>
        <div className="flex flex-col gap-2">
          {(
            [
              'system_prompt',
              'tool_definitions',
              'instruction_context',
              'conversation_history',
              'tool_result_content',
            ] as ContextBucketKey[]
          ).map((k) => {
            const total = analysis.turns.reduce((s, t) => s + t.buckets[k], 0)
            return (
              <div key={k} className="flex items-center gap-3 text-sm">
                <span className="w-40 shrink-0 text-tertiary-foreground">{BUCKET_LABELS[k]}</span>
                <div className="h-2 flex-1 overflow-hidden rounded-full bg-interactive-secondary">
                  <div
                    className={`h-full ${BUCKET_COLORS[k]}`}
                    style={{ width: `${Math.max(2, (total / Math.max(1, totalInput)) * 100)}%` }}
                  />
                </div>
                <span className="w-20 shrink-0 text-right tabular-nums">
                  {fmtTokens(total)}
                  {analysis.derivedCounts && <span className="ml-1 text-xs text-tertiary-foreground">*</span>}
                </span>
              </div>
            )
          })}
        </div>
        {analysis.derivedCounts && (
          <p className="mt-3 text-xs text-tertiary-foreground" data-testid="derived-caveat">
            * lower bound (model: {derivedModel})
          </p>
        )}
      </div>

      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">Heatmap</h3>
        <Heatmap turns={analysis.turns} />
      </div>

      <div className="rounded-md border border-border bg-card p-4">
        <h3 className="mb-3 text-xs font-semibold uppercase tracking-wider text-heading">Turns</h3>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Turn</th>
                <th className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">Input</th>
                <th className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">Headroom</th>
                <th className="pb-2 text-right text-[11px] font-medium text-tertiary-foreground">Accumulation</th>
                <th className="pb-2 text-left text-[11px] font-medium text-tertiary-foreground">Residual</th>
              </tr>
            </thead>
            <tbody>
              {analysis.turns.map((t) => (
                <tr key={t.index} className="border-t border-border">
                  <td className="py-2 tabular-nums">
                    {t.index}
                    {t.freshInputJump && <span className="ml-1 text-amber-600">⚡</span>}
                  </td>
                  <td className="py-2 text-right tabular-nums">
                    {fmtTokens(t.bucketedTokens + t.residual.tokens)}
                    {analysis.derivedCounts && <span className="text-xs text-tertiary-foreground"> *</span>}
                  </td>
                  <td className="py-2 text-right tabular-nums">{fmtTokens(t.headroom)}</td>
                  <td className="py-2 text-right tabular-nums">{fmtTokens(t.accumulationRate)}</td>
                  <td className="py-2 tabular-nums">
                    {fmtTokens(t.residual.tokens)}
                    <span className="ml-1 text-xs text-tertiary-foreground">({t.residual.attribution})</span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  )
}
