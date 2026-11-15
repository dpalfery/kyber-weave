// Tool and schema cost analysis for KyberDash, ported from the Python
// pipeline's `views.py` and `cost.py` (spec: docs/specs/kyberdash,
// design.md "Analysis layer", R8).
//
// Every MCP server a session keeps connected injects its tools' schemas into
// the context of every turn the server is resident in — rent the session
// pays whether or not a tool is ever called. The ranking orders definitions
// by that rent, schema tokens multiplied by turns resident (R8.1), so it
// surfaces what a server costs to keep, not how loudly it is used.
//
// Three facts about the data keep this module honest:
//
//   - A tool offered all session and never invoked is pure waste, and its
//     cost is reported separately from the ranking (R8.2): the figure that
//     answers the requirement's user story is the waste, not the total.
//   - Server names come only from the ground truth a definition carries
//     (R8.3). Harnesses expose tools under prefixed identifiers —
//     `server__tool` — and splitting on the delimiter is the implementation
//     this module refuses: delimiters appear inside real server names, so a
//     split regroups tools a telemetry attribute already names correctly.
//   - Whether a resident schema was served from cache or charged as fresh
//     input is not in the telemetry, and the two prices differ by roughly an
//     order of magnitude. The unused cost is therefore a range (R8.4): a
//     floor assuming every residency was a cache read, a ceiling assuming
//     every one was fresh input. Anything tighter would be a guess presented
//     as a measurement.
//
// A source that reports tool invocations without exporting definitions —
// measured at 14 tools across 368 pi calls, none exported (design.md,
// "Normalization layer") — cannot rank anything, and says so rather than
// reporting a plausible all-zero ranking (R8.5). The refusal can arrive two
// ways through the same seam: inferred from the data's shape (definitions
// empty, invocations present), or declared ahead of the data by the source's
// measurability map (canon/measurability.ts, R10.1) — which also refuses the
// source that supplied no invocations and no definitions, because a source
// that cannot see offered tools cannot claim "nothing was offered" either.

import { schemaRankingAvailability } from '../canon/measurability.js'
import { TOKENS_PER_MILLION } from '../canon/cost.js'
import type { Measurability } from '../canon/types.js'

/**
 * A tool definition the session offered to the model, as the normalization
 * layer recorded it. `server` is ground truth from telemetry (R8.3); it is
 * never derived from `name`, which harnesses prefix with the server's
 * identifier.
 */
export type ToolDefinition = {
  /** The tool's name exactly as the model addresses it, prefix included. */
  name: string
  /** The MCP server the definition came from; absent for built-in tools. */
  server?: string
  /** Tokens the definition's schema contributes to each resident turn. */
  tokens: number
  /**
   * Turns the definition was resident, when the source can say (a server
   * connected mid-session, a tool added late). Defaults to the session's
   * `turns`.
   */
  turnsResident?: number
}

/** One definition's place in the resident-cost ranking. */
export type RankedTool = {
  name: string
  /** Ground-truth MCP server; absent for a harness's built-in tools. */
  server?: string
  /** Resident cost: schema tokens × turns resident (R8.1). */
  cost: number
  /** Whether telemetry recorded at least one invocation of this tool. */
  invoked: boolean
}

/**
 * Per-million prices used to bound the unused-schema cost (R8.4), quoted
 * against the same million tokens as the cost engine's `RateTable` rates so
 * the two never mix units.
 */
export type SchemaCostRates = {
  /** Per-million price of input served from cache; sets the range's floor. */
  cacheReadRate: number
  /** Per-million price of fresh input; sets the range's ceiling. */
  freshInputRate: number
  /** Currency of both rates, when they are published prices. */
  currency?: string
}

/**
 * The default rates: none. With no published price to quote, the range is
 * stated in token residencies — every residency served free from cache at
 * the floor, every residency charged at full price at the ceiling — which
 * per-token rates of 0 and 1 express exactly. `currency` is absent so a
 * consumer renders the bounds as tokens, never as a fabricated currency
 * figure.
 */
export const TOKEN_RESIDENCY_RATES: SchemaCostRates = {
  cacheReadRate: 0,
  freshInputRate: TOKENS_PER_MILLION,
}

/**
 * The cost of never-invoked schemas, as a range (R8.4). `tokenResidencies`
 * is the measurable quantity — never-invoked schema tokens × turns
 * resident, summed — and `floor`/`ceiling` price it under the two cache
 * behaviours the telemetry cannot distinguish: every residency a cache read
 * (cheapest) against every residency fresh input (dearest). The bounds are
 * ordered, so a mis-published table that prices cache reads above fresh
 * input still yields floor ≤ ceiling rather than an inverted range.
 */
export type UnusedSchemaRange = {
  /** Never-invoked schema tokens × turns resident, summed over the session. */
  tokenResidencies: number
  /** Lower bound: every residency charged at the cache-read rate. */
  floor: number
  /** Upper bound: every residency charged at the fresh-input rate. */
  ceiling: number
  /** Currency of the bounds when priced; token residencies otherwise. */
  currency?: string
}

