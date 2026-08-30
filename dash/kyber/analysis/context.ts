// Context composition and pressure for KyberDash, ported from the Python
// pipeline's `views.py` (spec: docs/specs/kyberdash, design.md "Analysis
// layer", R7).
//
// A session that burned an unexpected number of tokens is diagnosed from what
// was resident in the context window, and "what was resident" is a fact about
// parts, not roles (R7.2): the system prompt is the system prompt whether the
// harness carried it in a `system` message or a preface block, and file
// contents are file contents whether they arrived in a tool result or were
// pasted into a user turn. This module therefore consumes parts already typed
// by part type — the canonical content keys of the record model — and its
// input type has no role field to bucket on even by accident. The buckets are
// exactly the five R7.1 names, and a part lands in its own bucket whatever
// message carried it in.
//
// Three facts about the data keep this module honest:
//
//   - Instruction blocks embedded in other content — the `<system-reminder>`
//     fences harnesses re-inject inside history and tool results — are
//     instruction context no matter where they sit, so they are stripped from
//     their carrier bucket and counted in `instruction_context` (R7.1). The
//     split is derived even when the carrier's whole-part count was measured,
//     and a derived split is what makes the turn's residual attributable to
//     tokenizer drift, because that is then the actual cause (R7.3).
//   - Tool-definition parts resolve to MCP servers only through the
//     ground-truth `server` field the record carries. Harnesses prefix tool
//     names with server identifiers, and splitting on the delimiter is the
//     implementation this module refuses (R8.3's rule, applied here too):
//     delimiters appear inside real server names. Built-in tools with no
//     server still occupy the bucket and are reported separately, never
//     guessed into a group.
//   - Buckets rarely account for the whole measured input, and the gap is
//     exposed explicitly as the residual (R7.3) rather than absorbed. The
//     residual is attributed to tokenizer drift only when some count in the
//     turn was derived by tokenization; with every count measured the gap is
//     content the source never exported, and calling that "drift" would be a
//     guess presented as a diagnosis.
//
// A source that cannot supply message structure — no parts on any turn — is
// answered with `measurable: false` (R7.6): composition is not measurable for
// that source, and the variant's shape makes charting a residual out of it a
// type error rather than a rendering choice, because a zero-bucket chart with
// a 100% "residual" would be read as tokenizer drift and would be nothing of
// the sort.

import { contextCompositionAvailability } from '../canon/measurability.js'
import { CANONICAL_CONTENT_KEYS, type CanonicalContentKey, type Measurability } from '../canon/types.js'
import { approximateO200kBase } from '../canon/tokens.js'

/**
 * One piece of a turn's input, typed by part type — never by message role
 * (R7.2). The record model's canonical content keys are the only names a part
 * may carry; the normalization layer decides which key a harness's content
 * maps to, and this module never second-guesses it.
 */
export type ContextPart = {
  /** The part type; one of the five R7.1 bucket names. */
  part: CanonicalContentKey
  /** The part's text, as the normalization layer reconstructed it. */
  text: string
  /**
   * The part's token count when a counter measured it. Absent means the
   * count must be derived by tokenization, and a derived count is a lower
   * bound (R4.6) — which is also what makes a residual attributable to
   * tokenizer drift (R7.3).
   */
  tokens?: number
  /**
   * Ground-truth MCP server for `tool_definitions` parts, from telemetry.
   * Never derived from the tool's name; built-in tools carry no server and
   * are reported separately rather than grouped by a guess.
   */
  server?: string
}

/** One turn of a session, as the analysis consumes it. */
export type ContextTurn = {
  /** The turn's parts, any order; bucketing does not depend on it. */
  parts: readonly ContextPart[]
  /**
   * The turn's measured total input — `freshInput + cacheRead +
   * cacheCreation` in the record's token usage (R4.1). The residual is the
   * gap between this figure and what the buckets account for.
   */
  inputTokens: number
  /** The turn's fresh (non-cached) input, for the sharp-rise flag (R7.5). */
  freshInput: number
}

/**
 * Delimiters of an instruction block embedded in another bucket's content.
 * The defaults are Claude-style system-reminder fences; a harness with
 * different conventions overrides them rather than pre-stripping its
 * content, so the strip stays visible in this module's output.
 */
export type InstructionBlockMarkers = { open: string; close: string }

export const SYSTEM_REMINDER_MARKERS: InstructionBlockMarkers = {
  open: '<system-reminder>',
  close: '</system-reminder>',
}

