// Measurability declarations for KyberDash (spec: docs/specs/kyberdash,
// task 9.4; design.md "`Measurability`", R7.6, R8.5, R10.1, R10.2).
//
// The rule the Python pipeline proved and this port keeps: **absent is not
// zero, and the reason matters.** Every ingest path declares, per metric,
// whether it can measure that metric at all — a declaration independent of
// any value (R10.1) — so a consumer renders "not measurable" in words rather
// than a number that reads as a result (R10.2).
//
// The two ingest paths declare differently, because they can:
//
//   * File-sourced sessions (`source` under the synthesizer's
//     `FILE_SOURCE_PREFIX` namespace) carry token counters, a cost figure
//     and tool *invocation* names — never tool *definitions*, never the
//     turn's message structure. Mapping the `userMessage` blurb upstream
//     keeps onto a content key would present a fragment as a bucket and make
//     context composition claim measurability with a misattributed residual
//     (R7.6), and having no definitions to tokenize leaves R8.5's ranking
//     with nothing to rank — so every canonical content key and
//     `schema_cost` is declared `not_measurable`. The synthesizer stamps the
//     same declaration on every record it emits; `measurabilityFor` is that
//     stamp, and the parent/child hierarchy R9's structure figures read is
//     absent from session files too (a parsed call arrives a parentless
//     root), so `execution_structure` is declared not measurable rather than
//     letting "0 subagents" state a fact the files never carried.
//
//   * OTLP-sourced sessions carry what the harness's telemetry exports, and
//     a full-telemetry harness exports structure and definitions. The
//     baseline therefore declares the canonical metrics measured, except
//     where the harness's adapter declares a gap: `unexportedMetrics` is the
//     adapter's own statement (pi invoked 14 tools across 368 calls while
//     exporting none), and `getMeasurability` reads it so the source-level
//     answer and the records' stamps agree by construction rather than by
//     two tables being kept in sync by hand. Cost stays `derived`: telemetry
//     carries no billing, so any OTLP cost figure is computed from tokens
//     and a published rate table, never read from a counter.
//
// The declaration keys are the canonical per-metric names — `schema_cost`,
// the five `CANONICAL_CONTENT_KEYS`, and the source-level `token_usage`,
// `cost` and `execution_structure` — the same vocabulary records carry and
// the analyses and comparison table read. The compound analysis metrics are
// derived from those keys rather than stored alongside them (R8.5's schema
// ranking needs definitions, R7.6's composition needs structure): a second
// copy of the answer in the map would drift from the first, so
// `schemaRankingAvailability` and `contextCompositionAvailability` are the
// derivation, and the analyses consult them.
//
// `getMeasurability` is the source-level answer a surface consults when
// deciding whether an analysis can be rendered for a source at all; the
// records' own `measurability` maps remain the per-record refinement, and
// the analyses accept a declaration explicitly so both layers flow through
// the same seam. Absent declarations measure nothing about the data — an
// undefined declaration means "no stated limitation", and the analyses fall
// back to what the data's shape supports.

import type { HarnessAdapter } from './adapters/base.js'
import { claudeCodeAdapter } from './adapters/claude-code.js'
import { copilotAdapter } from './adapters/copilot.js'
import { geminiAdapter } from './adapters/gemini.js'
import { piAdapter } from './adapters/pi.js'
import { CANONICAL_CONTENT_KEYS, type Measurability, type MetricAvailability, type NotMeasurable } from './types.js'

// ---------------------------------------------------------------------------
// The file-sourced declarations (R7.6, R8.5)
// ---------------------------------------------------------------------------

/** The source-name namespace the synthesizer stamps on file-sourced records. */
export const FILE_SOURCE_PREFIX = 'codeburn/'

/** True when `source` names a record synthesized from session files. */
export function isFileSource(source: string): boolean {
  return source.startsWith(FILE_SOURCE_PREFIX)
}

/**
 * Default limitations for readers that only expose upstream's parsed-call
 * counters. Provider readers that preserve transcript content override this
 * baseline below; a file source is not inherently unable to measure all
 * message structure.
 */
