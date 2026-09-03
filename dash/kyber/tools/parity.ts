// Parity harness for the migration from the Python pipeline (spec:
// docs/specs/kyberdash, design.md "Testing, end to end" — the parity gate of
// R15; requirements 15.1 and 15.2).
//
// Retiring the Python pipeline must be a verified step, not an act of faith:
// the ported pipeline has to reproduce, over the same span corpus, exactly
// what the old one computed. A digest is what makes that checkable. It is
// deliberately **content-free** — counts, sums, statuses and structure, and
// nothing else. No prompt or message text, no span or trace identifiers, no
// names, no model identifiers: a digest that carried any of those could not
// be committed next to this test, pasted into an issue, or compared across
// machines without moving user data with it, and the comparison is only
// useful if it travels.
//
// The digest is computed by running the ported pipeline — token classes,
// cost blocks through `sumCosts`, context composition through
// `analyzeContext`, schema ranking through `rankSchemas`, the call tree
// through `buildTimeline` — over the corpus, section by section. Two
// derivations follow precedents already set in the analysis layer: a record
// is a turn when its canonical op is `llm.invoke` (the rule `compare.ts`
// states), and a record is a tool invocation when its op is `tool.invoke`.
// Tool definitions are the corpus's `tool_definitions` content — one
// definition per carrying turn, resident exactly that turn, counted by the
// same derived tokenizer seam as every other content-free figure (R4.6).
//
// Authority is structural (R15.2): `compareDigests` takes the Python digest
// first, as the expected side, and the ported digest second, as the actual.
// A difference makes `equal` false, and migration is blocked until the
// difference is resolved — which is this module's entire opinion on the
// matter; how to resolve it belongs to the migration, not the harness.
//
// Determinism is a property of the digest, not a hope: iteration orders are
// fixed (canonical content keys, declared basis and status orders, corpus
// order for sums and turns), there is no clock or randomness anywhere in the
// pipeline it calls, and cost figures are summed in corpus order so the
// comparison is exact equality over identical floating-point arithmetic.

import { measuredInput, sumCosts } from '../canon/cost.js'
import { approximateO200kBase } from '../canon/tokens.js'
import {
  CANONICAL_CONTENT_KEYS,
  type CanonicalContentKey,
  type CanonicalRecord,
  type CostBasis,
  type CostStatus,
  validateTokens,
} from '../canon/types.js'
import {
  analyzeContext,
  type ContextAnalysis,
  type ContextTurn,
} from '../analysis/context.js'
import {
  rankSchemas,
  type SchemaCostAnalysis,
  type ToolDefinition,
} from '../analysis/schema.js'
import {
  AUXILIARY_GROUP_ID,
  buildTimeline,
  subagentSessions,
  type TimelineNode,
} from '../analysis/timeline.js'

// ---------------------------------------------------------------------------
// Digest shape
// ---------------------------------------------------------------------------

/** Aggregated token classes over every record in the corpus (R4.1). */
export type TokenStatsDigest = {
  /** Fresh input + cache read + cache creation + output — never counting `reasoning`, which is a subset of output. */
  totalTokens: number
  freshInput: number
  cacheRead: number
  cacheCreation: number
  output: number
}

/**
 * Cost figures under one basis (R5.1). Bases are never blended, so each is
 * digested separately and `total` is present only when the basis carries at
 * least one priced block — absent is not zero (R5.4).
 */
export type CostBasisDigest = {
  /** Blocks on this basis, priced or not. */
  blocks: number
  /** Blocks carrying a finite figure. */
  priced: number
  /** Block counts by status, every status key present, zero included. */
  statuses: Record<CostStatus, number>
  /** Sum of finite figures; absent when nothing on this basis is priced. */
  total?: number
  /** ISO currency codes of priced blocks, sorted; absent when nothing is priced. */
  currencies?: string[]
}

/** The corpus's cost picture: every basis it carries, plus the one total that exists. */
export type CostStatsDigest = {
  /** Every basis, always all three keys in a fixed order, `blocks: 0` for a basis the corpus never used. */
  byBasis: Record<CostBasis, CostBasisDigest>
  /**
   * The corpus's single total figure, present only when `sumCosts` accepts
   * every block — one basis, one currency. A corpus whose blocks disagree is
   * not averaged into a plausible number; the refusal leaves this absent
   * (R5.1, R5.4).
   */
  totalCost?: { basis: CostBasis; value: number; currency: string }
}