/**
 * A fresh-input rise is sharp when it multiplies the previous turn's fresh
 * input by at least this factor (R7.5): the visible signature of a cache
 * invalidation is the same content billed as fresh input again, so the jump
 * that matters is multiplicative, not additive.
 */
export const DEFAULT_FRESH_JUMP_FACTOR = 2

/**
 * A rise from zero fresh input is flagged only above this floor. Every
 * session's first nontrivial turn jumps from zero, and flagging turn 2 for
 * "rising" from nothing would bury the real invalidations; a cold-cache first
 * burst above this many tokens is still sharp, a 12-token greeting is not.
 */
export const DEFAULT_ZERO_PREVIOUS_FLOOR = 1_000

/** How a turn's token counts were obtained, when it matters to the residual. */
export type ContextCountOptions = {
  /** Context window size in tokens; headroom and pressure are against it. */
  contextLimit: number
  /** Sharp-rise factor for R7.5; defaults to {@link DEFAULT_FRESH_JUMP_FACTOR}. */
  freshJumpFactor?: number
  /** Fresh-input floor for rises from zero; defaults to the 1_000 above. */
  zeroPreviousFloor?: number
  /** Instruction-block delimiters; defaults to system-reminder fences. */
  instructionMarkers?: InstructionBlockMarkers
  /**
   * The tokenizer used when a part carries no measured count. A sync seam on
   * purpose: the memoized `tokenize` of the canon layer is async, so callers
   * that want its cache resolve counts there and pass them in as measured
   * facts. Defaults to the `o200k_base` approximation (R4.6).
   */
  countTokens?: (text: string) => number
  /**
   * The source's measurability declaration (R10.1) — `getMeasurability` at
   * the source level, or the records' own maps merged. When it declares the
   * source cannot supply message structure (every canonical content key not
   * measurable), the analysis answers not measurable before computing
   * anything (R7.6), whatever the turns happen to carry: a declaration is
   * the source's statement about what it can supply, and rendering buckets
   * from fragments that contradicted it would present a partial export as a
   * composition chart with a residual read as tokenizer drift.
   */
  measurability?: Measurability
}

/** Why a residual exists, when the buckets do not cover the whole input. */
export type ResidualAttribution = 'tokenizer_drift' | 'unattributed'

/** One turn's context composition and pressure figures (R7.1–R7.5). */
export type TurnPressure = {
  /** 1-based position of the turn in the session. */
  index: number
  /** Token totals per part type; all five buckets, zero included (R7.1). */
  buckets: Record<CanonicalContentKey, number>
  /**
   * Tool-definition tokens per ground-truth MCP server. Built-in tools are
   * never guessed into a group; theirs are in `builtinToolDefinitionTokens`.
   */
  toolDefinitionsByServer: Map<string, number>
  /** Tool-definition tokens with no server to resolve to (harness built-ins). */
  builtinToolDefinitionTokens: number
  /** Instruction blocks stripped out of other buckets this turn (R7.1). */
  strippedInstructionBlocks: { count: number; tokens: number }
  /** Sum of the five buckets; what the composition accounts for. */
  bucketedTokens: number
  /**
   * The gap between `inputTokens` and `bucketedTokens`, explicit and
   * possibly negative (an over-count is a finding, not noise). Attribution
   * is `tokenizer_drift` only when some count in the turn was derived
   * (R7.3); a measured-only turn's gap is content the source never
   * exported, which is a different fact.
   */
  residual: { tokens: number; attribution: ResidualAttribution }
  /** Context window minus the turn's measured input; negative when over. */
  headroom: number
  /** The turn's input as a fraction of the window; 1 is exactly full. */
  pressure: number
  /**
   * Input growth since the previous turn in tokens/turn; the first turn
   * reports its whole input, which is what it grew from — nothing.
   */
  accumulationRate: number
  /** The turn's fresh input, carried for the R7.5 view. */
  freshInput: number
  /** Present only on turns whose fresh-input rise was flagged (R7.5). */
  freshInputJump?: { previous: number; factor: number }
}