export const FILE_SOURCE_UNMEASURABLE: readonly string[] = [
  'schema_cost',
  ...CANONICAL_CONTENT_KEYS,
]

/**
 * Per-reader limits for the transcript formats D5 supports. Claude stores
 * conversation and tool-result content but injects prompts and tool schemas
 * at runtime. Codex stores prompts, instructions, conversation, and results,
 * but records tool names rather than definition schemas.
 */
export const READER_UNMEASURABLE: ReadonlyMap<string, readonly string[]> = new Map([
  ['claude', ['schema_cost', 'system_prompt', 'tool_definitions']],
  ['claude-code', ['schema_cost', 'system_prompt', 'tool_definitions']],
  ['codex', ['schema_cost', 'tool_definitions']],
  ['opencode', ['schema_cost', ...CANONICAL_CONTENT_KEYS]],
  ['kilo', ['schema_cost', ...CANONICAL_CONTENT_KEYS]],
  ['kilo-code', ['schema_cost', ...CANONICAL_CONTENT_KEYS]],
  ['copilot', ['schema_cost', ...CANONICAL_CONTENT_KEYS]],
  ['pi', ['schema_cost', 'system_prompt', 'instruction_context', 'tool_definitions', 'tool_result_content']],
])

/**
 * Per-provider additions: counters the provider's files genuinely do not
 * carry, mirroring the harness adapters' `unexportedMetrics` declarations so
 * the file path and the OTLP path state the same limitation for the same
 * harness. Gemini's explicit caching has no cache-creation counter, so a
 * stored 0 there is an absent metric, not a measured zero.
 */
export const PROVIDER_UNMEASURABLE: ReadonlyMap<string, readonly string[]> = new Map([
  ['gemini', ['cache_creation']],
  ['antigravity', ['cache_creation']],
  ['antigravity-cli', ['cache_creation']],
  ['antigravity-ide', ['cache_creation']],
])

function unavailable(reason: string): NotMeasurable {
  return { availability: 'not_measurable', reason }
}

/** Read the discriminant while accepting legacy persisted string declarations. */
function availabilityOf(value: MetricAvailability | undefined): string | undefined {
  return typeof value === 'object' ? value.availability : value
}

/**
 * Display-only Gemini label for selector copy. Gemini is a model/chat identity,
 * not a persisted coding-harness id on derived rows or rollups.
 */
export const GEMINI_SELECTOR_LABEL = 'Gemini'

const EXCLUDED_HARNESS_IDENTITIES = new Set(['gemini'])

/** True when `harness` must not appear as a stored session/run/rollup harness id. */
export function isExcludedHarnessIdentity(harness: string): boolean {
  return EXCLUDED_HARNESS_IDENTITIES.has(harness.trim().toLowerCase())
}

/**
 * Map split client surfaces onto the E4 cache/prefix survey family they inherit.
 * The survey family is telemetry vocabulary, not a persisted identity.
 */
const SURVEY_FAMILY: Readonly<Record<string, string>> = {
  'antigravity-cli': 'antigravity',
  'antigravity-ide': 'antigravity',
  'copilot-cli': 'copilot',
  'copilot-vscode': 'copilot',
  'copilot-jetbrains': 'copilot',
  'copilot-agent': 'copilot',
  'cursor-agent': 'cursor',
  'cline-cli': 'cline',
  'claude-cli': 'claude-code',
  'claude-desktop': 'claude-code',
  'claude-unclassified': 'claude-code',
  'codex-cli': 'codex',
  'codex-desktop': 'codex',
  'codex-unclassified': 'codex',
  'kilo-shared-runtime': 'kilo-code',
  'kilo-vscode-legacy': 'kilo-code',
}

function surveyFamily(harness: string): string {
  const canonical = normalizeHarnessName(harness)
  if (canonical === 'gemini') return 'gemini'
  return SURVEY_FAMILY[canonical] ?? canonical
}

/**
 * Canonical persisted harness id. Split client surfaces stay distinct.
 * Gemini remains the excluded identity string so callers can refuse it;
 * it is never seeded into rollups or derived session/run rows.
 */