/**
 * Context composition over the corpus's turns (R7) — or the R7.6 answer that
 * composition is not measurable. Buckets are token sums per canonical
 * content key; the residual is explicit and possibly negative; a fresh-input
 * rise that was flagged is counted. Nothing here can name a harness
 * attribute, because the buckets are the canonical keys and nothing else.
 */
export type ContextBucketsDigest =
  | {
      measurable: true
      /** Turns the corpus supplied (records with the turn op). */
      turns: number
      /** Token sums per canonical content key, all five, zero included (R7.1). */
      buckets: Record<CanonicalContentKey, number>
      /** The summed gap between measured input and bucketed tokens (R7.3). */
      residualTotal: number
      /** True when any bucket count was derived by tokenization (R4.6). */
      derivedCounts: boolean
      /** Turns whose fresh-input rise was flagged (R7.5). */
      flaggedTurns: number
    }
  | {
      /** No turn supplied message structure, so no bucketing exists (R7.6). */
      measurable: false
      turns: number
    }

/** Schema-cost ranking over the corpus's tool definitions (R8) — or the R8.5 refusal. */
export type SchemaCostDigest =
  | {
      measurable: true
      /** Ranked definitions, invoked or not. */
      definitionCount: number
      /** Definitions telemetry never recorded an invocation for (R8.2). */
      neverInvokedCount: number
      /** Never-invoked schema tokens × turns resident, summed (R8.4's measurable quantity). */
      tokenResidencies: number
      /** Lower bound: every residency charged at the cache-read rate. */
      floor: number
      /** Upper bound: every residency charged at the fresh-input rate. */
      ceiling: number
      /** Distinct ground-truth servers in the grouping (R8.3). */
      servers: number
    }
  | {
      /** The corpus reports invocations but exported no definitions (R8.5). */
      measurable: false
      /** The invocations that were refused a ranking. */
      invocationCount: number
    }

/** The shape of the call tree `buildTimeline` produced over the corpus (R9). */
export type TimelineDigest = {
  /** Real spans in the tree — synthetic containers excluded. Equals the corpus size unless the tree dropped or invented one, which is exactly what this section is here to catch. */
  spans: number
  /** Children of the session root: declared roots, orphan groups, the auxiliary group. */
  rootChildren: number
  /** Synthetic attribute-keyed groups for spans whose parent never arrived (R9.1). */
  orphanGroups: number
  /** Spans partitioned into the auxiliary group (R9.4). */
  auxiliarySpans: number
  /** Spans carrying subagent evidence, with or without a parent in the input (R9.3). */
  subagentSpans: number
  /** Edges from the session root to the deepest node. */
  maxDepth: number
}

/**
 * The content-free parity digest of one span corpus (R15.1). Every section is
 * a plain JSON value — the digest survives a JSON round trip unchanged, which
 * the test asserts, because a digest that could not be serialized could not
 * travel to where the comparison happens.
 */
export type ParityDigest = {
  /** Records the digest was computed over. */
  recordCount: number
  tokenStats: TokenStatsDigest
  costStats: CostStatsDigest
  contextBuckets: ContextBucketsDigest
  schemaCost: SchemaCostDigest
  timeline: TimelineDigest
  /** Spans the run held out of the corpus as unclaimed (R6.1) — a property of the run, not the corpus, so it arrives through {@link ParityRunContext}. */
  quarantineCount: number
  /** Validation problems over the corpus (R4.3, R4.4), recomputed by default. */
  problemCount: number
}

/**
 * What a corpus cannot say about the run that produced it: spans quarantined
 * before normalization (by definition not in the corpus), problems recorded
 * against records that were rejected rather than stored, and the context
 * window the Python run used. All optional; every default is stated below.
 */