/**
 * The schema-cost analysis of one source (R8). `measurable: false` is the
 * R8.5 answer — the source reported invocations it cannot rank because it
 * exported no definitions — and carries no ranking, no grouping and no
 * range: absent is not zero, and the variant's shape makes rendering a
 * zero-cost table out of it a type error rather than a rendering choice.
 */
export type SchemaCostAnalysis =
  | {
      measurable: true
      /** Every definition, in descending resident cost (R8.1). */
      ranked: RankedTool[]
      /** Definitions never invoked, with their resident cost (R8.2). */
      neverInvoked: RankedTool[]
      /** Resident cost per ground-truth server name (R8.3). */
      byServer: Map<string, number>
      /** Bounds on the never-invoked cost (R8.4). */
      unusedRange: UnusedSchemaRange
      /** Session turns; the residency default each definition assumed. */
      turns: number
    }
  | {
      /** The source reports invocations but no definitions (R8.5). */
      measurable: false
      /** Invocations the source did report — what was refused a ranking. */
      invocationCount: number
      /**
       * Present when the refusal came from the source's own declaration
       * rather than the data's shape (R10.1) — the source said it cannot
       * supply definitions, whatever its spans happened to carry.
       */
      reason?: 'declared_not_measurable'
    }

/** Descending resident cost, then ascending name, so ranks are deterministic. */
function byCostDescending(a: RankedTool, b: RankedTool): number {
  return b.cost - a.cost || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)
}

/**
 * Rank a source's tool definitions by resident cost (R8). Each definition
 * costs its schema tokens times the turns it was resident — the default
 * being every turn of the session — and the ranking orders by that product,
 * ties broken by name. Definitions no turn invoked stay in the ranking and
 * are reported separately (R8.2), because the requirement's question is
 * what to remove, and only the never-invoked portion is removable. Grouping
 * is by each definition's ground-truth `server` field (R8.3): a prefixed
 * `name` is never split, and a built-in tool with no server ranks but does
 * not group. The never-invoked cost is returned as `unusedRange` (R8.4) —
 * priced between the cache-read floor and the fresh-input ceiling when
 * rates are supplied, in token residencies otherwise.
 *
 * A source that reports invocations but no definitions is answered with
 * `measurable: false` and the count it reported (R8.5) — never an empty
 * ranking, which would read as "nothing offered" and render as zero. A
 * session with no tools and no invocations is measurable and empty: nothing
 * was offered, which is a different fact from not being able to tell —
 * unless the source's `measurability` declaration says it cannot supply
 * definitions at all (R10.1), in which case the declaration answers before
 * the data is consulted, even against definitions that contradicted it and
 * even with nothing to count, and the refusal carries
 * `reason: 'declared_not_measurable'`.
 */
export function rankSchemas(
  definitions: ToolDefinition[],
  turns: number,
  invocations: readonly string[],
  rates: SchemaCostRates = TOKEN_RESIDENCY_RATES,
  measurability?: Measurability
): SchemaCostAnalysis {
  const availability = schemaRankingAvailability(measurability)
  const declared = typeof availability === 'object' && availability.availability === 'not_measurable'
  if (declared || (definitions.length === 0 && invocations.length > 0)) {
    return {
      measurable: false,
      invocationCount: invocations.length,
      ...(declared ? { reason: 'declared_not_measurable' as const } : {}),
    }
  }

  const invoked = new Set(invocations)

  const ranked: RankedTool[] = definitions
    .map((definition) => ({
      name: definition.name,
      ...(definition.server !== undefined ? { server: definition.server } : {}),
      cost: definition.tokens * (definition.turnsResident ?? turns),
      invoked: invoked.has(definition.name),
    }))
    .sort(byCostDescending)

  const neverInvoked = ranked.filter((tool) => !tool.invoked)

  // Grouping reads the ground-truth `server` field only (R8.3). The map is
  // rebuilt in descending-cost order so a surface iterating it names the
  // most expensive server first; never-invoked tools count too — the rent
  // is owed whether or not the tool was used.
  const totals = new Map<string, number>()
  for (const tool of ranked) {
    if (tool.server === undefined) continue
    totals.set(tool.server, (totals.get(tool.server) ?? 0) + tool.cost)
  }
  const byServer = new Map(
    [...totals.entries()].sort(([a, costA], [b, costB]) => costB - costA || (a < b ? -1 : a > b ? 1 : 0))
  )

  const tokenResidencies = neverInvoked.reduce((sum, tool) => sum + tool.cost, 0)
  const cacheBound = (tokenResidencies * rates.cacheReadRate) / TOKENS_PER_MILLION
  const freshBound = (tokenResidencies * rates.freshInputRate) / TOKENS_PER_MILLION

  return {
    measurable: true,
    ranked,
    neverInvoked,
    byServer,
    unusedRange: {
      tokenResidencies,
      floor: Math.min(cacheBound, freshBound),
      ceiling: Math.max(cacheBound, freshBound),
      ...(rates.currency !== undefined ? { currency: rates.currency } : {}),
    },
    turns,
  }
}