export function normalizeHarnessName(harness: string): string {
  const lower = harness.trim().toLowerCase()
  if (lower === 'claude-cli' || lower === 'claude-desktop' || lower === 'claude-unclassified') return lower
  if (lower === 'claude' || lower === 'claude-code') return lower === 'claude' ? 'claude-unclassified' : 'claude-code'
  if (lower === 'copilot-cli' || lower === 'copilot-vscode' || lower === 'copilot-jetbrains' || lower === 'copilot-agent') {
    return lower
  }
  if (lower === 'copilot-chat') return 'copilot-vscode'
  if (lower === 'copilot' || lower === 'github-copilot') return 'copilot'
  if (lower === 'cursor-agent') return 'cursor-agent'
  if (lower === 'cursor') return 'cursor'
  if (lower === 'windsurf' || lower === 'cascade') return 'windsurf'
  if (lower === 'roo' || lower === 'roo-code' || lower === 'roo-cline') return 'roo-code'
  if (lower === 'cline-cli') return 'cline-cli'
  if (lower === 'cline') return 'cline'
  if (lower === 'aider') return 'aider'
  if (lower === 'codex-cli' || lower === 'codex-desktop' || lower === 'codex-unclassified') return lower
  if (lower === 'codex' || lower === 'openai-codex') return lower === 'openai-codex' ? 'codex-unclassified' : 'codex'
  if (lower === 'gemini') return 'gemini'
  if (lower === 'antigravity-cli' || lower === 'antigravity-ide') return lower
  if (lower === 'antigravity' || lower === 'agy') return 'antigravity'
  if (lower === 'opencode') return 'opencode'
  if (lower === 'kilo-vscode-legacy') return 'kilo-vscode-legacy'
  if (lower === 'kilo-shared-runtime' || lower === 'kilo' || lower === 'kilo-code' || lower.startsWith('kilo')) {
    return 'kilo-shared-runtime'
  }
  if (lower === 'kimicode' || lower === 'kimi-code') return 'kimi-code'
  return lower
}

/** Canonical coding-harness id, or `null` when the name is excluded (Gemini). */
export function canonicalHarnessId(harness: string): string | null {
  const id = normalizeHarnessName(harness)
  return isExcludedHarnessIdentity(id) ? null : id
}

export function groupByCanonicalHarness<T extends { harness: string }>(items: readonly T[]): Map<string, T[]> {
  const groups = new Map<string, T[]>()
  for (const item of items) {
    const id = canonicalHarnessId(item.harness)
    if (id === null) continue
    const list = groups.get(id) ?? []
    list.push(item)
    groups.set(id, list)
  }
  return groups
}

/**
 * The id a derived session or execution takes for one canonical harness's
 * share of a session key: the bare key when the key has one canonical harness,
 * `${harness}:${key}` when it has several, so the shares do not collide on one
 * row. `buildSessions`, `buildRuns` and the compaction-hazard detector all mint
 * through here, and `harnessSessionKey` inverts it, so the id a row is written
 * under and the rule that gathers its records back cannot drift apart.
 */
export function harnessSessionId(harness: string, key: string, harnessCount: number): string {
  return harnessCount > 1 ? `${harness}:${key}` : key
}

/**
 * The session key a harness-qualified id was minted from, or `undefined` when
 * the id carries no prefix for `harness`. The harness has to be supplied —
 * from the execution or session row — rather than read off the id: native
 * session ids can themselves contain a colon, so splitting on one would turn a
 * bare key into a harness that does not exist and a key no record carries.
 */
export function harnessSessionKey(sessionId: string, harness: string): string | undefined {
  const prefix = `${harness}:`
  return sessionId.startsWith(prefix) && sessionId.length > prefix.length ? sessionId.slice(prefix.length) : undefined
}

