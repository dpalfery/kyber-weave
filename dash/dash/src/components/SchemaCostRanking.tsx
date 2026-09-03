import { cn, fmtTokens } from '../lib/utils'
import { MCP_SERVER_COLORS } from './SessionSpendCharts'

/**
 * Wire + analysis shape for schema-cost ranking (R8).
 *
 * `rankSchemas` returns a discriminated union on `measurable`. The session
 * payload serialises the measurable variant without `ranked` — that list
 * travels as `tools` ({ name, server, total_schema_cost, invoked }) — and
 * turns `byServer` from a Map into a plain object. Both forms are accepted
 * here so a consumer of either seam can render without reconstructing zeros.
 */
export type RankedTool = {
  name: string
  /** Ground-truth MCP server; absent for the harness's built-in tools. */
  server?: string
  /** Resident cost: schema tokens × turns resident. */
  cost: number
  invoked: boolean
}

export type UnusedSchemaRange = {
  tokenResidencies: number
  floor: number
  ceiling: number
  /** Present only when the bounds are published prices, not token residencies. */
  currency?: string
}

export type SchemaCostAnalysis =
  | {
      measurable: true
      ranked?: RankedTool[]
      neverInvoked: RankedTool[]
      byServer: Map<string, number> | Record<string, number>
      unusedRange: UnusedSchemaRange
      turns: number
    }
  | {
      measurable: false
      invocationCount: number
      reason?: 'declared_not_measurable'
    }

/**
 * A row from the session payload's `tools` array. `server` is null on the
 * wire for built-ins; `total_schema_cost` is the ranked resident cost.
 * Legacy rows may carry `invocations` instead of `invoked`.
 */
export type SchemaCostToolRow = {
  name: string
  server?: string | null
  is_mcp?: boolean
  total_schema_cost?: number | null
  invoked?: boolean
  invocations?: number
  in_definitions?: boolean
}

export type SchemaCostRankingProps = {
  schema?: SchemaCostAnalysis | null
  tools?: SchemaCostToolRow[]
  /** Kept for the session inspector: a click still opens the existing tool drawer. */
  onSelectTool?: (tool: {
    name: string
    server?: string
    is_mcp?: boolean
    invocations: number
    total_schema_cost?: number | null
    invoked?: boolean
    in_definitions?: boolean
  }) => void
  className?: string
}

type DisplayTool = {
  name: string
  server?: string
  /** Absent when the payload did not carry a resident cost — never coerced to 0. */
  cost?: number
  invoked: boolean
}

const BUILTIN_LABEL = 'Built-in'

function serverOf(tool: { server?: string | null }): string | undefined {
  return tool.server == null || tool.server === '' ? undefined : tool.server
}

function byServerEntries(
  byServer: Map<string, number> | Record<string, number>,
): Array<[string, number]> {
  return byServer instanceof Map ? [...byServer.entries()] : Object.entries(byServer)
}

function formatBound(value: number, currency?: string): string {
  if (currency === undefined) return fmtTokens(value)
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(value)
  } catch {
    // An unrecognised currency code is still a priced bound; don't fall back
    // to tokens and mix units.
    return `${value} ${currency}`
  }
}

/**
 * Ranked rows to draw. Prefer `schema.ranked` when the analysis included it;
 * otherwise map the payload `tools` array, which is how `buildSessionRow`
 * serialises the same ordering.
 */
