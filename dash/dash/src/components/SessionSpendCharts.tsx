import { useMemo, useState } from 'react'

import { cn, fmtNum, fmtTokens } from '../lib/utils'

// ---------------------------------------------------------------------------
// Types & Contracts
// ---------------------------------------------------------------------------

export type SpendTokenClass =
  | 'fresh_input'
  | 'cache_read'
  | 'cache_creation'
  | 'output'
  | 'reasoning'

export type ContextBucketKey =
  | 'system_prompt'
  | 'instruction_context'
  | 'tool_definitions'
  | 'conversation_history'
  | 'tool_result_content'
  | 'residual'

export interface TurnSpendItem {
  index: number
  spanId?: string
  timestamp?: string
  durationMs?: number
  model?: string
  input?: number
  output?: number
  cache_read?: number
  cache_creation?: number | null
  fresh?: number
  fresh_input?: number
  reasoning?: number | null
  visible_output?: number
  cumulative_input?: number
  fresh_jump_pct?: number | null
  request_start?: string | null
  reported_usd?: number | null
  usd?: number | null
  credits?: number | null
  cost_status?: string
}

export interface ContextTurnItem {
  index?: number
  turn?: number
  label?: string
  reported_input?: number
  bucketedTokens?: number
  buckets?: Partial<Record<ContextBucketKey | string, number | null>>
  residual?: number | { tokens: number; attribution?: string }
  headroom?: number
  pressure?: number
  accumulationRate?: number
}

export interface ContextCompositionData {
  measurable?: boolean
  reason?: 'no_message_structure' | 'declared_not_measurable' | string
  contextLimit?: number
  turns?: ContextTurnItem[]
  first?: {
    turn: number
    buckets: Record<string, number | null>
    reported_input?: number
  }
  last?: {
    turn: number
    buckets: Record<string, number | null>
    reported_input?: number
  } | '__single__'
  residualTotal?: number
  derivedCounts?: boolean
  derivedModel?: string
  freshJumpFactor?: number
  flaggedTurns?: number[]
  sessionAccumulationRate?: number
}

export interface TurnSpendChartProps {
  turns?: TurnSpendItem[]
  selectedTurnIndex?: number | null
  onSelectTurn?: (turnIndex: number) => void
  className?: string
}

export interface ContextCompositionChartProps {
  context?: ContextCompositionData | null
  turns?: TurnSpendItem[]
  selectedTurnIndex?: number | null
  selectedBucket?: ContextBucketKey | string | null
  onSelectTurn?: (turnIndex: number, bucketName?: ContextBucketKey | string) => void
  className?: string
}

export interface SessionSpendChartsProps {
  session?: any
  turns?: TurnSpendItem[]
  context?: ContextCompositionData | null
  selectedTurnIndex?: number | null
  selectedBucket?: ContextBucketKey | string | null
  onSelectTurn?: (turnIndex: number, bucketName?: ContextBucketKey | string) => void
  className?: string
}

// ---------------------------------------------------------------------------
// Design Tokens & Labels
// ---------------------------------------------------------------------------

export const SPEND_COLORS: Record<SpendTokenClass, string> = {
  cache_read: 'var(--chart-2)', // Mint / teal
  cache_creation: 'var(--chart-6)', // Blue
  fresh_input: 'var(--chart-4)', // Amber / Gold
  output: 'var(--chart-5)', // Terracotta / Coral
  reasoning: '#8b5cf6', // Purple
}

export const SPEND_LABELS: Record<SpendTokenClass, string> = {
  cache_read: 'Cache-read input',
  cache_creation: 'Cache-creation input',
  fresh_input: 'Fresh input',
  output: 'Output tokens',
  reasoning: 'Reasoning output',
}

export const CONTEXT_BUCKET_KEYS: ContextBucketKey[] = [
  'system_prompt',
  'instruction_context',
  'tool_definitions',
  'conversation_history',
  'tool_result_content',
  'residual',
]

export const CONTEXT_BUCKET_LABELS: Record<ContextBucketKey, string> = {
  system_prompt: 'System prompt',
  instruction_context: 'Instruction context',
  tool_definitions: 'Tool definitions',
  conversation_history: 'Conversation history',
  tool_result_content: 'Tool results',
  residual: 'Residual (drift / framing)',
}

export const CONTEXT_BUCKET_COLORS: Record<ContextBucketKey, string> = {
  system_prompt: 'var(--chart-1)', // Forest Green
  instruction_context: 'var(--chart-3)', // Deep Teal
  tool_definitions: 'var(--chart-2)', // Mint
  conversation_history: 'var(--chart-4)', // Gold / Amber
  tool_result_content: 'var(--chart-6)', // Blue
  residual: 'var(--chart-5)', // Terracotta
}

/**
 * Colour ramp for per-MCP-server tool-definition bands. Servers are not a
 * fixed set, so they cycle a ramp rather than getting named tokens. Ordering
 * is by descending token cost, so the same server keeps its colour within a
 * session even as the ramp repeats across many servers.
 */
export const MCP_SERVER_COLORS = [
  'var(--chart-7)',
  'var(--chart-8)',
  'var(--chart-9)',
  'var(--chart-10)',
  'var(--chart-2)',
  'var(--chart-6)',
]

/** One drawn band: a whole bucket, or one server's slice of tool definitions. */
export type ContextSegment = {
  /** The bucket this segment reports against, for selection and inspection. */
  key: ContextBucketKey
  /** Stable identity for React keys and test ids. */
  id: string
  label: string
  tokens: number
  color: string
  /** Ground-truth MCP server, when this segment is one server's definitions. */
  server?: string
}

/**
 * Expand a turn's buckets into the bands to draw.
 *
 * `tool_definitions` splits into one band per MCP server whenever the turn
 * carries ground-truth per-server attribution, plus a band for the harness's
 * built-in tools. That split is the whole reason this data is worth
 * collecting: a single "Tool definitions: 40k" bar says the schemas are
 * expensive, while a stack of per-server bands says WHICH server to
 * disconnect. Servers with no attribution are never invented - a tool whose
 * definition named no server is counted as built-in.
 */
