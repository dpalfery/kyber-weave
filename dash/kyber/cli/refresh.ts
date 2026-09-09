// Local-provider refresh orchestration for KyberDash.
//
// The upstream parser owns discovery and parsing of provider dot-folders. This
// module deliberately owns only the composition: discover every installed
// provider, synthesize its parsed calls through the existing file ingest seam,
// validate and persist the resulting canonical records, then rebuild the
// derived dashboard tables. Keeping that boundary here avoids making either
// the parser or the canonical store know about the other's runtime lifecycle.

import { readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'

import type { CanonicalRecord } from '../canon/types.js'
import type { Provider, SessionSource, ParsedProviderCall } from '../synth/provider.js'
import { recordValidationProblems } from '../canon/adapters/quarantine.js'
import { buildSessions, type BuildSessionsReport } from '../canon/sessions.js'
import type { CanonStore, SpanProblem } from '../canon/store.js'
import { deduplicate, deduplicationKeyFor } from '../synth/dedup.js'
import { ingestProviders, type ProviderIngestResult } from '../synth/provider.js'

/** Provider-local discovery or parser failures are surfaced under this code. */
export const PROVIDER_REFRESH_ERROR = 'PROVIDER_REFRESH_ERROR'

/** Observable outcome of one local-provider refresh command. */
export type LocalProviderRefreshReport = {
  providers: number
  sources: number
  synthesized: number
  accepted: number
  problems: number
  sessions: BuildSessionsReport
  rollups: number
  findings: number
}

export type LocalProviderRefreshOptions = {
  /** Limit a diagnostic refresh to exact native provider identities. */
  providers?: readonly string[]
}

export type RefreshDependencies = {
  getAllProviders: () => Promise<Provider[]>
}

const productionDependencies: RefreshDependencies = {
  getAllProviders: async () => (await import('../synth/provider.js')).getAllProviders(),
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error))
}

function withSourcePath(error: unknown, source: SessionSource): Error {
  const failure = asError(error) as Error & { file?: string }
  failure.file ??= source.path
  return failure
}

function providerProblem(provider: string, error: unknown, location?: string): SpanProblem {
  const failure = asError(error)
  return {
    // Problems are auditable without pretending that a failed source is a
    // model span. The deterministic key makes repeated refreshes recognizable.
    spanId: `provider:${provider}:${location ?? 'discovery'}`,
    severity: 'error',
    code: PROVIDER_REFRESH_ERROR,
    message: `provider '${provider}' refresh failed${location ? ` for ${location}` : ''}: ${failure.message}`,
    ...(location === undefined ? {} : { location }),
  }
}

/** Transcript extensions the registered content readers can parse. */
const TRANSCRIPT_EXTENSIONS = ['.jsonl', '.json']

/**
 * Expand a discovered source that is a directory into the transcript files
 * inside it.
 *
 * Claude Code's provider discovers `~/.claude/projects/<slug>` directories,
 * because the vendored aggregation walks them itself. Ingest here reads one
 * source as one transcript, so a directory reached the reader as a file path
 * and every Claude source failed with EISDIR — the whole harness ingested as
 * zero records. Providers that already discover files are returned untouched.
 */
function expandSourceFiles(source: SessionSource): SessionSource[] {
  let isDirectory: boolean
  try {
    isDirectory = statSync(source.path).isDirectory()
  } catch {
    // An unreadable path stays as-is so the ingest problem names the source
    // the provider actually reported rather than silently dropping it.
    return [source]
  }
  if (!isDirectory) return [source]

  let entries: string[]
  try {
    entries = readdirSync(source.path)
  } catch {
    return [source]
  }

  const files = entries
    .filter((entry) => TRANSCRIPT_EXTENSIONS.some((ext) => entry.endsWith(ext)))
    .sort()
    .map((entry) => ({ ...source, path: join(source.path, entry) }))

  // A directory with no transcripts is absence, not a source to fail on.
  return files
}

/** The OTLP records sharing a session key with anything this source parsed. */
function otlpRecordsFor(
  fileRecords: readonly CanonicalRecord[],
  otlpSpansByKey: ReadonlyMap<string, readonly string[]>,
  store: CanonStore,
): CanonicalRecord[] {
  const spanIds: string[] = []
  const seen = new Set<string>()
  for (const record of fileRecords) {
    const key = deduplicationKeyFor(record)
    if (key === null || seen.has(key)) continue
    seen.add(key)
    const group = otlpSpansByKey.get(key)
    if (group !== undefined) spanIds.push(...group)
  }
  return store.recordsBySpanIds(spanIds)
}