/** The context analysis of one session (R7), or its not-measurable answer. */
export type ContextAnalysis =
  | {
      measurable: true
      /** The window size every headroom and pressure figure is against. */
      contextLimit: number
      /** Composition and pressure per turn, session order (R7.1, R7.4). */
      turns: TurnPressure[]
      /** Summed residual over the session, as explicit as per turn (R7.3). */
      residualTotal: number
      /** True when any bucket count was derived; present totals as bounds. */
      derivedCounts: boolean
      /** The factor jumps were flagged against, carried for the view. */
      freshJumpFactor: number
      /** Indices of turns whose fresh-input rise was flagged (R7.5). */
      flaggedTurns: number[]
      /** Mean input growth across the session in tokens/turn (R7.4). */
      sessionAccumulationRate: number
    }
  | {
      /** No turn supplied message structure, or the source declared it cannot. */
      measurable: false
      /**
       * `no_message_structure` when no turn carried parts;
       * `declared_not_measurable` when the source's measurability
       * declaration refused ahead of the data (R10.1).
       */
      reason: 'no_message_structure' | 'declared_not_measurable'
      /** Turns the session had — what was refused a composition. */
      turns: number
      contextLimit: number
    }

function emptyBuckets(): Record<CanonicalContentKey, number> {
  const buckets = {} as Record<CanonicalContentKey, number>
  for (const key of CANONICAL_CONTENT_KEYS) buckets[key] = 0
  return buckets
}

/**
 * Split `text` into the instruction blocks embedded in it and the remainder.
 * Non-overlapping, first-match-wins scanning over the marker pair: content
 * after an unterminated `open` is kept in the remainder rather than swallowed
 * wholesale, because a malformed fence is carrier content, not a silent drop.
 */
function stripInstructionBlocks(
  text: string,
  markers: InstructionBlockMarkers
): { blocks: string[]; remainder: string } {
  const blocks: string[] = []
  let remainder = ''
  let at = 0
  for (;;) {
    const open = text.indexOf(markers.open, at)
    if (open === -1) break
    const close = text.indexOf(markers.close, open + markers.open.length)
    if (close === -1) {
      remainder += text.slice(at)
      return { blocks, remainder }
    }
    blocks.push(text.slice(open + markers.open.length, close))
    remainder += text.slice(at, open)
    at = close + markers.close.length
  }
  remainder += text.slice(at)
  return { blocks, remainder }
}

/**
 * Analyze a session's context composition and pressure (R7).
 *
 * Each turn is bucketed by part type across the five canonical buckets
 * (R7.1), with instruction-block fences stripped out of history and tool
 * results into the instruction bucket, and tool definitions resolved to MCP
 * servers only through ground truth (never a split of the tool name). What
 * the buckets do not account for is returned as an explicit residual whose
 * attribution names tokenizer drift only when a derived count could actually
 * cause it (R7.3). Headroom, per-turn accumulation and pressure are computed
 * against `contextLimit` (R7.4), and turns whose fresh input rose sharply —
 * at least `freshJumpFactor` times the previous turn, or from zero past
 * `zeroPreviousFloor` — carry `freshInputJump` and are listed in
 * `flaggedTurns` (R7.5).
 *
 * A session no turn of which supplied parts — a source that cannot export
 * message structure — is answered `measurable: false` with the turn count
 * (R7.6): no buckets, no residual, nothing a surface could render as a
 * composition chart or read as tokenizer drift. The same answer, with
 * `reason: 'declared_not_measurable'`, comes back when the source's
 * `options.measurability` declaration refuses composition ahead of the data
 * (R10.1) — including when some turn did carry parts, because a source that
 * declared the inability is not rendered from fragments that leaked through.
 */
