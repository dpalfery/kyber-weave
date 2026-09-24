import { randomUUID } from 'node:crypto'
import { mkdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

import { recordValidationProblems } from '../canon/adapters/quarantine.js'
// The shared projection entry point, not a private buildSessions call: static
// refresh and the live OTLP collector must run the ONE full projection over
// the canonical store (plan T20 → T21), or the two ingress paths drift.
import { projectCanonicalStore } from '../canon/projection.js'
import { purgeExpiredContent } from '../canon/retention.js'
import type { CoverageInterval, RecordProvenance, SourceCheckpoint } from '../canon/source-state.js'
import { checkpointIsReusable, uncoveredIntervals } from '../canon/source-state.js'
import type { RefreshTrigger } from '../canon/refresh-run.js'
import type { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { deduplicate, deduplicationKeyFor } from '../synth/dedup.js'
import {
  ingestProviders as productionIngestProviders,
  PROVIDER_PARSE_ERROR,
  type Provider,
} from '../synth/provider.js'
import { provenanceFor } from '../synth/synth.js'

import { auditProviderRegistry, HARNESS_DESCRIPTORS } from './registry.js'
import type { HarnessJobRow, HarnessJobStatus, RefreshReport } from './report.js'
import { defaultJobConcurrency, runJobsSettled } from './scheduler.js'
import {
  iterateNativeUnits as productionIterateNativeUnits,
  utcHistoryWindow,
  type NativeUnit,
  type SourceReaderDependencies,
} from './source-reader.js'
import type { HarnessSourceDescriptor } from './types.js'
import { createCanonicalWriter, type SourceUnitCommit } from './writer.js'

export type { RefreshReport, HarnessJobRow, HarnessJobStatus } from './report.js'

export const DEFAULT_HISTORY_WEEKS = 2
export const DEFAULT_WRITER_CAPACITY = 8
export const HARNESS_REFRESH_ERROR = 'HARNESS_REFRESH_ERROR'

export type RefreshDependencies = {
  getAllProviders: () => Promise<Provider[]>
  descriptors?: readonly HarnessSourceDescriptor[]
  jobConcurrency?: number
  writerCapacity?: number
  iterateNativeUnits?: typeof productionIterateNativeUnits
  ingestProviders?: typeof productionIngestProviders
  parseAllSessions?: SourceReaderDependencies['parseAllSessions']
  fingerprintFile?: SourceReaderDependencies['fingerprintFile']
  peekEvidence?: SourceReaderDependencies['peekEvidence']
  parseCalls?: SourceReaderDependencies['parseCalls']
  commandStartedAt?: Date
  claudeCacheDir?: string
  signal?: AbortSignal
}

const productionDependencies: RefreshDependencies = {
  getAllProviders: async () => (await import('../synth/provider.js')).getAllProviders(),
}

export async function refreshHarnessSources(
  store: CanonStore,
  dependencies: RefreshDependencies = productionDependencies,
  options: { historyWeeks?: number; trigger?: RefreshTrigger } = {},
): Promise<RefreshReport> {
  const historyWeeks = options.historyWeeks ?? DEFAULT_HISTORY_WEEKS
  const commandStartedAt = dependencies.commandStartedAt ?? new Date()
  const dateRange = utcHistoryWindow(commandStartedAt, historyWeeks)
  const coveredFromUtc = dateRange.start.toISOString()
  const coveredThroughUtc = dateRange.end.toISOString()
  const importedAtUtc = commandStartedAt.toISOString()
  store.reconcileDeadRefreshRuns(importedAtUtc)
  const runId = store.startRefreshRun({
    // Random, not derived: two runs can legitimately start in the same millisecond from
    // the same process (a test harness does exactly that), and a derived id would collide.
    id: randomUUID(),
    startedAt: importedAtUtc,
    pid: process.pid,
    trigger: options.trigger ?? 'cli',
  })
  let runClosed = false
  try {
    const descriptors = [...(dependencies.descriptors ?? HARNESS_DESCRIPTORS)]
    const iterate = dependencies.iterateNativeUnits ?? productionIterateNativeUnits
    const ingest = dependencies.ingestProviders ?? productionIngestProviders
    const concurrency = dependencies.jobConcurrency ?? defaultJobConcurrency()
    const writer = createCanonicalWriter({
      capacity: dependencies.writerCapacity ?? DEFAULT_WRITER_CAPACITY,
      commit: (item) => store.commitSourceUnit(item),
    })

    const providers = await dependencies.getAllProviders()
    auditProviderRegistry(providers)

    const otlpSpansByKey = indexOtlpSpans(store)
    const parseAllSessions = dependencies.parseAllSessions
      ?? ((range, filter) => warmClaudeSpecialPath(range, filter, dependencies.claudeCacheDir))

    const settled = await runJobsSettled(descriptors, concurrency, async (descriptor) =>
      runHarnessJob({
        descriptor,
        store,
        providers,
        dateRange,
        coveredFromUtc,
        coveredThroughUtc,
        importedAtUtc,
        iterate,
        ingest,
        writer,
        otlpSpansByKey,
        parseAllSessions,
        fingerprintFile: dependencies.fingerprintFile,
        peekEvidence: dependencies.peekEvidence,
        parseCalls: dependencies.parseCalls,
        signal: dependencies.signal,
      }),
    )

    const rows = settled.map((result, index) => {
      if (result.status === 'fulfilled') return result.value
      return failedRow(descriptors[index]!.harnessId, asError(result.reason))
    })

    try {
      await writer.drain()
    } catch {
      // Per-item commit failures already landed on the responsible job row.
    }
    purgeExpiredContent(store, commandStartedAt)

    // Exactly one projection per refresh, and only after the writer drained —
    // projecting earlier would cache a store the writer was still committing to.
    let derivationFailed = false
    try {
      await projectCanonicalStore(store)
    } catch {
      derivationFailed = true
    }

    const failedJobs = rows.filter((row) => row.status === 'failed').length
    const exitCode: 0 | 1 = derivationFailed || failedJobs > 0 ? 1 : 0

    const report: RefreshReport = {
      historyWeeks,
      commandStartedAt: importedAtUtc,
      rows,
      derived: {
        sessions: store.sessionCount(),
        runs: store.runCount(),
        executions: store.executionCount(),
        rollups: store.harnessRollupCount(),
      },
      failedJobs,
      derivationFailed,
      exitCode,
    }

    // Close the run the same way whether it worked or not: the footer needs the failure as
    // much as the success, and a run row left open would later read as one still going.
    const summary =
      exitCode === 0
        ? `${rows.length} source job(s), ${report.derived.sessions} session(s)`
        : `${failedJobs} of ${rows.length} source job(s) failed${derivationFailed ? '; derivation failed' : ''}`
    store.completeRefreshRun(
      runId,
      exitCode === 0 ? 'success' : 'failure',
      new Date().toISOString(),
      summary,
    )
    runClosed = true
    return report
  } catch (error) {
    if (!runClosed) {
      const failure = asError(error)
      try {
        store.completeRefreshRun(
          runId,
          'failure',
          new Date().toISOString(),
          safeDiagnostic(failure, 'refresh failed'),
        )
      } catch {
        // Preserve the original refresh error if the store also cannot close the row.
      }
    }
    throw error
  }
}

type JobContext = {
  descriptor: HarnessSourceDescriptor
  store: CanonStore
  providers: readonly Provider[]
  dateRange: SourceReaderDependencies['dateRange']
  coveredFromUtc: string
  coveredThroughUtc: string
  importedAtUtc: string
  iterate: typeof productionIterateNativeUnits
  ingest: typeof productionIngestProviders
  writer: { enqueue: (item: SourceUnitCommit) => Promise<void> }
  otlpSpansByKey: ReadonlyMap<string, readonly string[]>
  parseAllSessions: NonNullable<SourceReaderDependencies['parseAllSessions']>
  fingerprintFile?: SourceReaderDependencies['fingerprintFile']
  peekEvidence?: SourceReaderDependencies['peekEvidence']
  parseCalls?: SourceReaderDependencies['parseCalls']
  signal?: AbortSignal
}

async function runHarnessJob(context: JobContext): Promise<HarnessJobRow> {
  const { descriptor, store } = context
  const row: HarnessJobRow = {
    harnessId: descriptor.harnessId,
    units: 0,
    changed: 0,
    skipped: 0,
    created: 0,
    updated: 0,
    problems: 0,
    status: 'unavailable',
  }

  let units: NativeUnit[]
  try {
    units = await context.iterate(descriptor.harnessId, {
      providers: context.providers,
      dateRange: context.dateRange,
      previousFingerprints: previousFingerprintsFor(
        store,
        descriptor,
        context.coveredFromUtc,
        context.coveredThroughUtc,
      ),
      parseAllSessions: context.parseAllSessions,
      ...(context.fingerprintFile ? { fingerprintFile: context.fingerprintFile } : {}),
      ...(context.peekEvidence ? { peekEvidence: context.peekEvidence } : {}),
      ...(context.parseCalls ? { parseCalls: context.parseCalls } : {}),
    })
  } catch (error) {
    const failure = asError(error)
    store.recordProblem({
      spanId: `harness:${descriptor.harnessId}:job`,
      severity: 'error',
      code: PROVIDER_PARSE_ERROR,
      message: `harness '${descriptor.harnessId}' could not be parsed: ${failure.message}`,
    })
    return failedRow(descriptor.harnessId, failure, 'source unreadable')
  }

  if (units.length === 0) {
    return row
  }

  let writerFailed = false
  for (const unit of units) {
    if (context.signal?.aborted) {
      return { ...row, status: 'failed', diagnostic: 'refresh interrupted' }
    }
    row.units += 1
    if (unit.status === 'unchanged') {
      row.skipped += 1
      continue
    }
    row.changed += 1
    row.problems += unit.problems.length
    for (const problem of unit.problems) {
      store.recordProblem({
        spanId: `harness:${descriptor.harnessId}:${unit.sourceKey}:${problem.code}`,
        severity: 'error',
        code: problem.code,
        message: `harness '${descriptor.harnessId}' ${problem.message}`,
      })
    }

    const ingestResult = await ingestUnit(context.ingest, descriptor, unit)
    for (const problem of ingestResult.problems) {
      store.recordProblem({
        ...problem,
        spanId: `harness:${descriptor.harnessId}:${unit.sourceKey}`,
      })
    }
    row.problems += ingestResult.problems.length

    const valid = recordValidationProblems(ingestResult.records, store)
    row.problems += ingestResult.records.length - valid.length
    const merged = deduplicate(
      valid,
      otlpRecordsFor(valid, context.otlpSpansByKey, store),
      store,
    )
    const previous = store.getSourceCheckpoint(descriptor.harnessId, unit.sourceKey)
    const requested: CoverageInterval = {
      fromUtc: context.coveredFromUtc,
      throughUtc: context.coveredThroughUtc,
    }
    const coverageRequest = {
      revisionToken: unit.revision?.token ?? previous?.revisionToken ?? 'unknown',
      parserContractVersion: descriptor.parserContractVersion,
    }
    const outgoing = recordsForUncoveredCommit(merged, store, previous, requested, coverageRequest)
    const created = outgoing.filter((record) => store.get(record.spanId) === undefined).length
    const updated = outgoing.length - created
    const checkpoint = checkpointFor(descriptor, unit, {
      coveredFromUtc: context.coveredFromUtc,
      coveredThroughUtc: context.coveredThroughUtc,
      importedAtUtc: context.importedAtUtc,
      recordCount: (previous?.recordCount ?? 0) + created,
      status: ingestResult.problems.length > 0 || unit.problems.length > 0 ? 'partial' : 'ok',
    })
    const provenance = provenanceRows(outgoing, unit, descriptor, context.importedAtUtc)
    try {
      await context.writer.enqueue({ records: outgoing, provenance, checkpoint })
      row.created += created
      row.updated += updated
    } catch (error) {
      writerFailed = true
      row.diagnostic = safeDiagnostic(asError(error), 'writer failed')
    }
  }

  row.status = jobStatus(row, writerFailed)
  if (row.status === 'failed' && row.diagnostic === undefined) {
    row.diagnostic = 'harness job failed'
  }
  if (row.status === 'partial' && row.diagnostic === undefined) {
    row.diagnostic = `${row.problems} unit recorded validation problems`
  }
  return row
}

function jobStatus(row: HarnessJobRow, writerFailed: boolean): HarnessJobStatus {
  if (writerFailed) return 'failed'
  if (row.units === 0) return 'unavailable'
  if (row.changed === 0 && row.created === 0 && row.updated === 0) return 'unchanged'
  if (row.problems > 0) return 'partial'
  return 'ok'
}

async function ingestUnit(
  ingest: typeof productionIngestProviders,
  descriptor: HarnessSourceDescriptor,
  unit: NativeUnit,
) {
  const loaded = await ingest([descriptor.harnessId], () => ({
    calls: unit.envelopes.map((envelope) => envelope.call),
    filePath: unit.source.path,
    harnessId: descriptor.harnessId,
    sourceKey: unit.sourceKey,
  }))
  if (loaded.records.length > 0 || unit.envelopes.length === 0) return loaded
  const fallback = await ingest([descriptor.harnessId], () =>
    unit.envelopes.map((envelope) => ({
      ...envelope.call,
      provider: descriptor.harnessId,
    })),
  )
  return fallback
}

function failedRow(harnessId: string, error: Error, fallback = 'harness job failed'): HarnessJobRow {
  return {
    harnessId,
    units: 0,
    changed: 0,
    skipped: 0,
    created: 0,
    updated: 0,
    problems: 1,
    status: 'failed',
    diagnostic: safeDiagnostic(error, fallback),
  }
}

function safeDiagnostic(error: Error, fallback: string): string {
  const message = error.message.trim()
  if (message === '') return fallback
  return message.replace(/\/(?:Users|home)\/[^\s:]+/g, '').replace(/\/native\/[^\s]+/g, 'source unit').trim() || fallback
}

function previousFingerprintsFor(
  store: CanonStore,
  descriptor: HarnessSourceDescriptor,
  coveredFromUtc: string,
  coveredThroughUtc: string,
): Map<string, { dev: number; ino: number; mtimeMs: number; sizeBytes: number }> {
  const fingerprints = new Map<string, { dev: number; ino: number; mtimeMs: number; sizeBytes: number }>()
  const requested = { fromUtc: coveredFromUtc, throughUtc: coveredThroughUtc }
  for (const checkpoint of store.listSourceCheckpoints(descriptor.harnessId)) {
    const request = {
      revisionToken: checkpoint.revisionToken,
      parserContractVersion: descriptor.parserContractVersion,
    }
    if (!checkpointIsReusable(checkpoint, request)) continue
    if (uncoveredIntervals(checkpoint, requested, request).length > 0) continue
    const fingerprint = fingerprintFromToken(checkpoint.revisionToken)
    if (fingerprint) fingerprints.set(checkpoint.sourceKey, fingerprint)
  }
  return fingerprints
}

function fingerprintFromToken(token: string): {
  dev: number
  ino: number
  mtimeMs: number
  sizeBytes: number
} | undefined {
  const parts = token.split(':')
  if (parts.length !== 4) return undefined
  const [dev, ino, mtimeMs, sizeBytes] = parts.map(Number)
  if (![dev, ino, mtimeMs, sizeBytes].every((value) => Number.isFinite(value))) return undefined
  return { dev, ino, mtimeMs, sizeBytes }
}

function checkpointFor(
  descriptor: HarnessSourceDescriptor,
  unit: NativeUnit,
  extras: {
    coveredFromUtc: string
    coveredThroughUtc: string
    importedAtUtc: string
    recordCount: number
    status: SourceCheckpoint['lastStatus']
  },
): SourceCheckpoint {
  return {
    harnessId: descriptor.harnessId,
    sourceKey: unit.sourceKey,
    providerId: descriptor.providerName,
    parserId: descriptor.providerName,
    parserContractVersion: descriptor.parserContractVersion,
    format: descriptor.nativeFormat,
    sourceRootLabel: descriptor.sourceRootLabel,
    revisionToken: unit.revision?.token ?? 'unknown',
    coveredFromUtc: extras.coveredFromUtc,
    coveredThroughUtc: extras.coveredThroughUtc,
    lastAttemptUtc: extras.importedAtUtc,
    lastSuccessUtc: extras.importedAtUtc,
    lastStatus: extras.status,
    lastErrorCode: extras.status === 'ok' || extras.status === 'unchanged' ? null : extras.status,
    unitCount: 1,
    recordCount: extras.recordCount,
  }
}

function recordTimestampUtc(record: CanonicalRecord): string {
  return record.timestamp instanceof Date ? record.timestamp.toISOString() : record.timestamp
}

function timestampOutsideCovered(timestampUtc: string, covered: CoverageInterval): boolean {
  return timestampUtc < covered.fromUtc || timestampUtc > covered.throughUtc
}

/**
 * Keep already-covered unchanged rows out of the commit. A widened history
 * window only writes the newly uncovered prefix/suffix; a revision change
 * writes spans that are not already in the store.
 */
function recordsForUncoveredCommit(
  merged: readonly CanonicalRecord[],
  store: CanonStore,
  previous: SourceCheckpoint | undefined,
  requested: CoverageInterval,
  request: { revisionToken: string; parserContractVersion: string },
): CanonicalRecord[] {
  const reusable = checkpointIsReusable(previous, request)
  const gaps = uncoveredIntervals(previous, requested, request)
  return merged.filter((record) => {
    if (store.get(record.spanId) !== undefined) return false
    if (!reusable || previous === undefined) return true
    if (gaps.length === 0) return false
    return timestampOutsideCovered(recordTimestampUtc(record), {
      fromUtc: previous.coveredFromUtc,
      throughUtc: previous.coveredThroughUtc,
    })
  })
}

function provenanceRows(
  records: readonly CanonicalRecord[],
  unit: NativeUnit,
  descriptor: HarnessSourceDescriptor,
  importedAtUtc: string,
): RecordProvenance[] {
  return records.map((record, index) => {
    const timestampUtc = recordTimestampUtc(record)
    const envelope = unit.envelopes.find((candidate) => candidate.timestamp === timestampUtc)
      ?? unit.envelopes[index]
    const fromSynth = envelope
      ? provenanceFor(envelope.call, {
          ...envelope,
          sourceKey: unit.sourceKey,
          harnessId: descriptor.harnessId,
          sourceRevision: unit.revision?.token,
          parserContractVersion: descriptor.parserContractVersion,
          locationToken: descriptor.sourceRootLabel,
        }, index)
      : undefined
    return {
      spanId: record.spanId,
      harnessId: descriptor.harnessId,
      sourceKey: unit.sourceKey,
      nativeSessionId: fromSynth?.nativeSessionId ?? record.sessionId ?? null,
      nativeRecordId: fromSynth?.nativeRecordId ?? null,
      sourceRevision: unit.revision?.token ?? 'unknown',
      parserVersion: descriptor.parserContractVersion,
      importedAtUtc,
      locationToken: descriptor.sourceRootLabel,
    }
  })
}

function indexOtlpSpans(store: CanonStore): Map<string, string[]> {
  const otlpSpansByKey = new Map<string, string[]>()
  for (const record of store.streamOtlpSourced()) {
    const key = deduplicationKeyFor(record)
    if (key === null) continue
    const group = otlpSpansByKey.get(key)
    if (group === undefined) otlpSpansByKey.set(key, [record.spanId])
    else group.push(record.spanId)
  }
  return otlpSpansByKey
}

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

async function warmClaudeSpecialPath(
  dateRange: SourceReaderDependencies['dateRange'] | undefined,
  providerFilter: string | undefined,
  cacheDir = join(homedir(), '.kyberdash', 'parser-cache'),
): Promise<void> {
  mkdirSync(cacheDir, { recursive: true })
  const previous = process.env['KYBERDASH_CACHE_DIR']
  process.env['KYBERDASH_CACHE_DIR'] = cacheDir
  const { acquireCacheRefreshLock } = await import('./lock.js')
  const lock = await acquireCacheRefreshLock({ directory: cacheDir })
  try {
    const { parseAllSessions } = await import('../ingest/parser.js')
    await parseAllSessions(dateRange, providerFilter)
  } finally {
    if (lock.outcome === 'acquired') await lock.handle.release()
    if (previous === undefined) delete process.env['KYBERDASH_CACHE_DIR']
    else process.env['KYBERDASH_CACHE_DIR'] = previous
  }
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error))
}