/** The measurability map a file-sourced record declares for its provider. */
export function measurabilityFor(
  provider: string,
  unmeasurable: ReadonlyMap<string, readonly string[]> = PROVIDER_UNMEASURABLE,
): Measurability {
  const family = surveyFamily(provider)
  const metrics = new Set(
    READER_UNMEASURABLE.get(provider) ?? READER_UNMEASURABLE.get(family) ?? FILE_SOURCE_UNMEASURABLE,
  )
  for (const metric of unmeasurable.get(provider) ?? unmeasurable.get(family) ?? []) metrics.add(metric)
  return Object.fromEntries(
    [...metrics].sort().map((metric) => [
      metric,
      unavailable(
        metric === 'cache_creation' &&
        (provider === 'gemini' || surveyFamily(provider) === 'antigravity')
          ? provider === 'gemini'
            ? 'Gemini session files do not export a cache-creation counter.'
            : 'Antigravity conversation stores do not export a cache-creation counter.'
          : metric === 'system_prompt' &&
              (provider === 'claude' ||
                provider === 'claude-code' ||
                surveyFamily(provider) === 'claude-code')
            ? 'Claude Code session files do not store the runtime system_prompt.'
            : metric === 'tool_definitions' &&
                (provider === 'claude' ||
                  provider === 'claude-code' ||
                  surveyFamily(provider) === 'claude-code')
            ? 'Claude Code session files record tool invocations, not tool_definitions.'
              : metric === 'tool_definitions' && surveyFamily(provider) === 'codex'
                ? 'Codex session files record tool names, not tool definition schemas.'
                : `Session files for ${provider} do not include ${metric} data.`,
      ),
    ]),
  )
}

// ---------------------------------------------------------------------------
// The OTLP-sourced baseline (R10.1)
// ---------------------------------------------------------------------------

/**
 * The harness adapters whose `unexportedMetrics` declarations
 * `getMeasurability` reads for the OTLP path. The adapters are the single
 * statement of what a harness's telemetry does not export — the same
 * statement `normalize` stamps on each record — so the baseline consults
 * them instead of carrying a second table a new adapter would have to know
 * to update.
 */
const ADAPTERS_BY_HARNESS: ReadonlyMap<string, HarnessAdapter> = new Map(
  [copilotAdapter, geminiAdapter, piAdapter, claudeCodeAdapter].map((adapter) => [adapter.name, adapter]),
)

// ---------------------------------------------------------------------------
// Deriving the compound analyses' availability (R7.6, R8.5)
// ---------------------------------------------------------------------------

/**
 * Whether R8.5's schema ranking can be measured for a source carrying this
 * declaration. Not measurable when the source cannot supply definitions —
 * declared as the analysis metric (`schema_cost`, the synthesizer's
 * spelling) or as the content key definitions resolve to
 * (`tool_definitions`, the adapters' spelling, the same pair the comparison
 * table's schema-cost row requires). An absent declaration states no
 * limitation, so the ranking proceeds on the data.
 */
export function schemaRankingAvailability(
  measurability: Measurability | undefined,
): MetricAvailability {
  if (measurability === undefined) return 'measured'
  const schemaCost = measurability['schema_cost']
  if (availabilityOf(schemaCost) === 'not_measurable') {
    return typeof schemaCost === 'object'
      ? schemaCost
      : unavailable('This source does not provide schema-cost data.')
  }
  const toolDefinitions = measurability['tool_definitions']
  if (availabilityOf(toolDefinitions) === 'not_measurable') {
    return typeof toolDefinitions === 'object'
      ? toolDefinitions
      : unavailable('This source does not provide tool-definition data.')
  }
  return 'measured'
}

/**
 * Whether R7.6's context composition can be measured for a source carrying
 * this declaration. Not measurable when every canonical content key — the
 * only buckets composition is allowed to chart (R7.1) — is declared not
 * measurable: a source that cannot supply message structure cannot supply
 * part of it either, and a partial declaration leaves the analysis to bucket
 * what does arrive, gaps surfacing as the residual rather than as a refusal.
 * An absent declaration states no limitation.
 */
export function contextCompositionAvailability(
  measurability: Measurability | undefined,
): MetricAvailability {
  if (measurability === undefined) return 'measured'
  const blockedKey = CANONICAL_CONTENT_KEYS.find(
    (key) => availabilityOf(measurability[key]) !== 'not_measurable',
  )
  if (blockedKey !== undefined) return 'measured'
  const unavailableValue = measurability[CANONICAL_CONTENT_KEYS[0]]
  return typeof unavailableValue === 'object'
    ? unavailableValue
    : unavailable('This source does not provide message-structure data.')
}