export function contextSegments(row: {
  buckets: Record<ContextBucketKey, number>
  servers?: Record<string, number>
  builtinToolTokens?: number
}): ContextSegment[] {
  const segments: ContextSegment[] = []

  for (const key of CONTEXT_BUCKET_KEYS) {
    const value = row.buckets[key]

    if (key === 'tool_definitions') {
      const servers = Object.entries(row.servers ?? {})
        .filter(([, tokens]) => tokens > 0)
        .sort((a, b) => b[1] - a[1])

      if (servers.length > 0) {
        servers.forEach(([server, tokens], index) => {
          segments.push({
            key,
            id: `mcp:${server}`,
            label: `mcp: ${server}`,
            tokens,
            color: MCP_SERVER_COLORS[index % MCP_SERVER_COLORS.length]!,
            server,
          })
        })
        const builtin = row.builtinToolTokens ?? Math.max(0, value - servers.reduce((sum, [, t]) => sum + t, 0))
        if (builtin > 0) {
          segments.push({
            key,
            id: 'tool_definitions:builtin',
            label: 'Built-in tools',
            tokens: builtin,
            color: CONTEXT_BUCKET_COLORS[key],
          })
        }
        continue
      }
    }

    if (!value || value <= 0) continue
    segments.push({
      key,
      id: key,
      label: CONTEXT_BUCKET_LABELS[key],
      tokens: value,
      color: CONTEXT_BUCKET_COLORS[key],
    })
  }

  return segments
}

// ---------------------------------------------------------------------------
// Helper Normalizers
// ---------------------------------------------------------------------------

export function normalizeContextBuckets(
  rawBuckets: Record<string, number | null | undefined> = {},
  reportedInput?: number,
): Record<ContextBucketKey, number> {
  const result: Record<ContextBucketKey, number> = {
    system_prompt: 0,
    instruction_context: 0,
    tool_definitions: 0,
    conversation_history: 0,
    tool_result_content: 0,
    residual: 0,
  }

  for (const [rawKey, rawVal] of Object.entries(rawBuckets)) {
    if (rawVal === null || rawVal === undefined || Number.isNaN(rawVal) || rawVal <= 0) continue
    const key = rawKey.toLowerCase().trim()
    if (key === 'system_prompt' || key.includes('system prompt') || key.includes('system instructions')) {
      result.system_prompt += rawVal
    } else if (
      key === 'instruction_context' ||
      key.includes('instruction') ||
      key.includes('workspace') ||
      key.includes('skill')
    ) {
      result.instruction_context += rawVal
    } else if (
      key === 'tool_definitions' ||
      key.includes('tool def') ||
      key.includes('built-in tools') ||
      key.startsWith('mcp:') ||
      key === 'tools'
    ) {
      result.tool_definitions += rawVal
    } else if (
      key === 'conversation_history' ||
      key.includes('conversation') ||
      key.includes('history')
    ) {
      result.conversation_history += rawVal
    } else if (
      key === 'tool_result_content' ||
      key.includes('tool result') ||
      key.includes('tool_result') ||
      key.includes('file contents')
    ) {
      result.tool_result_content += rawVal
    } else if (key === 'residual' || key.includes('unattributed') || key.includes('drift')) {
      result.residual += rawVal
    } else {
      result.residual += rawVal
    }
  }

  // Account for unattributed residual when reported input exceeds sum of known buckets
  if (reportedInput && reportedInput > 0) {
    const known =
      result.system_prompt +
      result.instruction_context +
      result.tool_definitions +
      result.conversation_history +
      result.tool_result_content
    result.residual = Math.max(result.residual, reportedInput - known)
  }

  return result
}

export function extractNormalizedContextTurns(context?: ContextCompositionData | null): Array<{
  turnIndex: number
  label: string
  reportedInput: number
  buckets: Record<ContextBucketKey, number>
  total: number
  pressure?: number
  headroom?: number
  /** Ground-truth per-MCP-server tool-definition tokens, when attributed. */
  servers?: Record<string, number>
  /** Tool-definition tokens belonging to no server (the harness's built-ins). */
  builtinToolTokens?: number
}> {
  if (!context || context.measurable === false) return []

  if (Array.isArray(context.turns) && context.turns.length > 0) {
    return context.turns.map((t, i) => {
      const idx = t.index ?? t.turn ?? i + 1
      const rawBuckets = t.buckets ?? {}
      const rawResidual = typeof t.residual === 'number' ? t.residual : t.residual?.tokens
      const residualTokens = rawResidual ?? (rawBuckets.residual as number | undefined) ?? 0
      const known = Object.entries(rawBuckets).reduce((sum, [k, v]) => {
        if (!v || v <= 0) return sum
        const lk = k.toLowerCase().trim()
        if (lk === 'residual' || lk.includes('unattributed') || lk.includes('drift')) return sum
        return sum + v
      }, 0)
      const fallbackReported = (known + residualTokens) > 0 ? (known + residualTokens) : undefined
      const reported = t.reported_input ?? t.bucketedTokens ?? fallbackReported
      const buckets = normalizeContextBuckets(t.buckets ?? {}, reported)
      const total = Object.values(buckets).reduce((sum, v) => sum + v, 0)
      // The analysis emits `toolDefinitionsByServer` as a Map, which survives
      // the API as a plain object. Either shape is accepted; anything else is
      // treated as absent rather than coerced.
      const rawServers = (t as { toolDefinitionsByServer?: unknown }).toolDefinitionsByServer
      const servers =
        rawServers instanceof Map
          ? Object.fromEntries(rawServers)
          : rawServers !== null && typeof rawServers === 'object'
            ? (rawServers as Record<string, number>)
            : undefined
      const builtinToolTokens = (t as { builtinToolDefinitionTokens?: number }).builtinToolDefinitionTokens

      return {
        turnIndex: idx,
        label: `Turn #${idx}`,
        reportedInput: reported != null && reported > 0 ? reported : total,
        buckets,
        total: Math.max(total, reported ?? 0),
        pressure: t.pressure,
        headroom: t.headroom,
        ...(servers !== undefined && Object.keys(servers).length > 0 ? { servers } : {}),
        ...(builtinToolTokens !== undefined ? { builtinToolTokens } : {}),
      }
    })
  }

  const rows: Array<{
    turnIndex: number
    label: string
    reportedInput: number
    buckets: Record<ContextBucketKey, number>
    total: number
  }> = []

  if (context.first) {
    const reported = context.first.reported_input || 0
    const buckets = normalizeContextBuckets(context.first.buckets, reported)
    const total = Object.values(buckets).reduce((sum, v) => sum + v, 0)
    rows.push({
      turnIndex: context.first.turn || 1,
      label: `First turn (#${context.first.turn || 1})`,
      reportedInput: Math.max(reported, total),
      buckets,
      total: Math.max(reported, total),
    })
  }

  if (context.last && context.last !== '__single__') {
    const reported = context.last.reported_input || 0
    const buckets = normalizeContextBuckets(context.last.buckets, reported)
    const total = Object.values(buckets).reduce((sum, v) => sum + v, 0)
    rows.push({
      turnIndex: context.last.turn,
      label: `Last turn (#${context.last.turn})`,
      reportedInput: Math.max(reported, total),
      buckets,
      total: Math.max(reported, total),
    })
  }

  return rows
}