function displayToolsOf(schema: SchemaCostAnalysis | null | undefined, tools: SchemaCostToolRow[] | undefined): DisplayTool[] {
  if (schema?.measurable && schema.ranked !== undefined) {
    return schema.ranked.map((tool) => ({
      name: tool.name,
      ...(tool.server !== undefined ? { server: tool.server } : {}),
      cost: tool.cost,
      invoked: tool.invoked,
    }))
  }

  if (tools === undefined || tools.length === 0) return []

  return [...tools]
    .map((tool) => {
      const server = serverOf(tool)
      const invoked = tool.invoked ?? (typeof tool.invocations === 'number' ? tool.invocations > 0 : false)
      const row: DisplayTool = {
        name: tool.name,
        invoked,
        ...(server !== undefined ? { server } : {}),
      }
      // A missing cost is left off the object. Assigning 0 would rank an
      // unmeasured tool as the cheapest, which is the lie this surface exists
      // to refuse.
      if (tool.total_schema_cost != null && Number.isFinite(tool.total_schema_cost)) {
        row.cost = tool.total_schema_cost
      }
      return row
    })
    .sort((a, b) => {
      const aN = a.cost !== undefined
      const bN = b.cost !== undefined
      if (aN && bN) return b.cost! - a.cost! || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)
      if (aN) return -1
      if (bN) return 1
      return a.name < b.name ? -1 : a.name > b.name ? 1 : 0
    })
}

function neverInvokedOf(
  schema: SchemaCostAnalysis | null | undefined,
  ranked: DisplayTool[],
): DisplayTool[] {
  if (schema?.measurable) return schema.neverInvoked
  return ranked.filter((tool) => !tool.invoked)
}

function unusedResidentCost(schema: SchemaCostAnalysis | null | undefined, neverInvoked: DisplayTool[]): number | undefined {
  if (schema?.measurable) return schema.unusedRange.tokenResidencies
  const measured = neverInvoked.filter((tool) => tool.cost !== undefined)
  if (measured.length !== neverInvoked.length) return undefined
  return measured.reduce((sum, tool) => sum + tool.cost!, 0)
}