// ---------------------------------------------------------------------------
// The source-level answer (R10.1)
// ---------------------------------------------------------------------------

/**
 * The measurability declaration for one telemetry source and its voted
 * harness — the answer a surface consults before rendering a metric for
 * that source (R10.1), so an unmeasurable metric is presented in words
 * rather than as a zero (R10.2).
 *
 * The declaration is keyed by the source's ingest path, which its name
 * carries: `codeburn/<provider>` is the synthesizer's file-sourced
 * namespace (`isFileSource`), and any other name arrived as telemetry. The
 * source name contributes nothing here but that path identification —
 * harness attribution stays the registry's fingerprint vote (R6.2), and the
 * per-metric limitations come from the harness's own adapter declaration,
 * never from its name.
 */
export function getMeasurability(source: string, harness: string): Measurability {
  if (isFileSource(source)) {
    // Files measure counters and cost; they cannot measure structure,
    // definitions or the R9 hierarchy. `measurabilityFor` spreads last so a
    // provider-specific declaration always wins over the baseline — every
    // entry it returns is `not_measurable`, and not measurable is the side
    // a conflict must resolve to.
    return {
      token_usage: 'measured',
      cost: 'measured',
      ...measurabilityFor(harness),
      execution_structure: unavailable('Claude Code session files do not carry execution structure.'),
    }
  }

  const declared: Measurability = {
    token_usage: 'measured',
    cost: 'derived',
    schema_cost: 'measured',
    execution_structure: 'measured',
  }
  for (const key of CANONICAL_CONTENT_KEYS) declared[key] = 'measured'
  const adapter = ADAPTERS_BY_HARNESS.get(harness) ?? ADAPTERS_BY_HARNESS.get(surveyFamily(harness))
  for (const metric of adapter?.unexportedMetrics() ?? []) {
    declared[metric] = unavailable(`${harness} telemetry does not export the ${metric} metric.`)
  }
  return declared
}

// ---------------------------------------------------------------------------
// Cache counter and prefix byte survey declarations (Task E4)
// ---------------------------------------------------------------------------

/** Verification level of an observed harness capability. */
export type ConfidenceTag = 'verified' | 'documented' | 'unverified' | 'assumed'

/** Availability status for cache counters or prefix byte support. */
export type CapabilityAvailabilityStatus =
  | 'supported'
  | 'unsupported'
  | 'not_measurable'
  | 'partial'

/** Fallback behavior when prefix bytes cannot be located directly. */
export type PrefixFallback = 'detect-but-cannot-locate' | 'none'

/** Cache counter availability declaration for a harness. */
export type CacheAvailability = {
  harness: string
  status: CapabilityAvailabilityStatus
  confidence: ConfidenceTag
  cacheRead: boolean
  cacheCreation: boolean
  reason: string
}

/** Prefix byte availability declaration for a harness. */
export type PrefixAvailability = {
  harness: string
  status: CapabilityAvailabilityStatus
  confidence: ConfidenceTag
  prefixBytes: boolean
  fallback: PrefixFallback
  reason: string
}

/**
 * The agent harnesses surveyed under Task E4.
 *
 * Antigravity is listed separately from Gemini. It writes under `~/.gemini/`
 * and speaks Gemini's telemetry vocabulary, but it is a distinct harness with
 * its own conversation store and its own agentic surface; folding it into
 * `gemini` reported one harness's rollup under another harness's name.
 */
export const SURVEYED_HARNESSES = [
  'copilot',
  'claude-code',
  'cursor',
  'windsurf',
  'roo-code',
  'cline',
  'aider',
  'codex',
  'gemini',
  'antigravity',
  'opencode',
] as const

export type SurveyedHarness = (typeof SURVEYED_HARNESSES)[number]

