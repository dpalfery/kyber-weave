// Provider failure handling for the session-file ingest path (spec:
// docs/specs/kyberdash, task 9.2; requirements 1.2, 1.3; design.md "Error
// Handling" table, first two rows).
//
// Upstream's parser swallows failures inside each provider adapter (the bare
// `catch` blocks of dash/src/providers/index.ts): a store that cannot be read
// quietly becomes a provider with zero data. For upstream's single-purpose
// report that is merely invisible; for KyberDash it would violate the
// governing principle that a failure the system cannot interpret is surfaced,
// never silently dropped. This module is the per-provider boundary where the
// Error Handling table's first two rows are decided, explicitly:
//
//   * R1.2 — a provider whose session store is absent is omitted silently.
//     Most machines do not run most agents, so "store not present" is the
//     ordinary shape of a first run, not an error. Absence is signalled by
//     the loader returning null/undefined or by a Node fs `ENOENT` (returned
//     or thrown): a negative existence check, never a read or parse failure.
//   * R1.3 — a store that exists but cannot be parsed records one
//     `PROVIDER_PARSE_ERROR` problem naming the provider and the file, and
//     the run continues with every other provider. One corrupt rollout.json
//     narrows the corpus; it must not cost the operator the other thirty
//     providers' data.
//
// The loader seam keeps this module pure — no paths, no env overrides, no
// discovery (task 10.1 wires the real loader over upstream's parser). It
// hands back already-parsed calls and this module applies the three-way
// split: a value (synthesize through 9.1's Synthesizer, so both cold-parse
// paths stay one data path), an absence (omit), or a failure (record and
// continue). Successful loads are byte-identical to calling the Synthesizer
// directly; failure handling adds problems, never guesses at data.

import type { ParsedProviderCall } from '../../src/providers/types.js'
import type { CanonicalRecord, Problem } from '../canon/types.js'
import { claudeReader } from './readers/claude.js'
import { copilotCliReader, loadCopilotCliCalls } from './readers/copilot.js'
import { codexReader } from './readers/codex.js'
import { kiloReader } from './readers/kilo.js'
import { opencodeReader } from './readers/opencode.js'
import { piReader } from './readers/pi.js'
import type { ContentReader, ReaderTurn } from './readers/types.js'
import { Synthesizer } from './synth.js'

/** Problem code for a session store that exists but cannot be parsed (R1.3). */
export const PROVIDER_PARSE_ERROR = 'PROVIDER_PARSE_ERROR'

/**
 * What loading one provider's session store can produce:
 *
 *   * a call array — the store is present and parsed;
 *   * `null`/`undefined` — the store is absent (R1.2); omit silently;
 *   * an `Error` — the loader declined to throw. An `ENOENT` carrier is
 *     still absence (R1.2); anything else is an unparseable store (R1.3).
 *
 * The loader may equivalently throw; a throw is classified exactly like a
 * returned error.
 */
export type ProviderLoad = {
  /** Calls parsed from one session file. */
  calls: ParsedProviderCall[]
  /** The same session file the registered reader must inspect. */
  filePath: string
}

export type ProviderLoaderResult = ParsedProviderCall[] | ProviderLoad | Error | null | undefined

/** Loads one provider's parsed calls, or reports why it could not. */
export type ProviderLoader = (provider: string) => ProviderLoaderResult

/** What ingesting a set of providers produced. */
export type ProviderIngestResult = {
  /** Synthesized records, provider by provider in input order. */
  records: CanonicalRecord[]
  /** One problem per unparseable store; absent providers appear nowhere. */
  problems: Problem[]
}

/** Readers registered for the transcript formats D5 and D6 have verified. */
export const PROVIDER_READERS: ReadonlyMap<string, ContentReader> = new Map([
  ['claude', claudeReader],
  ['claude-code', claudeReader],
  ['codex', codexReader],
  ['opencode', opencodeReader],
  ['kilo', kiloReader],
  ['kilo-code', kiloReader],
  ['copilot', copilotCliReader],
  ['pi', piReader],
])