export function analyzeContext(turns: readonly ContextTurn[], options: ContextCountOptions): ContextAnalysis {
  const contextLimit = options.contextLimit
  if (!Number.isFinite(contextLimit) || contextLimit <= 0) {
    throw new RangeError(`contextLimit must be a positive token count (got ${contextLimit})`)
  }
  const freshJumpFactor = options.freshJumpFactor ?? DEFAULT_FRESH_JUMP_FACTOR
  const zeroPreviousFloor = options.zeroPreviousFloor ?? DEFAULT_ZERO_PREVIOUS_FLOOR
  const markers = options.instructionMarkers ?? SYSTEM_REMINDER_MARKERS
  const countTokens = options.countTokens ?? approximateO200kBase

  // The declaration is consulted before the data's shape: a source that
  // cannot supply structure is refused on its word, not on whether parts
  // happened to arrive this run (R7.6, R10.1).
  const declared = contextCompositionAvailability(options.measurability) === 'not_measurable'
  const hasStructure = turns.some((turn) =>
    turn.parts.some((part) => part.text !== '' || part.tokens !== undefined)
  )
  if (declared || !hasStructure) {
    return {
      measurable: false,
      reason: declared ? 'declared_not_measurable' : 'no_message_structure',
      turns: turns.length,
      contextLimit,
    }
  }

  const perTurn: TurnPressure[] = []
  const flaggedTurns: number[] = []
  let residualTotal = 0
  let derivedCounts = false

  for (const [position, turn] of turns.entries()) {
    const buckets = emptyBuckets()
    const toolDefinitionsByServer = new Map<string, number>()
    let builtinToolDefinitionTokens = 0
    let strippedCount = 0
    let strippedTokens = 0
    let derivedThisTurn = false

    for (const part of turn.parts) {
      // Instruction fences ride inside history and tool results; stripping
      // them anywhere else would miscount a system prompt that happens to
      // quote a fence, so the strip applies to the carrier buckets only.
      const strippable = part.part === 'conversation_history' || part.part === 'tool_result_content'
      const containsMarkers =
        strippable && part.text.includes(markers.open) && part.text.includes(markers.close)
      let tokens: number

      if (containsMarkers) {
        // A measured whole-part count cannot be split without deriving the
        // pieces, so the split is always derived — and that derivation is
        // precisely what a tokenizer-drift attribution of the turn's
        // residual would be claiming (R7.3).
        const { blocks, remainder } = stripInstructionBlocks(part.text, markers)
        const remainderTokens = countTokens(remainder)
        buckets[part.part] += remainderTokens
        derivedThisTurn = true
        let partTokens = remainderTokens
        for (const block of blocks) {
          const blockTokens = countTokens(block)
          buckets.instruction_context += blockTokens
          strippedCount += 1
          strippedTokens += blockTokens
          partTokens += blockTokens
        }
        tokens = partTokens
      } else {
        tokens = part.tokens ?? countTokens(part.text)
        if (part.tokens === undefined) derivedThisTurn = true
        buckets[part.part] += tokens
      }

      // Server resolution reads ground truth only (the schema-cost rule,
      // R8.3): the prefixed tool name is never split, and a built-in's
      // tokens are counted, not guessed into a group.
      if (part.part === 'tool_definitions') {
        if (part.server === undefined) {
          builtinToolDefinitionTokens += tokens
        } else {
          toolDefinitionsByServer.set(
            part.server,
            (toolDefinitionsByServer.get(part.server) ?? 0) + tokens
          )
        }
      }
    }

    const bucketedTokens = CANONICAL_CONTENT_KEYS.reduce((sum, key) => sum + buckets[key], 0)
    const residual = turn.inputTokens - bucketedTokens
    const attribution: ResidualAttribution = derivedThisTurn ? 'tokenizer_drift' : 'unattributed'
    if (derivedThisTurn) derivedCounts = true
    residualTotal += residual

    const previous = position > 0 ? turns[position - 1] : undefined
    const accumulationRate = previous === undefined ? turn.inputTokens : turn.inputTokens - previous.inputTokens

    let freshInputJump: TurnPressure['freshInputJump']
    if (previous !== undefined) {
      const previousFresh = previous.freshInput
      const sharpRise =
        turn.freshInput > 0 &&
        ((previousFresh > 0 && turn.freshInput >= freshJumpFactor * previousFresh) ||
          (previousFresh === 0 && turn.freshInput >= zeroPreviousFloor))
      if (sharpRise) {
        freshInputJump = {
          previous: previousFresh,
          factor: previousFresh > 0 ? turn.freshInput / previousFresh : Infinity,
        }
      }
    }

    perTurn.push({
      index: position + 1,
      buckets,
      toolDefinitionsByServer,
      builtinToolDefinitionTokens,
      strippedInstructionBlocks: { count: strippedCount, tokens: strippedTokens },
      bucketedTokens,
      residual: { tokens: residual, attribution },
      headroom: contextLimit - turn.inputTokens,
      pressure: turn.inputTokens / contextLimit,
      accumulationRate,
      freshInput: turn.freshInput,
      ...(freshInputJump !== undefined ? { freshInputJump } : {}),
    })
    if (freshInputJump !== undefined) flaggedTurns.push(position + 1)
  }

  const last = turns[turns.length - 1].inputTokens
  const first = turns[0].inputTokens
  const sessionAccumulationRate = turns.length > 1 ? (last - first) / (turns.length - 1) : first

  return {
    measurable: true,
    contextLimit,
    turns: perTurn,
    residualTotal,
    derivedCounts,
    freshJumpFactor,
    flaggedTurns,
    sessionAccumulationRate,
  }
}