const HARNESS_CACHE_SURVEY: ReadonlyMap<SurveyedHarness, Omit<CacheAvailability, 'harness'>> = new Map([
  [
    'copilot',
    {
      status: 'supported',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: true,
      reason:
        'Copilot Chat OTLP exports cache read and creation counters; Copilot CLI preserves ASAD tiers in SQLite.',
    },
  ],
  [
    'claude-code',
    {
      status: 'supported',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: true,
      reason:
        'Claude Code enhanced telemetry exports cache_read_tokens and cache_creation_tokens using Anthropic cache-exclusive convention.',
    },
  ],
  [
    'cursor',
    {
      status: 'unsupported',
      confidence: 'verified',
      cacheRead: false,
      cacheCreation: false,
      reason:
        'Cursor hook JSONL and enterprise OTel export provide token totals without cache read/write counters.',
    },
  ],
  [
    'windsurf',
    {
      status: 'unsupported',
      confidence: 'documented',
      cacheRead: false,
      cacheCreation: false,
      reason:
        'Windsurf Cascade telemetry exports non-model metadata and quarantined windsurf.* attributes without cache counters.',
    },
  ],
  [
    'roo-code',
    {
      status: 'supported',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: true,
      reason:
        'Roo Code ui_messages.json api_req_started events record cacheReads and cacheWrites counters.',
    },
  ],
  [
    'cline',
    {
      status: 'supported',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: true,
      reason:
        'Cline task transcripts (ui_messages.json) record cacheReads and cacheWrites counters.',
    },
  ],
  [
    'aider',
    {
      status: 'unsupported',
      confidence: 'documented',
      cacheRead: false,
      cacheCreation: false,
      reason:
        'Aider displays cache metrics in terminal output via LiteLLM but exports no native OTLP telemetry and records no structured cache counters in chat history.',
    },
  ],
  [
    'codex',
    {
      status: 'supported',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: false,
      reason:
        'Codex rollout session files record input_tokens, output_tokens, and cached_tokens (cache_read); cache creation is implicit.',
    },
  ],
  [
    'gemini',
    {
      status: 'partial',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: false,
      reason:
        'Gemini telemetry exports cached_content_token_count (cache_read); Gemini explicit caching architecture has no cache-creation counter.',
    },
  ],
  [
    'antigravity',
    {
      status: 'partial',
      confidence: 'verified',
      cacheRead: true,
      cacheCreation: false,
      reason:
        'Antigravity conversation stores carry cached_content_token_count (cache_read) in the Gemini counter vocabulary; that architecture has no cache-creation counter.',
    },
  ],
  [
    'opencode',
    {
      status: 'not_measurable',
      confidence: 'documented',
      cacheRead: false,
      cacheCreation: false,
      reason:
        'OpenCode is installed with experimental OpenTelemetry disabled and exports no supported session files.',
    },
  ],
])