export type ParityRunContext = {
  /** Spans held out as unclaimed (R6.1). Default 0. */
  quarantineCount?: number
  /**
   * The run's recorded problem count, when the run knows it — ingest-time
   * rejections, provider-store problems — and it should stand in for the
   * recomputed validation count. Default: recompute by validating every
   * record's token decomposition (R4.3: validation is a property of the
   * record, orphans included).
   */
  problemCount?: number
  /**
   * Context window in tokens that headroom and pressure are against (R7.4).
   * Must match the window the Python run used, or the sections that read it
   * are not comparing like with like. Default {@link PARITY_CONTEXT_LIMIT}.
   */
  contextLimit?: number
}

// ---------------------------------------------------------------------------
// Fixed orders and harness constants
// ---------------------------------------------------------------------------

/** Every cost basis in a fixed order, so `byBasis` iterates identically everywhere. */
const BASES: readonly CostBasis[] = ['published', 'harness', 'unknown']

/** Every cost status in a fixed order, zero counts included (R5's vocabulary, complete). */
const STATUSES: readonly CostStatus[] = [
  'priced',
  'partial',
  'no_rate',
  'out_of_scope',
  'not_billed',
]

/**
 * The op that makes a record a turn: one model request. The same rule
 * `compare.ts` states; stated here again because the parity harness must not
 * depend on another analysis's private constant.
 */
const TURN_OP = 'llm.invoke'

/** The op that marks a record a tool invocation (the copilot adapter's canonical vocabulary). */
const TOOL_OP = 'tool.invoke'

/**
 * The context window the parity run assumes (R7.4). A Claude-scale window;
 * a Python run against a different window passes its own through
 * {@link ParityRunContext.contextLimit}.
 */
export const PARITY_CONTEXT_LIMIT = 200_000

/** Synthetic timeline ids are parenthesized; a telemetry span id is not (the timeline module's own convention). */
const SYNTHETIC_PREFIX = '('

// ---------------------------------------------------------------------------
// Section derivations
// ---------------------------------------------------------------------------

function tokenStatsDigest(corpus: readonly CanonicalRecord[]): TokenStatsDigest {
  let freshInput = 0
  let cacheRead = 0
  let cacheCreation = 0
  let output = 0
  for (const record of corpus) {
    freshInput += record.tokens.freshInput
    cacheRead += record.tokens.cacheRead
    cacheCreation += record.tokens.cacheCreation
    output += record.tokens.output
  }
  return {
    totalTokens: freshInput + cacheRead + cacheCreation + output,
    freshInput,
    cacheRead,
    cacheCreation,
    output,
  }
}

function costStatsDigest(corpus: readonly CanonicalRecord[]): CostStatsDigest {
  const byBasis = {} as Record<CostBasis, CostBasisDigest>
  for (const basis of BASES) {
    const blocks = corpus.filter((record) => record.cost.basis === basis)
    const priced = blocks.filter(
      (block) => typeof block.cost.value === 'number' && Number.isFinite(block.cost.value)
    )

    const statuses = {} as Record<CostStatus, number>
    for (const status of STATUSES) {
      statuses[status] = blocks.filter((block) => block.cost.status === status).length
    }

    // `total` and `currencies` exist only when a priced block does: an
    // unpriced basis has no figure, and writing 0 / [] would read as one.
    byBasis[basis] = {
      blocks: blocks.length,
      priced: priced.length,
      statuses,
      ...(priced.length > 0
        ? {
            total: priced.reduce((sum, block) => sum + (block.cost.value as number), 0),
            currencies: [
              ...new Set(
                priced
                  .map((block) => block.cost.currency)
                  .filter((currency): currency is string => typeof currency === 'string')
              ),
            ].sort(),
          }
        : {}),
    }
  }

  // The one total, only when sumCosts accepts every block. A corpus with
  // mixed bases or currencies is refused — never blended (R5.1).
  const summed = sumCosts(corpus.map((record) => record.cost))
  const totalCost =
    summed.ok &&
    typeof summed.total.value === 'number' &&
    Number.isFinite(summed.total.value) &&
    typeof summed.total.currency === 'string'
      ? {
          basis: summed.total.basis,
          value: summed.total.value,
          currency: summed.total.currency,
        }
      : undefined

  return { byBasis, ...(totalCost !== undefined ? { totalCost } : {}) }
}