// ---------------------------------------------------------------------------
// 1. Per-Turn Spend Chart (`TurnSpendChart`)
// ---------------------------------------------------------------------------

export function TurnSpendChart({
  turns,
  selectedTurnIndex,
  onSelectTurn,
  className,
}: TurnSpendChartProps) {
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null)

  const normalizedTurns = useMemo(() => {
    if (!turns || turns.length === 0) return []
    let runningInput = 0
    let prevFresh: number | null = null

    return turns.map((t, i) => {
      const idx = t.index ?? i + 1
      const cacheRead = Math.max(0, t.cache_read ?? 0)
      const cacheCreation = t.cache_creation != null ? Math.max(0, t.cache_creation) : 0
      const hasCc = t.cache_creation !== null && t.cache_creation !== undefined
      const output = Math.max(
        0,
        t.visible_output ??
          (t.output != null && t.reasoning != null ? Math.max(0, t.output - t.reasoning) : t.output) ??
          0,
      )
      const reasoning = Math.max(0, t.reasoning ?? 0)

      let fresh = t.fresh_input ?? t.fresh
      if (fresh == null) {
        fresh = Math.max(0, (t.input ?? 0) - cacheRead - cacheCreation)
      } else {
        fresh = Math.max(0, fresh)
      }

      runningInput += t.input ?? fresh + cacheRead + cacheCreation
      const cumulativeInput = t.cumulative_input ?? runningInput

      let freshJumpPct = t.fresh_jump_pct
      if (freshJumpPct == null && prevFresh != null && prevFresh > 0 && fresh > prevFresh) {
        const jump = Math.round(((fresh - prevFresh) / prevFresh) * 100)
        if (jump >= 25) freshJumpPct = jump
      }
      prevFresh = fresh

      const totalTokens = fresh + cacheRead + cacheCreation + output + reasoning

      return {
        turn: t,
        index: idx,
        fresh,
        cacheRead,
        cacheCreation,
        hasCc,
        output,
        reasoning,
        totalTokens,
        cumulativeInput,
        freshJumpPct,
        requestStart: t.request_start,
      }
    })
  }, [turns])

  if (!turns || turns.length === 0 || normalizedTurns.length === 0) {
    return (
      <div
        data-testid="turn-spend-empty"
        className={cn(
          'rounded-lg border border-border bg-card p-6 text-center text-card-foreground shadow-xs',
          className,
        )}
      >
        <p className="text-sm font-medium text-foreground">No agent turns recorded</p>
        <p className="mt-1 text-xs text-muted-foreground">
          This session does not contain any chat turns or token spend progression.
        </p>
      </div>
    )
  }

  // Summary aggregation
  const totalFresh = normalizedTurns.reduce((s, t) => s + t.fresh, 0)
  const totalCacheRead = normalizedTurns.reduce((s, t) => s + t.cacheRead, 0)
  const totalCacheCreation = normalizedTurns.reduce((s, t) => s + t.cacheCreation, 0)
  const totalOutput = normalizedTurns.reduce((s, t) => s + t.output, 0)
  const ccTurnCount = normalizedTurns.filter((t) => t.hasCc && t.cacheCreation > 0).length
  const hasRecordedCc = ccTurnCount > 0
  const inputSum = totalFresh + totalCacheRead + totalCacheCreation
  const cacheHitPercent = inputSum > 0 ? (totalCacheRead / inputSum) * 100 : 0

  // SVG coordinate configuration
  const turnCount = normalizedTurns.length
  const barSlotWidth = Math.max(48, Math.min(84, 1100 / Math.max(1, turnCount)))
  const chartWidth = Math.max(640, turnCount * barSlotWidth + 120)
  const chartHeight = 280
  const padLeft = 60
  const padRight = 60
  const padTop = 32
  const padBottom = 48
  const innerWidth = chartWidth - padLeft - padRight
  const innerHeight = chartHeight - padTop - padBottom

  const maxTurnTokens = Math.max(1, ...normalizedTurns.map((t) => t.totalTokens)) * 1.15
  const maxCumulative = Math.max(1, ...normalizedTurns.map((t) => t.cumulativeInput)) * 1.08
  const barWidth = Math.min(42, Math.max(16, (innerWidth / turnCount) * 0.65))

  // Cumulative line points
  const linePoints = normalizedTurns.map((t, i) => {
    const cx = padLeft + innerWidth * ((i + 0.5) / turnCount)
    const cy = padTop + innerHeight - (t.cumulativeInput / maxCumulative) * innerHeight
    return { cx, cy, turnIndex: t.index }
  })
  const linePath = linePoints.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.cx.toFixed(1)} ${p.cy.toFixed(1)}`).join(' ')

  const jumps = normalizedTurns.filter((t) => t.freshJumpPct != null && t.freshJumpPct >= 25)
  const activeHoverTurn =
    hoveredIndex != null ? normalizedTurns.find((t) => t.index === hoveredIndex) : null

  return (
    <div
      data-testid="turn-spend-chart"
      className={cn('rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs', className)}
    >
      {/* Header */}
      <div className="flex flex-wrap items-start justify-between gap-4 border-b border-border pb-3">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-sm font-semibold text-foreground">Token Spend per Turn</h3>
            <span className="rounded-full bg-interactive-secondary px-2 py-0.5 text-[11px] font-medium text-tertiary-foreground">
              {turnCount} {turnCount === 1 ? 'turn' : 'turns'}
            </span>
          </div>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Stacked token progression across turns. Dashed line: cumulative input context growth.
          </p>
        </div>

        {/* Quick totals chips */}
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <div className="rounded-md border border-border bg-interactive-secondary/60 px-2.5 py-1">
            <span className="text-tertiary-foreground">Cache Hit: </span>
            <span className="font-semibold tabular-nums text-foreground">{cacheHitPercent.toFixed(1)}%</span>
          </div>
          <div className="rounded-md border border-border bg-interactive-secondary/60 px-2.5 py-1">
            <span className="text-tertiary-foreground">Fresh: </span>
            <span className="font-semibold tabular-nums text-foreground">{fmtTokens(totalFresh)}</span>
          </div>
          <div className="rounded-md border border-border bg-interactive-secondary/60 px-2.5 py-1">
            <span className="text-tertiary-foreground">Output: </span>
            <span className="font-semibold tabular-nums text-foreground">{fmtTokens(totalOutput)}</span>
          </div>
        </div>
      </div>

      {/* Legend & caveats */}
      <div className="mt-3 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 text-xs text-tertiary-foreground">
        <div className="flex flex-wrap items-center gap-3">
          <span className="inline-flex items-center gap-1.5">
            <span className="h-2.5 w-2.5 rounded-xs" style={{ backgroundColor: SPEND_COLORS.fresh_input }} />
            <span className="text-foreground">{SPEND_LABELS.fresh_input}</span>
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="h-2.5 w-2.5 rounded-xs" style={{ backgroundColor: SPEND_COLORS.cache_read }} />
            <span className="text-foreground">{SPEND_LABELS.cache_read}</span>
          </span>
          {hasRecordedCc && (
            <span className="inline-flex items-center gap-1.5">
              <span className="h-2.5 w-2.5 rounded-xs" style={{ backgroundColor: SPEND_COLORS.cache_creation }} />
              <span className="text-foreground">{SPEND_LABELS.cache_creation}</span>
            </span>
          )}
          <span className="inline-flex items-center gap-1.5">
            <span className="h-2.5 w-2.5 rounded-xs" style={{ backgroundColor: SPEND_COLORS.output }} />
            <span className="text-foreground">{SPEND_LABELS.output}</span>
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="h-2.5 w-2.5 rounded-xs" style={{ backgroundColor: SPEND_COLORS.reasoning }} />
            <span className="text-foreground">{SPEND_LABELS.reasoning}</span>
          </span>
          <span className="inline-flex items-center gap-1.5">
            <span className="h-0.5 w-3.5 bg-primary" />
            <span className="text-foreground">Cumulative input (right axis)</span>
          </span>
        </div>

        {!hasRecordedCc && (
          <span className="text-[11px] italic text-tertiary-foreground">
            Cache-creation input was not recorded for this session.
          </span>
        )}
      </div>

      {/* Interactive SVG Chart Container */}
      <div className="relative mt-3 overflow-x-auto rounded-md border border-border bg-muted/20">
        <svg
          viewBox={`0 0 ${chartWidth} ${chartHeight}`}
          width={chartWidth}
          height={chartHeight}
          style={{ minWidth: `${chartWidth}px` }}
          className="block font-sans select-none"
        >
          {/* Horizontal Grid lines & left ticks */}
          {[0, 0.25, 0.5, 0.75, 1].map((frac, idx) => {
            const y = padTop + innerHeight * (1 - frac)
            const tokenVal = maxTurnTokens * frac
            const cumVal = maxCumulative * frac
            return (
              <g key={`grid-${idx}`}>
                <line
                  x1={padLeft}
                  y1={y}
                  x2={padLeft + innerWidth}
                  y2={y}
                  stroke="var(--color-chart-grid-stroke)"
                  strokeDasharray="2 3"
                />
                <text
                  x={padLeft - 8}
                  y={y + 3}
                  textAnchor="end"
                  className="fill-tertiary-foreground text-[10px] tabular-nums"
                >
                  {fmtTokens(tokenVal)}
                </text>
                <text
                  x={padLeft + innerWidth + 8}
                  y={y + 3}
                  textAnchor="start"
                  className="fill-tertiary-foreground text-[10px] tabular-nums"
                >
                  {fmtTokens(cumVal)}
                </text>
              </g>
            )
          })}

          {/* Turn Stacked Bars */}
          {normalizedTurns.map((t, i) => {
            const cx = padLeft + innerWidth * ((i + 0.5) / turnCount)
            const x = cx - barWidth / 2
            const isSelected = selectedTurnIndex === t.index
            const isHovered = hoveredIndex === t.index

            // Segments bottom to top: cacheRead, cacheCreation, fresh, output, reasoning
            const segments: Array<{ key: SpendTokenClass; value: number; color: string }> = (
              [
                { key: 'cache_read', value: t.cacheRead, color: SPEND_COLORS.cache_read },
                { key: 'cache_creation', value: t.cacheCreation, color: SPEND_COLORS.cache_creation },
                { key: 'fresh_input', value: t.fresh, color: SPEND_COLORS.fresh_input },
                { key: 'output', value: t.output, color: SPEND_COLORS.output },
                { key: 'reasoning', value: t.reasoning, color: SPEND_COLORS.reasoning },
              ] as const
            ).filter((s) => s.value > 0)

            let accumulatedHeight = 0

            return (
              <g
                key={`turn-${t.index}`}
                data-testid={`turn-bar-${t.index}`}
                className="cursor-pointer transition-opacity focus:outline-hidden"
                tabIndex={0}
                role="button"
                aria-label={`Turn ${t.index}`}
                onClick={() => onSelectTurn?.(t.index)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    onSelectTurn?.(t.index)
                  }
                }}
                onMouseEnter={() => setHoveredIndex(t.index)}
                onMouseLeave={() => setHoveredIndex(null)}
                onFocus={() => setHoveredIndex(t.index)}
                onBlur={() => setHoveredIndex(null)}
              >
                {/* Background column hit target & selection indicator */}
                <rect
                  x={x - 4}
                  y={padTop}
                  width={barWidth + 8}
                  height={innerHeight}
                  fill={
                    isSelected
                      ? 'var(--color-interactive-secondary-hover)'
                      : isHovered
                        ? 'var(--color-interactive-secondary)'
                        : 'transparent'
                  }
                  rx={4}
                />

                {/* Stacked segments */}
                {segments.map((seg) => {
                  const segHeight = Math.max(1, (seg.value / maxTurnTokens) * innerHeight)
                  const y = padTop + innerHeight - accumulatedHeight - segHeight
                  accumulatedHeight += segHeight

                  return (
                    <rect
                      key={seg.key}
                      x={x}
                      y={y}
                      width={barWidth}
                      height={segHeight}
                      fill={seg.color}
                      opacity={isHovered || isSelected ? 1 : 0.85}
                      rx={accumulatedHeight === segHeight ? 1 : 0}
                    >
                      <title>
                        Turn {t.index} · {SPEND_LABELS[seg.key]}: {fmtNum(seg.value)} tokens
                      </title>
                    </rect>
                  )
                })}

                {/* Selected highlight border */}
                {isSelected && (
                  <rect
                    x={x - 2}
                    y={padTop + innerHeight - accumulatedHeight - 2}
                    width={barWidth + 4}
                    height={accumulatedHeight + 4}
                    fill="none"
                    stroke="var(--color-foreground)"
                    strokeWidth={1.5}
                    rx={2}
                  />
                )}

                {/* Turn index label at bottom */}
                <text
                  x={cx}
                  y={padTop + innerHeight + 16}
                  textAnchor="middle"
                  className={cn(
                    'text-[11px] tabular-nums font-medium',
                    isSelected ? 'fill-foreground font-bold' : 'fill-muted-foreground',
                  )}
                >
                  {t.index}
                </text>

                {/* Total turn tokens below index */}
                <text
                  x={cx}
                  y={padTop + innerHeight + 28}
                  textAnchor="middle"
                  className="fill-tertiary-foreground text-[9px] tabular-nums"
                >
                  {fmtTokens(t.totalTokens)}
                </text>

                {/* Fresh Input Jump Warning Annotation */}
                {t.freshJumpPct != null && t.freshJumpPct >= 25 && (
                  <g>
                    <circle
                      cx={cx}
                      cy={padTop + innerHeight - accumulatedHeight - 10}
                      r={7}
                      fill="#ef4444"
                      fillOpacity={0.15}
                      stroke="#ef4444"
                      strokeWidth={1}
                    />
                    <text
                      x={cx}
                      y={padTop + innerHeight - accumulatedHeight - 7}
                      textAnchor="middle"
                      className="fill-red-600 text-[8px] font-bold dark:fill-red-400"
                    >
                      !
                    </text>
                    <title>Fresh input jumped +{t.freshJumpPct}% vs previous turn</title>
                  </g>
                )}

                {/* Request Boundary Marker */}
                {t.requestStart && i > 0 && (
                  <g>
                    <line
                      x1={cx - innerWidth / turnCount / 2}
                      y1={padTop}
                      x2={cx - innerWidth / turnCount / 2}
                      y2={padTop + innerHeight + 30}
                      stroke="var(--color-brand)"
                      strokeWidth={1}
                      strokeDasharray="2 3"
                      strokeOpacity={0.7}
                    />
                    <title>New prompt request: {t.requestStart}</title>
                  </g>
                )}
              </g>
            )
          })}

          {/* Cumulative Input Line */}
          <path
            d={linePath}
            fill="none"
            stroke="var(--primary)"
            strokeWidth={1.8}
            strokeDasharray="4 3"
          />
          {linePoints.map((p) => (
            <circle
              key={`cum-${p.turnIndex}`}
              cx={p.cx}
              cy={p.cy}
              r={2.8}
              fill="var(--primary)"
            />
          ))}

          {/* Baseline horizontal line */}
          <line
            x1={padLeft}
            y1={padTop + innerHeight}
            x2={padLeft + innerWidth}
            y2={padTop + innerHeight}
            stroke="var(--border)"
            strokeWidth={1}
          />
          <text
            x={padLeft + innerWidth / 2}
            y={chartHeight - 6}
            textAnchor="middle"
            className="fill-tertiary-foreground text-[10px] font-medium"
          >
            Turn Index (click to inspect)
          </text>
        </svg>

        {/* Hover / Active Detail Tooltip Card */}
        {activeHoverTurn && (
          <div
            className="pointer-events-none absolute top-2 right-4 z-10 w-64 rounded-md border border-border bg-popover/95 p-2.5 text-xs shadow-md backdrop-blur-xs"
            aria-live="polite"
          >
            <div className="flex items-center justify-between border-b border-border pb-1 font-medium text-foreground">
              <span>Turn #{activeHoverTurn.index}</span>
              <span className="tabular-nums text-tertiary-foreground">
                Total: {fmtNum(activeHoverTurn.totalTokens)} tok
              </span>
            </div>
            <div className="mt-1.5 space-y-1 text-[11px]">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1 text-muted-foreground">
                  <span className="h-2 w-2 rounded-xs" style={{ backgroundColor: SPEND_COLORS.fresh_input }} />
                  Fresh input
                </span>
                <span className="tabular-nums text-foreground">
                  {fmtNum(activeHoverTurn.fresh)} (
                  {activeHoverTurn.totalTokens > 0
                    ? ((activeHoverTurn.fresh / activeHoverTurn.totalTokens) * 100).toFixed(0)
                    : 0}
                  %)
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1 text-muted-foreground">
                  <span className="h-2 w-2 rounded-xs" style={{ backgroundColor: SPEND_COLORS.cache_read }} />
                  Cache read
                </span>
                <span className="tabular-nums text-foreground">
                  {fmtNum(activeHoverTurn.cacheRead)} (
                  {activeHoverTurn.totalTokens > 0
                    ? ((activeHoverTurn.cacheRead / activeHoverTurn.totalTokens) * 100).toFixed(0)
                    : 0}
                  %)
                </span>
              </div>
              {activeHoverTurn.cacheCreation > 0 && (
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-1 text-muted-foreground">
                    <span className="h-2 w-2 rounded-xs" style={{ backgroundColor: SPEND_COLORS.cache_creation }} />
                    Cache creation
                  </span>
                  <span className="tabular-nums text-foreground">
                    {fmtNum(activeHoverTurn.cacheCreation)}
                  </span>
                </div>
              )}
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-1 text-muted-foreground">
                  <span className="h-2 w-2 rounded-xs" style={{ backgroundColor: SPEND_COLORS.output }} />
                  Output
                </span>
                <span className="tabular-nums text-foreground">
                  {fmtNum(activeHoverTurn.output)}
                </span>
              </div>
              {activeHoverTurn.reasoning > 0 && (
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-1 text-muted-foreground">
                    <span className="h-2 w-2 rounded-xs" style={{ backgroundColor: SPEND_COLORS.reasoning }} />
                    Reasoning
                  </span>
                  <span className="tabular-nums text-foreground">
                    {fmtNum(activeHoverTurn.reasoning)}
                  </span>
                </div>
              )}
              <div className="mt-1 flex items-center justify-between border-t border-border pt-1 font-medium text-foreground">
                <span className="text-tertiary-foreground">Cumulative input</span>
                <span className="tabular-nums">{fmtNum(activeHoverTurn.cumulativeInput)}</span>
              </div>
            </div>
          </div>
        )}
      </div>

      {/* Fresh Jump Notification Banner */}
      <div className="mt-3">
        {jumps.length > 0 ? (
          <div className="flex items-start gap-2 rounded-md border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-xs text-amber-700 dark:text-amber-300">
            <span className="font-bold">⚡</span>
            <span>
              Fresh-input jump &gt;25% at turn{jumps.length > 1 ? 's' : ''}{' '}
              {jumps.map((j) => `#${j.index} (+${j.freshJumpPct}%)`).join(', ')} — context expansion or cache invalidation.
            </span>
          </div>
        ) : (
          <div className="rounded-md border border-border bg-interactive-secondary/40 px-3 py-1.5 text-xs text-tertiary-foreground">
            ✓ Cache stability verified: No turn grew fresh input by more than 25% over the previous turn.
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// 2. Context Composition Heatmap & Chart (`ContextCompositionChart`)
// ---------------------------------------------------------------------------

/**
 * The honesty line under the chart.
 *
 * Bars are scaled to the model's own reported input, so whatever the buckets
 * do not account for is drawn as a residual rather than quietly absorbed.
 * What that residual MEANS depends on where the counts came from, and saying
 * so is the difference between a chart you can act on and one you cannot:
 *
 *  - Harness-reported counts are exact. The residual is then real unattributed
 *    content - framing, role delimiters - and worth investigating.
 *  - Tokenized counts are a lower bound, because a proxy tokenizer is not the
 *    model's own. A large residual there is mostly tokenizer drift, and the
 *    bucket sizes should be read as proportions rather than absolutes.
 *
 * The previous version showed "(* lower bound proxy)" whenever `derivedCounts`
 * was set, which is the right warning applied indiscriminately: it appeared
 * even on harnesses reporting their own per-bucket totals, where it is simply
 * false.
 */
export function ContextCaveat({
  derivedCounts,
  derivedModel,
  residualPct,
  unmeasuredTurns,
  hasServerAttribution,
}: {
  derivedCounts?: boolean
  derivedModel?: string
  residualPct: number
  unmeasuredTurns?: number
  hasServerAttribution: boolean
}) {
  const heavyDrift = derivedCounts === true && residualPct > 15

  return (
    <div
      data-testid="context-caveat"
      className={cn(
        'mt-3 rounded-md border px-3 py-2 text-[11px] leading-relaxed',
        heavyDrift
          ? 'border-amber-500/40 bg-amber-500/5 text-amber-700 dark:text-amber-300'
          : 'border-border bg-interactive-secondary/40 text-tertiary-foreground',
      )}
    >
      {derivedCounts === false ? (
        <span data-testid="context-caveat-measured">
          Bucket sizes are <strong>reported by the harness</strong>, not estimated. The residual of{' '}
          <span className="tabular-nums">{residualPct.toFixed(1)}%</span> is input the harness did not
          attribute to any bucket — chat framing and role delimiters — rather than tokenizer drift.
        </span>
      ) : (
        <span data-testid="context-caveat-derived">
          Bucket sizes are <strong>derived</strong> by tokenizing content with{' '}
          <span className="font-mono">{derivedModel ?? 'o200k_base'}</span>, a proxy for models that do
          not publish their tokenizer, so each is a lower bound. The residual of{' '}
          <span className="tabular-nums">{residualPct.toFixed(1)}%</span> is the gap between the buckets
          and the model's own reported input.
          {heavyDrift && (
            <>
              {' '}
              <strong>Treat this session's bucket sizes as a lower bound.</strong> A residual this large
              means the proxy is a poor fit for this model: every bucket is undercounted by roughly the
              same factor, so proportions stay informative but absolute numbers do not.
            </>
          )}
        </span>
      )}

      {!hasServerAttribution && (
        <span data-testid="context-caveat-no-servers" className="mt-1 block">
          Tool definitions are not attributed to MCP servers for this session: the harness exported tool
          names without naming the server each came from. Those tokens are counted, not split by a guess.
        </span>
      )}

      {unmeasuredTurns !== undefined && unmeasuredTurns > 0 && (
        <span data-testid="context-caveat-unmeasured" className="mt-1 block">
          {unmeasuredTurns} turn{unmeasuredTurns === 1 ? '' : 's'} carried content but no token counters
          and {unmeasuredTurns === 1 ? 'is' : 'are'} not charted here. Their spend is still counted.
        </span>
      )}
    </div>
  )
}

export function ContextCompositionChart({
  context,
  turns: _turns,
  selectedTurnIndex,
  selectedBucket,
  onSelectTurn,
  className,
}: ContextCompositionChartProps) {
  const [viewMode, setViewMode] = useState<'bars' | 'heatmap'>('bars')
  const [hoveredCell, setHoveredCell] = useState<{ turnIndex: number; bucket: ContextBucketKey } | null>(null)

  // Extract normalized rows
  const rows = useMemo(() => extractNormalizedContextTurns(context), [context])

  // Check if measurable is explicitly false
  if (context?.measurable === false) {
    return (
      <div
        data-testid="context-composition-not-measurable"
        className={cn('rounded-lg border border-border bg-card p-6 text-card-foreground shadow-xs', className)}
      >
        <div className="flex items-center gap-2">
          <span className="rounded-sm bg-amber-500/10 px-2 py-0.5 text-xs font-semibold text-amber-600 dark:text-amber-400">
            Not Measurable
          </span>
          <h3 className="text-sm font-semibold text-foreground">Context Composition Unavailable</h3>
        </div>
        <p className="mt-2 text-xs text-muted-foreground">
          {context.reason ?? 'Context composition is not measurable for this harness source.'}
        </p>
        <p className="mt-1 text-xs text-tertiary-foreground">
          Per-turn spend and cost metrics remain completely accurate as they are derived directly from API counters.
        </p>
      </div>
    )
  }

  // Check if context is completely empty
  if (!context || rows.length === 0) {
    return (
      <div
        data-testid="context-composition-empty"
        className={cn(
          'rounded-lg border border-border bg-card p-6 text-center text-card-foreground shadow-xs',
          className,
        )}
      >
        <p className="text-sm font-medium text-foreground">No context data available</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Context composition telemetry was not captured for this session.
        </p>
      </div>
    )
  }

  // The legend names the bands actually drawn, so a per-server split shows
  // which servers are present rather than a single "Tool definitions" swatch
  // that the bars then contradict. Union across turns: a server that appears
  // only in the last turn still belongs in the key.
  const legendSegments: ContextSegment[] = []
  const seenSegments = new Set<string>()
  for (const row of rows) {
    for (const segment of contextSegments(row)) {
      if (seenSegments.has(segment.id)) continue
      seenSegments.add(segment.id)
      legendSegments.push(segment)
    }
  }

  const maxBucketValue = Math.max(
    1,
    ...rows.flatMap((r) => CONTEXT_BUCKET_KEYS.map((k) => r.buckets[k])),
  )

  // Residual percentage calculations
  const totalResidual = rows.reduce((s, r) => s + r.buckets.residual, 0)
  const totalAllTokens = rows.reduce((s, r) => s + r.total, 0)
  const avgResidualPct = totalAllTokens > 0 ? (totalResidual / totalAllTokens) * 100 : 0

  return (
    <div
      data-testid="context-composition-chart"
      className={cn('rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs', className)}
    >
      {/* Header & Controls */}
      <div className="flex flex-wrap items-start justify-between gap-4 border-b border-border pb-3">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-sm font-semibold text-foreground">Context Composition</h3>
            {context.contextLimit && (
              <span className="rounded-full bg-interactive-secondary px-2 py-0.5 text-[11px] font-medium text-tertiary-foreground">
                Limit: {fmtTokens(context.contextLimit)} tok
              </span>
            )}
          </div>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Interactive semantic token breakdown across turns by message part type.
          </p>
        </div>

        {/* View Switcher Tabs */}
        <div className="flex items-center rounded-md border border-border bg-interactive-secondary p-0.5 text-xs">
          <button
            type="button"
            data-testid="tab-stacked-bars"
            onClick={() => setViewMode('bars')}
            className={cn(
              'rounded-sm px-2.5 py-1 text-xs font-medium transition-colors',
              viewMode === 'bars'
                ? 'bg-card text-foreground shadow-xs'
                : 'text-tertiary-foreground hover:text-foreground',
            )}
          >
            Stacked Bars
          </button>
          <button
            type="button"
            data-testid="tab-heatmap"
            onClick={() => setViewMode('heatmap')}
            className={cn(
              'rounded-sm px-2.5 py-1 text-xs font-medium transition-colors',
              viewMode === 'heatmap'
                ? 'bg-card text-foreground shadow-xs'
                : 'text-tertiary-foreground hover:text-foreground',
            )}
          >
            Heatmap Matrix
          </button>
        </div>
      </div>

      {/* Legend & Context Metadata */}
      <div className="mt-3 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 text-xs text-tertiary-foreground">
        <div className="flex flex-wrap items-center gap-3">
          {legendSegments.map((segment) => (
            <span
              key={segment.id}
              tabIndex={0}
              role="button"
              data-testid={`context-legend-${segment.id}`}
              className={cn(
                'inline-flex items-center gap-1.5 cursor-pointer select-none transition-opacity focus:outline-hidden focus-visible:ring-1 focus-visible:ring-ring rounded-xs',
                selectedBucket === segment.key
                  ? 'font-semibold text-foreground'
                  : 'text-tertiary-foreground hover:text-foreground',
              )}
              onClick={() => onSelectTurn?.(selectedTurnIndex ?? rows[0]?.turnIndex ?? 1, segment.key)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  onSelectTurn?.(selectedTurnIndex ?? rows[0]?.turnIndex ?? 1, segment.key)
                }
              }}
            >
              <span className="h-2.5 w-2.5 rounded-xs" style={{ backgroundColor: segment.color }} />
              <span>{segment.label}</span>
            </span>
          ))}
        </div>

        <div className="text-[11px] text-tertiary-foreground">
          {hoveredCell ? (
            <span className="font-medium text-foreground">
              Turn #{hoveredCell.turnIndex} · {CONTEXT_BUCKET_LABELS[hoveredCell.bucket]}
            </span>
          ) : (
            <>
              Residual:{' '}
              <span className="font-semibold tabular-nums text-foreground">{avgResidualPct.toFixed(1)}%</span>
            </>
          )}
        </div>
      </div>

      <ContextCaveat
        derivedCounts={context.derivedCounts}
        derivedModel={context.derivedModel}
        residualPct={avgResidualPct}
        unmeasuredTurns={(context as { unmeasuredTurns?: number }).unmeasuredTurns}
        hasServerAttribution={legendSegments.some((segment) => segment.server !== undefined)}
      />

      {/* View 1: Stacked Horizontal Bars */}
      {viewMode === 'bars' && (
        <div data-testid="context-stacked-bars" className="mt-4 space-y-3">
          {rows.map((row) => {
            const isTurnSelected = selectedTurnIndex === row.turnIndex

            return (
              <div
                key={row.turnIndex}
                data-testid={`context-turn-${row.turnIndex}`}
                className={cn(
                  'rounded-md border border-border p-2.5 transition-colors',
                  isTurnSelected ? 'bg-interactive-secondary ring-1 ring-border' : 'hover:bg-interactive-secondary/50',
                )}
              >
                {/* Row Header */}
                <div className="flex items-center justify-between text-xs font-medium">
                  <span
                    tabIndex={0}
                    role="button"
                    className="cursor-pointer font-semibold text-foreground hover:underline focus:outline-hidden focus-visible:ring-1 focus-visible:ring-ring rounded-xs"
                    onClick={() => onSelectTurn?.(row.turnIndex)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault()
                        onSelectTurn?.(row.turnIndex)
                      }
                    }}
                  >
                    {row.label}
                  </span>
                  <span className="tabular-nums text-tertiary-foreground">
                    {fmtNum(row.total)} tokens
                  </span>
                </div>

                {/* Horizontal Stacked Bar */}
                <div className="mt-2 flex h-6 w-full overflow-hidden rounded-sm bg-muted">
                  {contextSegments(row).map((segment) => {
                    const k = segment.key
                    const val = segment.tokens
                    const pct = (val / row.total) * 100
                    const isBucketSelected = selectedBucket === k

                    return (
                      <div
                        key={segment.id}
                        tabIndex={0}
                        role="button"
                        data-testid={`context-segment-${segment.id}`}
                        style={{
                          width: `${pct}%`,
                          backgroundColor: segment.color,
                        }}
                        className={cn(
                          'relative h-full cursor-pointer transition-opacity flex items-center justify-center text-[10px] font-bold text-white drop-shadow-[0_1px_1px_rgba(0,0,0,0.8)] focus:outline-hidden focus-visible:ring-2 focus-visible:ring-foreground z-0',
                          isBucketSelected ? 'ring-2 ring-foreground z-10' : 'hover:opacity-90',
                        )}
                        title={`${segment.label}: ${fmtNum(val)} tokens (${pct.toFixed(1)}%) — click to inspect`}
                        onClick={(e) => {
                          e.stopPropagation()
                          onSelectTurn?.(row.turnIndex, k)
                        }}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault()
                            e.stopPropagation()
                            onSelectTurn?.(row.turnIndex, k)
                          }
                        }}
                        onMouseEnter={() => setHoveredCell({ turnIndex: row.turnIndex, bucket: k })}
                        onMouseLeave={() => setHoveredCell(null)}
                        onFocus={() => setHoveredCell({ turnIndex: row.turnIndex, bucket: k })}
                        onBlur={() => setHoveredCell(null)}
                      >
                        {pct >= 8 && <span className="truncate px-1">{pct.toFixed(0)}%</span>}
                      </div>
                    )
                  })}
                </div>

                {/* Subtitle breakdown */}
                <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px] text-tertiary-foreground">
                  {contextSegments(row).map((segment) => (
                    <span key={segment.id} className="tabular-nums">
                      <span className="text-muted-foreground">{segment.label}:</span>{' '}
                      {fmtTokens(segment.tokens)}
                    </span>
                  ))}
                </div>
              </div>
            )
          })}
        </div>
      )}

      {/* View 2: Heatmap Matrix View */}
      {viewMode === 'heatmap' && (
        <div data-testid="context-heatmap" className="mt-4 overflow-x-auto rounded-md border border-border">
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b border-border bg-interactive-secondary/60">
                <th className="px-3 py-2 text-left font-medium text-tertiary-foreground">Turn</th>
                {CONTEXT_BUCKET_KEYS.map((k) => (
                  <th key={k} className="px-2 py-2 text-right font-medium text-tertiary-foreground">
                    {CONTEXT_BUCKET_LABELS[k]}
                  </th>
                ))}
                <th className="px-3 py-2 text-right font-medium text-tertiary-foreground">Total Input</th>
                <th className="px-3 py-2 text-right font-medium text-tertiary-foreground">Pressure</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const isSelected = selectedTurnIndex === row.turnIndex

                return (
                  <tr
                    key={row.turnIndex}
                    className={cn(
                      'border-b border-border transition-colors',
                      isSelected ? 'bg-interactive-secondary ring-1 ring-border' : 'hover:bg-interactive-secondary/40',
                    )}
                  >
                    <td
                      tabIndex={0}
                      role="button"
                      className="px-3 py-2 font-medium tabular-nums cursor-pointer text-foreground hover:underline focus:outline-hidden focus-visible:ring-1 focus-visible:ring-ring"
                      onClick={() => onSelectTurn?.(row.turnIndex)}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault()
                          onSelectTurn?.(row.turnIndex)
                        }
                      }}
                    >
                      {row.label}
                    </td>

                    {CONTEXT_BUCKET_KEYS.map((k) => {
                      const val = row.buckets[k] || 0
                      const intensity = maxBucketValue > 0 ? val / maxBucketValue : 0

                      return (
                        <td
                          key={k}
                          tabIndex={0}
                          role="button"
                          className="px-2 py-2 text-right tabular-nums cursor-pointer focus:outline-hidden focus-visible:ring-1 focus-visible:ring-ring"
                          onClick={() => onSelectTurn?.(row.turnIndex, k)}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter' || e.key === ' ') {
                              e.preventDefault()
                              onSelectTurn?.(row.turnIndex, k)
                            }
                          }}
                          title={`${CONTEXT_BUCKET_LABELS[k]}: ${fmtNum(val)} tokens`}
                        >
                          <span
                            className="inline-block min-w-12 rounded-sm px-1.5 py-0.5 font-medium text-foreground transition-all"
                            style={{
                              backgroundColor: `color-mix(in srgb, ${CONTEXT_BUCKET_COLORS[k]} ${Math.round(
                                intensity * 75 + 10,
                              )}%, transparent)`,
                            }}
                          >
                            {val > 0 ? fmtTokens(val) : '—'}
                          </span>
                        </td>
                      )
                    })}

                    <td className="px-3 py-2 text-right font-semibold tabular-nums text-foreground">
                      {fmtTokens(row.total)}
                    </td>

                    <td className="px-3 py-2 text-right tabular-nums text-tertiary-foreground">
                      {row.pressure != null
                        ? `${(row.pressure * 100).toFixed(1)}%`
                        : context.contextLimit
                          ? `${((row.total / context.contextLimit) * 100).toFixed(1)}%`
                          : '—'}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {/* Explanatory caveat note */}
      <div className="mt-3 text-xs text-tertiary-foreground">
        Residual accounts for tokenizer drift, role delimiters, and unmeasured chat framing. Lower residual values indicate
        higher precision message token tracking.
      </div>
    </div>
  )
}

// ---------------------------------------------------------------------------
// 3. Composite SessionSpendCharts Component
// ---------------------------------------------------------------------------

export function SessionSpendCharts({
  session,
  turns: propsTurns,
  context: propsContext,
  selectedTurnIndex,
  selectedBucket,
  onSelectTurn,
  className,
}: SessionSpendChartsProps) {
  const turns = propsTurns ?? session?.turns
  const context = propsContext ?? session?.context
  return (
    <div className={cn('flex flex-col gap-6', className)} data-testid="session-spend-charts">
      <TurnSpendChart
        turns={turns}
        selectedTurnIndex={selectedTurnIndex}
        onSelectTurn={(idx) => onSelectTurn?.(idx)}
      />
      <ContextCompositionChart
        context={context}
        turns={turns}
        selectedTurnIndex={selectedTurnIndex}
        selectedBucket={selectedBucket}
        onSelectTurn={(idx, bucket) => onSelectTurn?.(idx, bucket)}
      />
    </div>
  )
}