const HARNESS_PREFIX_SURVEY: ReadonlyMap<SurveyedHarness, Omit<PrefixAvailability, 'harness'>> = new Map([
  [
    'copilot',
    {
      status: 'supported',
      confidence: 'verified',
      prefixBytes: true,
      fallback: 'detect-but-cannot-locate',
      reason:
        'Content-enabled OTLP capture reconstructs system instructions and message parts; falls back to cache_read / input when capture is off.',
    },
  ],
  [
    'claude-code',
    {
      status: 'not_measurable',
      confidence: 'documented',
      prefixBytes: false,
      fallback: 'detect-but-cannot-locate',
      reason:
        'Session files omit runtime system prompt and tool definitions; raw API body export (OTEL_LOG_RAW_API_BODIES=1) is unconfigured by default.',
    },
  ],
  [
    'cursor',
    {
      status: 'not_measurable',
      confidence: 'verified',
      prefixBytes: false,
      fallback: 'none',
      reason:
        'Cursor hook events emit isolated prompt text without multi-turn prefix reconstruction or cache counter fallback.',
    },
  ],
  [
    'windsurf',
    {
      status: 'not_measurable',
      confidence: 'documented',
      prefixBytes: false,
      fallback: 'none',
      reason:
        'Cascade local storage and telemetry do not export raw request prefix bytes or system prompt boundaries.',
    },
  ],
  [
    'roo-code',
    {
      status: 'not_measurable',
      confidence: 'verified',
      prefixBytes: false,
      fallback: 'detect-but-cannot-locate',
      reason:
        'Conversation history is stored on disk but dynamic template system prompts are not persisted; fallback cache_read / input is available.',
    },
  ],
  [
    'cline',
    {
      status: 'not_measurable',
      confidence: 'verified',
      prefixBytes: false,
      fallback: 'detect-but-cannot-locate',
      reason:
        'Conversation history is stored but runtime prompt template is omitted; fallback cache_read / input is available.',
    },
  ],
  [
    'aider',
    {
      status: 'not_measurable',
      confidence: 'documented',
      prefixBytes: false,
      fallback: 'none',
      reason:
        'Aider chat history (.aider.chat.history.md) lacks structured repo map and system prompt prefix demarcation.',
    },
  ],
  [
    'codex',
    {
      status: 'supported',
      confidence: 'verified',
      prefixBytes: true,
      fallback: 'none',
      reason:
        'Codex rollout files preserve full base_instructions.text, agents_md.text, conversation history, and tool outputs for prefix reconstruction.',
    },
  ],
  [
    'gemini',
    {
      status: 'not_measurable',
      confidence: 'verified',
      prefixBytes: false,
      fallback: 'detect-but-cannot-locate',
      reason:
        'Gemini telemetry exports model operations and tool names without raw prompt prefix bytes; fallback cache_read / input is available.',
    },
  ],
  [
    'antigravity',
    {
      status: 'not_measurable',
      confidence: 'verified',
      prefixBytes: false,
      fallback: 'detect-but-cannot-locate',
      reason:
        'Antigravity conversation stores record model operations and tool names without raw prompt prefix bytes; fallback cache_read / input is available.',
    },
  ],
  [
    'opencode',
    {
      status: 'not_measurable',
      confidence: 'documented',
      prefixBytes: false,
      fallback: 'none',
      reason:
        'OpenCode OpenTelemetry is disabled; no prefix bytes or transcript telemetry are available.',
    },
  ],
])

/**
 * Return the typed cache counter availability and confidence tag for a harness.
 */
export function cacheAvailability(harness: string): CacheAvailability {
  const canonical = normalizeHarnessName(harness)
  const family = surveyFamily(canonical) as SurveyedHarness
  const survey = HARNESS_CACHE_SURVEY.get(family)
  if (survey !== undefined) {
    return { harness: canonical, ...survey }
  }
  return {
    harness: canonical,
    status: 'not_measurable',
    confidence: 'assumed',
    cacheRead: false,
    cacheCreation: false,
    reason: `Harness "${harness}" is not catalogued in the telemetry inventory.`,
  }
}

/**
 * Return the typed prefix byte availability, confidence tag, and fallback mode for a harness.
 */
export function prefixAvailability(harness: string): PrefixAvailability {
  const canonical = normalizeHarnessName(harness)
  const family = surveyFamily(canonical) as SurveyedHarness
  const survey = HARNESS_PREFIX_SURVEY.get(family)
  if (survey !== undefined) {
    return { harness: canonical, ...survey }
  }
  return {
    harness: canonical,
    status: 'not_measurable',
    confidence: 'assumed',
    prefixBytes: false,
    fallback: 'none',
    reason: `Harness "${harness}" is not catalogued in the telemetry inventory.`,
  }
}

/**
 * Return the availability declaration for a specific diagnostic dimension of a harness
 * (Decision D3, ADR 0009, ADR 0011).
 *
 * In accordance with the core measurability discipline: where telemetry is absent
 * (e.g. prefix bytes on Cursor, cache counters on Aider, or tool schemas on raw transcripts),
 * this function emits an explicit `not_measurable` with its empirical reason, never
 * fabricating data or defaulting to a misleading zero.
 */