export function SchemaCostRanking({ schema, tools, onSelectTool, className }: SchemaCostRankingProps) {
  if (schema?.measurable === false) {
    return (
      <div
        data-testid="schema-cost-not-measurable"
        className={cn('rounded-lg border border-border bg-card p-6 text-card-foreground shadow-xs', className)}
      >
        <div className="flex items-center gap-2">
          <span className="rounded-sm bg-amber-500/10 px-2 py-0.5 text-xs font-semibold text-amber-600 dark:text-amber-400">
            Not Measurable
          </span>
          <h3 className="text-sm font-semibold text-foreground">Schema cost ranking unavailable</h3>
        </div>
        <p className="mt-2 text-xs text-muted-foreground">
          The harness reported tool invocations
          {Number.isFinite(schema.invocationCount) ? ` (${schema.invocationCount})` : ''} but exported no
          tool definitions, so they cannot be ranked.
        </p>
        <p className="mt-1 text-xs text-tertiary-foreground">
          An empty ranking would read as “nothing was offered”. That is a different fact from not being
          able to tell.
        </p>
      </div>
    )
  }

  const ranked = displayToolsOf(schema, tools)
  const neverInvoked = neverInvokedOf(schema, ranked)
  const unusedCost = unusedResidentCost(schema, neverInvoked)

  if (ranked.length === 0) {
    return (
      <div
        data-testid="schema-cost-ranking"
        className={cn('rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs', className)}
      >
        <table className="w-full text-xs" data-testid="tools-ranking-table">
          <tbody>
            <tr>
              <td colSpan={9} className="py-6 text-center text-sm text-muted-foreground">
                No tools recorded for this session.
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    )
  }

  const measuredCosts = ranked.map((tool) => tool.cost).filter((cost): cost is number => cost !== undefined)
  const maxCost = measuredCosts.length > 0 ? Math.max(...measuredCosts) : undefined

  const serverRollup: Array<{ label: string; cost?: number; builtin: boolean; mcp: boolean }> = []
  if (schema?.measurable) {
    for (const [label, cost] of byServerEntries(schema.byServer).sort(
      ([a, costA], [b, costB]) => costB - costA || (a < b ? -1 : a > b ? 1 : 0),
    )) {
      const mcp = tools?.some((tool) => serverOf(tool) === label && tool.is_mcp === true) ?? true
      serverRollup.push({ label, cost, builtin: false, mcp })
    }
  } else {
    const totals = new Map<string, number>()
    const mcpByServer = new Map<string, boolean>()
    for (const tool of ranked) {
      if (tool.server === undefined || tool.cost === undefined) continue
      totals.set(tool.server, (totals.get(tool.server) ?? 0) + tool.cost)
    }
    for (const tool of tools ?? []) {
      const server = serverOf(tool)
      if (server === undefined) continue
      if (tool.is_mcp) mcpByServer.set(server, true)
    }
    serverRollup.push(
      ...[...totals.entries()]
        .sort(([a, costA], [b, costB]) => costB - costA || (a < b ? -1 : a > b ? 1 : 0))
        .map(([label, cost]) => ({
          label,
          cost,
          builtin: false,
          mcp: mcpByServer.get(label) === true,
        })),
    )
  }

  // Built-ins never enter `byServer` (R8.3). Group them under a fixed label so
  // a prefixed name like `context7__query` is not guessed into a server.
  const builtinTools = ranked.filter((tool) => tool.server === undefined)
  if (builtinTools.length > 0) {
    const builtinMeasured = builtinTools.filter((tool) => tool.cost !== undefined)
    serverRollup.push({
      label: BUILTIN_LABEL,
      cost: builtinMeasured.length === builtinTools.length
        ? builtinMeasured.reduce((sum, tool) => sum + tool.cost!, 0)
        : undefined,
      builtin: true,
      mcp: false,
    })
  }

  const unusedRange = schema?.measurable ? schema.unusedRange : undefined
  const offered = ranked.length
  const unusedCount = neverInvoked.length
  const serverCosts = serverRollup
    .map((row) => row.cost)
    .filter((cost): cost is number => cost !== undefined)
  const maxServerCost = serverCosts.length > 0 ? Math.max(...serverCosts) : undefined

  const select = (tool: DisplayTool) => {
    if (!onSelectTool) return
    const original = tools?.find((row) => row.name === tool.name)
    // Prefer the payload row so the inspector sees invocations the ranking
    // never had to invent. A never-invoked tool with no row is 0 calls, which
    // is measured; an invoked tool with no count is not passed as 0.
    if (original !== undefined && typeof original.invocations === 'number') {
      onSelectTool({
        ...original,
        server: serverOf(original),
        invocations: original.invocations,
      })
      return
    }
    if (!tool.invoked) {
      onSelectTool({
        name: tool.name,
        server: tool.server,
        invocations: 0,
        total_schema_cost: tool.cost ?? null,
        invoked: false,
      })
    }
  }

  return (
    <div
      data-testid="schema-cost-ranking"
      className={cn('rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs', className)}
    >
      <div data-testid="schema-cost-headline" className="border-b border-border pb-3">
        <p className="text-sm font-medium text-foreground">
          {unusedCount} of {offered} offered {offered === 1 ? 'tool was' : 'tools were'} never invoked
          {unusedCost !== undefined ? (
            <>
              , costing{' '}
              <span className="tabular-nums font-semibold">{fmtTokens(unusedCost)}</span> resident tokens
            </>
          ) : (
            <>, costing <span className="text-tertiary-foreground">not measurable</span> resident tokens</>
          )}
          .
        </p>
        <p className="mt-1 text-xs text-muted-foreground">
          Resident cost is schema tokens multiplied by turns resident — a tool's schema is re-sent every
          turn it stays loaded, so a never-invoked tool is pure waste.
        </p>
      </div>

      <div data-testid="tools-ranking-table" className="mt-4 space-y-2">
        {ranked.map((tool) => {
          const never = !tool.invoked
          const hasCost = tool.cost !== undefined
          const pct = hasCost && maxCost !== undefined && maxCost > 0 ? (tool.cost! / maxCost) * 100 : undefined

          return (
            <div
              key={tool.name}
              data-testid={`tool-row-${tool.name}`}
              data-never-invoked={never ? 'true' : 'false'}
              role="button"
              tabIndex={0}
              onClick={() => select(tool)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault()
                  select(tool)
                }
              }}
              className={cn(
                'cursor-pointer rounded-md border p-2.5 transition-colors focus:outline-hidden focus-visible:ring-1 focus-visible:ring-ring',
                never
                  ? 'border-amber-500/40 bg-amber-500/10 hover:bg-amber-500/15'
                  : 'border-border hover:bg-interactive-secondary/50',
              )}
            >
              <div className="flex items-center justify-between gap-3 text-xs">
                <div className="flex min-w-0 items-center gap-2">
                  <span className="truncate font-semibold text-foreground">{tool.name}</span>
                  <span className="shrink-0 text-tertiary-foreground">
                    {tool.server ?? BUILTIN_LABEL}
                  </span>
                  {never && (
                    <span className="shrink-0 rounded border border-amber-500/30 bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-semibold text-amber-700 dark:text-amber-300">
                      0 calls · never called
                    </span>
                  )}
                </div>
                <span className="shrink-0 font-semibold tabular-nums text-foreground">
                  {hasCost ? fmtTokens(tool.cost) : 'not measurable'}
                </span>
              </div>
              <div className="mt-2 h-2 w-full overflow-hidden rounded-sm bg-muted">
                {pct !== undefined && (
                  <div
                    data-testid={`schema-tool-bar-${tool.name}`}
                    className="h-full rounded-sm"
                    style={{
                      width: `${pct}%`,
                      backgroundColor: never ? 'var(--chart-5)' : 'var(--chart-1)',
                    }}
                  />
                )}
              </div>
            </div>
          )
        })}
      </div>

      {serverRollup.length > 0 && (
        <div data-testid="schema-server-rollup" className="mt-4 border-t border-border pt-3">
          <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">By MCP server</h3>
          <p className="mt-0.5 text-[11px] text-tertiary-foreground">
            Resident cost grouped by the server each definition named. Tools with no server are the
            harness's built-ins, not a guessed MCP.
          </p>
          <div className="mt-2 space-y-2">
            {serverRollup.map((entry, index) => {
              const color = entry.builtin
                ? 'var(--chart-2)'
                : MCP_SERVER_COLORS[index % MCP_SERVER_COLORS.length]
              const width =
                entry.cost !== undefined && maxServerCost !== undefined && maxServerCost > 0
                  ? (entry.cost / maxServerCost) * 100
                  : undefined

              return (
                <div
                  key={entry.label}
                  data-testid={entry.builtin ? 'schema-server-builtin' : `schema-server-${entry.label}`}
                  className="flex items-center gap-3 text-xs"
                >
                  <span className="w-36 shrink-0 truncate font-medium text-foreground">
                    {entry.label}
                    {entry.mcp && (
                      <span className="ml-1.5 rounded bg-violet-500/15 px-1.5 py-0.5 text-[9px] font-semibold text-violet-600 dark:text-violet-400">
                        MCP
                      </span>
                    )}
                  </span>
                  <div className="h-2 min-w-0 flex-1 overflow-hidden rounded-sm bg-muted">
                    {width !== undefined && (
                      <div
                        className="h-full rounded-sm"
                        style={{ width: `${width}%`, backgroundColor: color }}
                      />
                    )}
                  </div>
                  <span className="w-16 shrink-0 text-right tabular-nums text-foreground">
                    {entry.cost !== undefined ? fmtTokens(entry.cost) : 'not measurable'}
                  </span>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {unusedRange !== undefined && (
        <div
          data-testid="schema-unused-range"
          className="mt-4 rounded-md border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-xs text-amber-800 dark:text-amber-300"
        >
          <div className="font-semibold">Unused-schema cost range</div>
          <p className="mt-1 tabular-nums text-foreground">
            {formatBound(unusedRange.floor, unusedRange.currency)} – {formatBound(unusedRange.ceiling, unusedRange.currency)}
            {unusedRange.currency === undefined ? ' token residencies' : ` ${unusedRange.currency}`}
          </p>
          <p className="mt-1 text-[11px] text-tertiary-foreground">
            Telemetry cannot tell whether a resident schema was a cache-read or fresh input, so the true
            cost is bounded, not known
            {unusedRange.currency === undefined
              ? ' — these bounds are token residencies, not a money figure.'
              : '.'}{' '}
            Floor assumes every residency a cache read; ceiling assumes every one was fresh input.
          </p>
        </div>
      )}
    </div>
  )
}
