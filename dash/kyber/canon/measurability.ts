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
import { copilotAdapter } from './adapters/copilot.js'
import { geminiAdapter } from './adapters/gemini.js'
import { piAdapter } from './adapters/pi.js'
import { CANONICAL_CONTENT_KEYS, type Measurability, type MetricAvailability } from './types.js'

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
 * Metrics no session file can measure, declared on every file-sourced
 * record. A parsed call carries counters and tool *invocation* names —
 * never tool *definitions*, never the session's message structure. The five
 * canonical content keys cover the structure half of that (R7.6, and
 * `tool_definitions` among them is R8.5's schema-ranking input);
 * `schema_cost` follows from having no definitions to tokenize. The analyses
 * read these declarations and answer not measurable rather than zero.
 */
export const FILE_SOURCE_UNMEASURABLE: readonly string[] = [
  'schema_cost',
  ...CANONICAL_CONTENT_KEYS,
]

/**
 * Per-provider additions: counters the provider's files genuinely do not
 * carry, mirroring the harness adapters' `unexportedMetrics` declarations so
 * the file path and the OTLP path state the same limitation for the same
 * harness. Gemini's explicit caching has no cache-creation counter, so a
 * stored 0 there is an absent metric, not a measured zero.
 */
export const PROVIDER_UNMEASURABLE: ReadonlyMap<string, readonly string[]> = new Map([
  ['gemini', ['cache_creation']],
])

/** The measurability map a file-sourced record declares for its provider. */
export function measurabilityFor(
  provider: string,
  unmeasurable: ReadonlyMap<string, readonly string[]> = PROVIDER_UNMEASURABLE,
): Measurability {
  const metrics = new Set(FILE_SOURCE_UNMEASURABLE)
  for (const metric of unmeasurable.get(provider) ?? []) metrics.add(metric)
  return Object.fromEntries([...metrics].sort().map((metric) => [metric, 'not_measurable']))
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
  [copilotAdapter, geminiAdapter, piAdapter].map((adapter) => [adapter.name, adapter]),
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
  return measurability['schema_cost'] === 'not_measurable' ||
    measurability['tool_definitions'] === 'not_measurable'
    ? 'not_measurable'
    : 'measured'
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
  const blocked = CANONICAL_CONTENT_KEYS.every((key) => measurability[key] === 'not_measurable')
  return blocked ? 'not_measurable' : 'measured'
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
      execution_structure: 'not_measurable',
    }
  }

  const declared: Measurability = {
    token_usage: 'measured',
    cost: 'derived',
    schema_cost: 'measured',
    execution_structure: 'measured',
  }
  for (const key of CANONICAL_CONTENT_KEYS) declared[key] = 'measured'
  const adapter = ADAPTERS_BY_HARNESS.get(harness)
  for (const metric of adapter?.unexportedMetrics() ?? []) declared[metric] = 'not_measurable'
  return declared
}