function callsAndTurns(
  provider: string,
  load: ProviderLoad,
  reader: ContentReader | undefined,
): Promise<readonly [ParsedProviderCall[], ReaderTurn[] | undefined]> {
  // Copilot CLI records its ASAD context taxonomy in SQLite rather than in a
  // transcript. Its upstream parser supplies no transcript calls for that
  // source, so turn the reported rows into calls before normal synthesis.
  const calls = provider === 'copilot' && load.calls.length === 0
    ? loadCopilotCliCalls(load.filePath)
    : load.calls
  if (reader === undefined) return Promise.resolve([calls, undefined])
  return (async () => {
    const turns: ReaderTurn[] = []
    for await (const turn of reader.read(load.filePath)) turns.push(turn)
    return [calls, turns] as const
  })()
}

/**
 * Pair parser calls with turns from their shared session file. A reader turn
 * names the same session when available; an unnamed turn remains positionally
 * attributable to that file. Extra calls or turns are left unpaired rather
 * than borrowing content from an adjacent invocation.
 */
function matchingTurns(
  calls: readonly ParsedProviderCall[],
  turns: readonly ReaderTurn[],
): Array<ReaderTurn | undefined> {
  return calls.map((call, index) => {
    const turn = turns[index]
    return turn === undefined || turn.sessionId === undefined || turn.sessionId === call.sessionId
      ? turn
      : undefined
  })
}

/**
 * The fields this module reads off a loader error, structurally: `code` and
 * `path` are Node's fs error conventions, `file` is the convention loaders
 * follow when the failure has no fs error of its own (e.g. a SyntaxError
 * from `JSON.parse`). A parse problem must name the provider and the file,
 * so a loader whose parse can fail without any fs error attaches the file.
 */
type ErrorCarrier = { code?: unknown; file?: unknown; path?: unknown }

/** The file a failure is about, when anything attached one. */
function fileOf(error: Error): string | undefined {
  const carrier = error as ErrorCarrier
  if (typeof carrier.file === 'string' && carrier.file !== '') return carrier.file
  if (typeof carrier.path === 'string' && carrier.path !== '') return carrier.path
  return undefined
}

/**
 * Absence means the store is not there — a negative existence check (`ENOENT`)
 * — and nothing else. A store that exists but denies access, is a directory,
 * or fails to parse is a present store with a problem, not an absent one.
 */
function isAbsent(error: Error): boolean {
  return (error as ErrorCarrier).code === 'ENOENT'
}

/** The R1.3 problem: severity error, provider and file named. */
function parseProblem(provider: string, error: Error): Problem {
  const file = fileOf(error)
  return {
    severity: 'error',
    code: PROVIDER_PARSE_ERROR,
    message: `provider '${provider}': session store${file ? ` ${file}` : ''} could not be parsed: ${error.message}`,
    ...(file !== undefined ? { location: file } : {}),
  }
}

/**
 * Ingest every provider's session store through `loader`, classifying each
 * outcome per the Error Handling table: absent stores are omitted silently
 * (R1.2), unparseable stores record a {@link PROVIDER_PARSE_ERROR} problem
 * naming the provider and the file (R1.3), and in both failure shapes the
 * remaining providers are still ingested. Successful loads synthesize
 * through 9.1's {@link Synthesizer} — records land exactly as if that
 * synthesizer had been called directly, preserving input order.
 */
export async function ingestProviders(
  providers: readonly string[],
  loader: ProviderLoader,
): Promise<ProviderIngestResult> {
  const synthesizer = new Synthesizer()
  const records: CanonicalRecord[] = []
  const problems: Problem[] = []

  for (const provider of providers) {
    let loaded: ProviderLoaderResult
    try {
      loaded = loader(provider)
    } catch (err) {
      const error = err instanceof Error ? err : new Error(String(err))
      if (isAbsent(error)) continue // R1.2: absent, silently
      problems.push(parseProblem(provider, error)) // R1.3: record, continue
      continue
    }

    if (loaded === null || loaded === undefined) continue // R1.2: absent, silently

    if (loaded instanceof Error) {
      if (isAbsent(loaded)) continue // R1.2: absence reported as an error value
      problems.push(parseProblem(provider, loaded)) // R1.3: record, continue
      continue
    }

    if (Array.isArray(loaded)) {
      records.push(...synthesizer.synthesize(loaded))
      continue
    }

    const [calls, turns] = await callsAndTurns(provider, loaded, PROVIDER_READERS.get(provider))
    records.push(...synthesizer.synthesize(calls, turns === undefined ? undefined : matchingTurns(calls, turns)))
  }

  return { records, problems }
}