/** A record's canonical content, as analysis parts; the same mapping every turn takes. */
function turnOf(record: CanonicalRecord): ContextTurn {
  return {
    parts: CANONICAL_CONTENT_KEYS.filter(
      (key) => typeof record.content[key] === 'string' && record.content[key] !== ''
    ).map((key) => ({ part: key, text: record.content[key] as string })),
    inputTokens: measuredInput(record.tokens),
    freshInput: record.tokens.freshInput,
  }
}

function contextBucketsDigest(
  corpus: readonly CanonicalRecord[],
  contextLimit: number
): ContextBucketsDigest {
  const turns = corpus.filter((record) => record.op === TURN_OP).map(turnOf)
  const analysis: ContextAnalysis = analyzeContext(turns, {
    contextLimit,
    // The derived seam, named: the corpus carries no measured per-part
    // counts, so bucket counts derive through the same approximation every
    // other content-free figure uses (R4.6), deterministically.
    countTokens: approximateO200kBase,
  })

  if (!analysis.measurable) {
    return { measurable: false, turns: analysis.turns }
  }

  return {
    measurable: true,
    turns: analysis.turns.length,
    buckets: Object.fromEntries(
      CANONICAL_CONTENT_KEYS.map((key) => [key, analysis.turns.reduce((sum, turn) => sum + turn.buckets[key], 0)])
    ) as Record<CanonicalContentKey, number>,
    residualTotal: analysis.residualTotal,
    derivedCounts: analysis.derivedCounts,
    flaggedTurns: analysis.flaggedTurns.length,
  }
}

function schemaCostDigest(corpus: readonly CanonicalRecord[]): SchemaCostDigest {
  const turnRecords = corpus.filter((record) => record.op === TURN_OP)

  // One definition per turn that carried tool-definition content, resident
  // exactly that turn. The name is the span id — unique per corpus — and is
  // consumed by the ranking's invocation match, never emitted into the
  // digest. No server is guessed (R8.3): the corpus model carries no
  // ground-truth server for content text.
  const definitions: ToolDefinition[] = []
  for (const record of turnRecords) {
    const text = record.content.tool_definitions
    if (typeof text === 'string' && text !== '') {
      definitions.push({
        name: record.spanId,
        tokens: approximateO200kBase(text),
        turnsResident: 1,
      })
    }
  }

  const invocations = corpus.filter((record) => record.op === TOOL_OP).map((record) => record.name)
  const analysis: SchemaCostAnalysis = rankSchemas(definitions, turnRecords.length, invocations)

  if (!analysis.measurable) {
    return { measurable: false, invocationCount: analysis.invocationCount }
  }

  return {
    measurable: true,
    definitionCount: analysis.ranked.length,
    neverInvokedCount: analysis.neverInvoked.length,
    tokenResidencies: analysis.unusedRange.tokenResidencies,
    floor: analysis.unusedRange.floor,
    ceiling: analysis.unusedRange.ceiling,
    servers: analysis.byServer.size,
  }
}

function timelineDigest(corpus: readonly CanonicalRecord[]): TimelineDigest {
  const root = buildTimeline([...corpus])

  let spans = 0
  let auxiliarySpans = 0
  let maxDepth = 0
  const walk = (node: TimelineNode, depth: number): void => {
    if (node.isAuxiliary) auxiliarySpans += 1
    if (!node.spanId.startsWith(SYNTHETIC_PREFIX)) spans += 1
    if (depth > maxDepth) maxDepth = depth
    for (const child of node.children) walk(child, depth + 1)
  }
  walk(root, 0)

  return {
    spans,
    rootChildren: root.children.length,
    // Root children are real roots plus the parenthesized synthetic groups;
    // every group that is not the auxiliary group is an orphan group.
    orphanGroups: root.children.filter(
      (child) => child.spanId.startsWith(SYNTHETIC_PREFIX) && child.spanId !== AUXILIARY_GROUP_ID
    ).length,
    auxiliarySpans,
    subagentSpans: subagentSessions(root).length,
    maxDepth,
  }
}