async function parseSource(
  provider: Provider,
  source: SessionSource,
  seenKeys: Set<string>,
): Promise<ParsedProviderCall[]> {
  const parser = provider.createSessionParser(source, seenKeys)
  const calls: ParsedProviderCall[] = []
  for await (const call of parser.parse()) calls.push(call)
  return calls
}

async function ingestSource(
  provider: Provider,
  source: SessionSource,
  seenKeys: Set<string>,
): Promise<ProviderIngestResult> {
  let parsed: ParsedProviderCall[] | Error
  try {
    parsed = await parseSource(provider, source, seenKeys)
  } catch (error) {
    parsed = withSourcePath(error, source)
  }

  // `ingestProviders` is intentionally the one synthesis seam. Calling it per
  // source keeps its existing R1.3 provider-problem behavior while preserving
  // the upstream parser's source-specific reader path.
  return ingestProviders([provider.name], () =>
    parsed instanceof Error
      ? parsed
      : { calls: parsed, filePath: source.path },
  )
}

/**
 * Refresh the local canonical corpus from every installed provider's native
 * session stores. A native Antigravity provider reaches this path under its
 * own `antigravity` identity; this code never infers an Antigravity identity
 * from Gemini or generic Google telemetry.
 */
export async function refreshLocalProviders(
  store: CanonStore,
  dependencies: RefreshDependencies = productionDependencies,
  options: LocalProviderRefreshOptions = {},
): Promise<LocalProviderRefreshReport> {
  const discoveredProviders = await dependencies.getAllProviders()
  const selected = options.providers === undefined
    ? discoveredProviders
    : discoveredProviders.filter((provider) => options.providers!.includes(provider.name))
  const seenKeys = new Set<string>()
  // The OTLP side, indexed by session key once for the whole refresh.
  //
  // This loop used to hand every OTLP record to `deduplicate` for every source.
  // `deduplicate` returns the complete merged corpus — including the OTLP rows
  // no file record touched — so each source re-upserted the entire OTLP side:
  // measured at 23,695 rows re-written to ingest one 28-record transcript, each
  // write recompressing its payload. Across ~2,200 sources that is ~52 million
  // upserts to store ~62,000 records. Handing over only the rows that share a
  // session key with the source at hand keeps the collapse identical (an OTLP
  // row under no shared key would have passed through untouched anyway) while
  // making the write volume proportional to what was actually parsed.
  // Only span ids are indexed, not the records: a record's key lives in its
  // compressed `raw`, so every OTLP row must be decoded to read one — but
  // holding them all afterwards cost ~3.3 GB resident. Streaming lets each row
  // go as soon as its key is read; the few that a source actually matches are
  // re-read by span id.
  const otlpSpansByKey = new Map<string, string[]>()
  for (const record of store.streamOtlpSourced()) {
    const key = deduplicationKeyFor(record)
    if (key === null) continue
    const group = otlpSpansByKey.get(key)
    if (group === undefined) otlpSpansByKey.set(key, [record.spanId])
    else group.push(record.spanId)
  }
  let sources = 0
  let synthesized = 0
  let accepted = 0
  let problems = 0

  for (const provider of selected) {
    let providerSources: SessionSource[]
    try {
      providerSources = await provider.discoverSessions()
    } catch (error) {
      store.recordProblem(providerProblem(provider.name, error))
      problems += 1
      continue
    }

    for (const discovered of providerSources.flatMap(expandSourceFiles)) {
      const source = discovered
      sources += 1
      const result = await ingestSource(provider, source, seenKeys)
      synthesized += result.records.length
      for (const problem of result.problems) {
        store.recordProblem({
          ...problem,
          spanId: `provider:${provider.name}:${problem.location ?? source.path}`,
        })
      }
      problems += result.problems.length

      // Persist one source at a time. A real dot-folder corpus can be many
      // gigabytes, so accumulating every parsed call until the end would make
      // a refresh fail exactly on the machines it is meant to diagnose.
      const valid = recordValidationProblems(result.records, store)
      accepted += valid.length
      store.upsertMany(deduplicate(valid, otlpRecordsFor(valid, otlpSpansByKey, store), store))
    }
  }

  // `buildSessions` rebuilds every derived table over the records it just
  // accepted — sessions, runs, executions, harness rollups and findings.
  const sessions = await buildSessions(store)

  return {
    providers: selected.length,
    sources,
    synthesized,
    accepted,
    problems,
    sessions,
    rollups: sessions.rollups,
    findings: sessions.findings,
  }
}