export function harnessDimensionAvailability(harness: string, dimension: string): MetricAvailability {
  const normalized = normalizeHarnessName(harness)
  const family = surveyFamily(normalized)
  const dim = dimension.trim().toLowerCase()

  // 1. Cache hit rate / cache efficiency: consult the Task E4 cache survey
  if (dim === 'cache_hit_rate' || dim === 'cache_efficiency' || dim === 'cache') {
    const cache = cacheAvailability(normalized)
    if (cache.status === 'unsupported' || cache.status === 'not_measurable') {
      return unavailable(cache.reason)
    }
    return 'measured'
  }

  // 2. Request prefix stability / prefix bytes: consult the Task E4 prefix survey
  if (dim === 'prefix_stability' || dim === 'prefix_bytes' || dim === 'prefix') {
    const prefix = prefixAvailability(normalized)
    if (prefix.status === 'unsupported' || prefix.status === 'not_measurable') {
      return unavailable(prefix.reason)
    }
    return 'measured'
  }

  // 3. Context pressure: requires per-turn input token counters and context limit
  if (
    dim === 'context_pressure' ||
    dim === 'context_pressure_median' ||
    dim === 'context_pressure_p95' ||
    dim === 'context_hygiene' ||
    dim === 'pressure'
  ) {
    if (normalized === 'opencode') {
      return unavailable('OpenCode is installed with experimental OpenTelemetry disabled and exports no supported session files.')
    }
    if (normalized === 'aider') {
      return unavailable('Aider displays terminal metrics via LiteLLM but records no structured context window or per-turn token usage.')
    }
    return 'measured'
  }

  // 4. Tool yield: requires tool definition schemas and invocation tracking
  if (dim === 'tool_yield' || dim === 'tool_definitions' || dim === 'schema_cost') {
    if (family === 'cursor') {
      return unavailable('Cursor hook telemetry does not export tool definition schemas.')
    }
    if (normalized === 'aider') {
      return unavailable('Aider chat history does not export tool definitions or schemas.')
    }
    if (normalized === 'windsurf') {
      return unavailable('Windsurf Cascade telemetry exports non-model metadata without tool definition schemas.')
    }
    if (normalized === 'opencode') {
      return unavailable('OpenCode is installed with experimental OpenTelemetry disabled.')
    }
    if (family === 'claude-code') {
      return unavailable('Claude Code session files record tool invocations, not tool definition schemas; raw API body export is required.')
    }
    if (family === 'codex') {
      return unavailable('Codex session files record tool names, not tool definition schemas.')
    }
    if (normalized === 'pi') {
      return unavailable('pi telemetry does not export tool definitions.')
    }
    return 'measured'
  }

  // 5. Delegation overhead: requires execution hierarchy or subagent parent/child linkage
  if (dim === 'delegation_overhead' || dim === 'execution_structure' || dim === 'delegation') {
    if (family === 'cursor') {
      return unavailable('Cursor does not export execution hierarchy or subagent delegation linkage.')
    }
    if (normalized === 'aider') {
      return unavailable('Aider does not export execution hierarchy or subagent delegation linkage.')
    }
    if (normalized === 'windsurf') {
      return unavailable('Windsurf does not export execution hierarchy or subagent delegation linkage.')
    }
    if (normalized === 'opencode') {
      return unavailable('OpenCode is installed with experimental OpenTelemetry disabled.')
    }
    if (family === 'codex') {
      return unavailable('Codex session files record standalone sessions without execution hierarchy.')
    }
    return 'measured'
  }

  // 6. Field coverage: meta-metric calculated over the set of declarations
  if (dim === 'field_coverage' || dim === 'coverage') {
    return 'measured'
  }

  // 7. Cost: billing basis (derived from token usage or reported)
  if (dim === 'cost') {
    if (normalized === 'opencode') {
      return unavailable('OpenCode is installed with experimental OpenTelemetry disabled.')
    }
    return 'derived'
  }

  // 8. Token usage: basic token counters
  if (dim === 'token_usage') {
    if (normalized === 'opencode') {
      return unavailable('OpenCode is installed with experimental OpenTelemetry disabled.')
    }
    if (normalized === 'aider') {
      return unavailable('Aider displays terminal output via LiteLLM without structured token counters.')
    }
    return 'measured'
  }

  // Uncatalogued harness fallback
  const isCatalogued =
    (SURVEYED_HARNESSES as readonly string[]).includes(normalized) ||
    (SURVEYED_HARNESSES as readonly string[]).includes(family)
  if (!isCatalogued) {
    return unavailable(`Harness "${harness}" is not catalogued in the telemetry inventory.`)
  }

  return 'measured'
}