// ---------------------------------------------------------------------------
// The digest
// ---------------------------------------------------------------------------

/**
 * Run the ported pipeline over a span corpus and digest it, content-free
 * (R15.1). The corpus is the records the Python run's corpus held, in session
 * order — the same order the Python pipeline processed them — because turn
 * sequence is an input to the context analysis and summation order is an
 * input to the cost figures.
 *
 * The pipeline invoked is exactly the ported layer: `validateTokens` over
 * every record, token-class sums, `sumCosts` per basis and whole, context
 * composition per turn, schema ranking over tool-definition content, and the
 * timeline tree. Nothing reads a clock, a store, or the network, so the same
 * corpus digests to the same value every time.
 */
export function computeDigest(
  corpus: CanonicalRecord[],
  run: ParityRunContext = {}
): ParityDigest {
  let problemCount = 0
  for (const record of corpus) {
    if (!validateTokens(record.tokens, record.spanId).valid) problemCount += 1
  }

  return {
    recordCount: corpus.length,
    tokenStats: tokenStatsDigest(corpus),
    costStats: costStatsDigest(corpus),
    contextBuckets: contextBucketsDigest(corpus, run.contextLimit ?? PARITY_CONTEXT_LIMIT),
    schemaCost: schemaCostDigest(corpus),
    timeline: timelineDigest(corpus),
    quarantineCount: run.quarantineCount ?? 0,
    problemCount: run.problemCount ?? problemCount,
  }
}

// ---------------------------------------------------------------------------
// Comparison
// ---------------------------------------------------------------------------

/** The outcome of `compareDigests`: equality, and where it broke. */
export type DigestComparison = {
  equal: boolean
  /**
   * One entry per diverging leaf, in the declared section order of the
   * authoritative digest — each a dotted path from the section
   * (`tokenStats.totalTokens`, `costStats.byBasis.published.total`) with the
   * two values, or a `present in … only` note when a leaf exists on one
   * side. The first segment of every path is the section that diverged.
   */
  diff: string[]
}

const isPlainObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const show = (value: unknown): string =>
  value === undefined ? 'absent' : JSON.stringify(value)

function compareLeaves(python: unknown, ported: unknown, path: string, diff: string[]): void {
  if (isPlainObject(python) && isPlainObject(ported)) {
    // The authoritative digest's key order is the report order — the
    // declared section order a reader of a failed parity run expects. Keys
    // existing only on the ported side follow, sorted, so the order stays
    // fully deterministic.
    const keys = [
      ...Object.keys(python),
      ...Object.keys(ported).filter((key) => !(key in python)).sort(),
    ]
    for (const key of keys) {
      const next = path === '' ? key : `${path}.${key}`
      if (!(key in python)) {
        diff.push(`${next}: present in ported only`)
      } else if (!(key in ported)) {
        diff.push(`${next}: present in python only`)
      } else {
        compareLeaves(python[key], ported[key], next, diff)
      }
    }
    return
  }

  if (Array.isArray(python) && Array.isArray(ported)) {
    const length = Math.max(python.length, ported.length)
    for (let index = 0; index < length; index += 1) {
      const next = `${path}[${index}]`
      if (index >= python.length) {
        diff.push(`${next}: present in ported only`)
      } else if (index >= ported.length) {
        diff.push(`${next}: present in python only`)
      } else {
        compareLeaves(python[index], ported[index], next, diff)
      }
    }
    return
  }

  if (python !== ported) {
    diff.push(`${path}: python=${show(python)} ported=${show(ported)}`)
  }
}

/**
 * Compare a Python pipeline digest against the ported pipeline's digest
 * (R15.1, R15.2). The Python digest is the first argument on purpose: it is
 * the authoritative side, the expected value the ported pipeline must
 * reproduce, and `diff` phrases every entry from its point of view. A digest
 * pair that differs makes `equal` false — which is the gate: migration does
 * not proceed past a difference, and the entries say exactly where to look.
 */
export function compareDigests(python: ParityDigest, ported: ParityDigest): DigestComparison {
  const diff: string[] = []
  compareLeaves(python, ported, '', diff)
  return { equal: diff.length === 0, diff }
}
