// Local-provider refresh orchestration for KyberDash.
//
// The upstream parser owns discovery and parsing of provider dot-folders. This
// module deliberately owns only the composition: discover every installed
// provider, synthesize its parsed calls through the existing file ingest seam,
// validate and persist the resulting canonical records, then rebuild the
// derived dashboard tables. Keeping that boundary here avoids making either
// the parser or the canonical store know about the other's runtime lifecycle.

import type { Provider, SessionSource, ParsedProviderCall } from '../synth/provider.js'
import { recordValidationProblems } from '../canon/adapters/quarantine.js'
import { buildHarnessRollup } from '../canon/harnesses.js'
import { buildSessions, type BuildSessionsReport } from '../canon/sessions.js'
import type { CanonStore, SpanProblem } from '../canon/store.js'
import { isFileSource } from '../canon/measurability.js'
import { deduplicate } from '../synth/dedup.js'
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
  const otlpRecords = store.listAll().filter((record) => !isFileSource(record.source))
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

    for (const source of providerSources) {
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
      store.upsertMany(deduplicate(valid, otlpRecords, store))
    }
  }

  const sessions = await buildSessions(store)
  const rollups = buildHarnessRollup(store)

  return {
    providers: selected.length,
    sources,
    synthesized,
    accepted,
    problems,
    sessions,
    rollups: rollups.length,
  }
}
