// Kyber-owned native-unit adapter around exported provider, parser, and cache seams.
//
// Refresh jobs walk one harness surface at a time. This module discovers native
// units, classifies them with the T1 registry, reuses DateRange / SessionCache /
// fingerprint reconciliation, and slices records to the UTC window. Claude's
// empty createSessionParser is recovered through directory expansion plus the
// existing Kyber transcript parser, after warming the upstream special parse
// path (`parseAllSessions(..., 'claude')`).

import { createReadStream, readdirSync, statSync } from 'node:fs'
import { availableParallelism } from 'node:os'
import { join } from 'node:path'
import { createInterface } from 'node:readline'

import { mapWithConcurrency } from '../../src/fs-utils.js'
import type { FileFingerprint } from '../../src/session-cache.js'
import { fingerprintFile as upstreamFingerprintFile, reconcileFile } from '../../src/session-cache.js'
import type { Provider, SessionSource, ParsedProviderCall } from '../../src/providers/types.js'
import type { DateRange } from '../../src/types.js'
import { loadClaudeCalls } from '../synth/readers/claude.js'

import { classifySessionSource, descriptorFor, sourceKeyFor } from './registry.js'
import type { HarnessId } from './types.js'

export type { DateRange }

const WEEK_MS = 7 * 24 * 60 * 60 * 1000
const TRANSCRIPT_EXTENSIONS = ['.jsonl', '.json']
const EVIDENCE_LINE_LIMIT = 80

export type NativeUnitStatus = 'new' | 'changed' | 'unchanged' | 'appended'

export type SourceProblemCode =
  | 'FUTURE_DATED'
  | 'MISSING_TIMESTAMP'
  | 'MALFORMED_TIMESTAMP'

export type SourceProblem = {
  code: SourceProblemCode
  message: string
}

export type NativeRevision = {
  fingerprint: FileFingerprint
  token: string
}

export type SourceRecordEnvelope = {
  harnessId: HarnessId
  sourceKey: string
  source: SessionSource
  nativeSessionId: string
  nativeRecordId?: string
  timestamp: string
  call: ParsedProviderCall
  revisionToken: string
}

export type NativeUnit = {
  harnessId: HarnessId
  sourceKey: string
  source: SessionSource
  status: NativeUnitStatus
  revision: NativeRevision | null
  envelopes: SourceRecordEnvelope[]
  problems: SourceProblem[]
}

export type NativeClassifierEvidence = {
  originator?: string | null
  entrypoint?: string | null
}

export type SourceReaderDependencies = {
  providers: readonly Provider[]
  dateRange: DateRange
  concurrency?: number
  previousFingerprints?: ReadonlyMap<string, FileFingerprint>
  fingerprintFile?: (path: string) => Promise<FileFingerprint | null>
  parseCalls?: (
    provider: Provider,
    source: SessionSource,
    dateRange: DateRange,
  ) => Promise<readonly ParsedProviderCall[]>
  parseAllSessions?: (dateRange?: DateRange, providerFilter?: string) => Promise<unknown>
  peekEvidence?: (source: SessionSource) => Promise<NativeClassifierEvidence>
}

export function utcHistoryWindow(commandStartedAt: Date, historyWeeks: number): DateRange {
  return {
    start: new Date(commandStartedAt.getTime() - historyWeeks * WEEK_MS),
    end: commandStartedAt,
  }
}

export function defaultReaderConcurrency(): number {
  const available = availableParallelism()
  return Math.max(1, Math.min(4, available - 1))
}

export function revisionTokenFor(fingerprint: FileFingerprint): string {
  return `${fingerprint.dev}:${fingerprint.ino}:${fingerprint.mtimeMs}:${fingerprint.sizeBytes}`
}

export async function iterateNativeUnits(
  harnessId: HarnessId,
  dependencies: SourceReaderDependencies,
): Promise<NativeUnit[]> {
  const descriptor = descriptorFor(harnessId)
  if (!descriptor) return []

  const provider = dependencies.providers.find(entry => entry.name === descriptor.providerName)
  if (!provider) return []

  if (descriptor.providerName === 'claude') {
    await warmClaudeSpecialPath(dependencies)
  }

  const discovered = await provider.discoverSessions()
  const nativeSources = discovered.flatMap(expandNativeDirectorySource)
  const classified: SessionSource[] = []
  for (const source of nativeSources) {
    const evidence = dependencies.peekEvidence
      ? await dependencies.peekEvidence(source)
      : await peekNativeEvidence(source)
    const classification = classifySessionSource({
      source,
      originator: evidence.originator,
      entrypoint: evidence.entrypoint,
    })
    if (classification.outcome === 'harness' && classification.harnessId === harnessId) {
      classified.push(source)
    }
  }

  classified.sort((left, right) => sourceKeyFor(harnessId, left).localeCompare(sourceKeyFor(harnessId, right)))

  const concurrency = Math.max(1, dependencies.concurrency ?? defaultReaderConcurrency())
  return mapWithConcurrency(classified, concurrency, source =>
    readNativeUnit(harnessId, provider, source, dependencies),
  )
}

export function expandNativeDirectorySource(source: SessionSource): SessionSource[] {
  let isDirectory: boolean
  try {
    isDirectory = statSync(source.path).isDirectory()
  } catch {
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
    .filter(entry => TRANSCRIPT_EXTENSIONS.some(extension => entry.endsWith(extension)))
    .sort()
    .map(entry => ({ ...source, path: join(source.path, entry) }))
  return files
}

export function sliceCallsToWindow(
  calls: readonly ParsedProviderCall[],
  dateRange: DateRange,
): { inWindow: ParsedProviderCall[]; problems: SourceProblem[] } {
  const inWindow: ParsedProviderCall[] = []
  const problems: SourceProblem[] = []
  for (const parsed of calls) {
    const raw = parsed.timestamp?.trim() ?? ''
    if (raw === '') {
      problems.push({
        code: 'MISSING_TIMESTAMP',
        message: 'Native record has no timestamp; file mtime is not a semantic substitute.',
      })
      continue
    }
    const timestamp = new Date(raw)
    if (Number.isNaN(timestamp.getTime())) {
      problems.push({
        code: 'MALFORMED_TIMESTAMP',
        message: `Native record timestamp '${raw}' is not a usable instant.`,
      })
      continue
    }
    if (timestamp > dateRange.end) {
      problems.push({
        code: 'FUTURE_DATED',
        message: 'Native record timestamp is after commandStartedAt; quarantined rather than imported.',
      })
      continue
    }
    if (timestamp < dateRange.start) continue
    inWindow.push(parsed)
  }
  return { inWindow, problems }
}

async function readNativeUnit(
  harnessId: HarnessId,
  provider: Provider,
  source: SessionSource,
  dependencies: SourceReaderDependencies,
): Promise<NativeUnit> {
  const sourceKey = sourceKeyFor(harnessId, source)
  const fingerprintFn = dependencies.fingerprintFile ?? upstreamFingerprintFile
  const fingerprint = await fingerprintFn(source.path)
  const revision = fingerprint ? { fingerprint, token: revisionTokenFor(fingerprint) } : null
  const previous = dependencies.previousFingerprints?.get(sourceKey)
  const status = changeStatus(fingerprint, previous)

  if (status === 'unchanged') {
    return {
      harnessId,
      sourceKey,
      source,
      status,
      revision,
      envelopes: [],
      problems: [],
    }
  }

  const calls = dependencies.parseCalls
    ? await dependencies.parseCalls(provider, source, dependencies.dateRange)
    : await parseSourceCalls(provider, source, dependencies.dateRange)
  const { inWindow, problems } = sliceCallsToWindow(calls, dependencies.dateRange)
  const revisionToken = revision?.token ?? 'unknown'
  const envelopes = inWindow.map(parsed => toEnvelope(harnessId, sourceKey, source, parsed, revisionToken))
  return { harnessId, sourceKey, source, status, revision, envelopes, problems }
}

function changeStatus(
  current: FileFingerprint | null,
  previous: FileFingerprint | undefined,
): NativeUnitStatus {
  if (!current) return 'new'
  const action = reconcileFile(current, previous ? { fingerprint: previous, mcpInventory: [], turns: [] } : undefined)
  if (action.action === 'unchanged') return 'unchanged'
  if (action.action === 'appended') return 'appended'
  if (action.action === 'modified') return 'changed'
  return 'new'
}

async function parseSourceCalls(
  provider: Provider,
  source: SessionSource,
  dateRange: DateRange,
): Promise<ParsedProviderCall[]> {
  const parser = provider.createSessionParser(source, new Set(), dateRange)
  const calls: ParsedProviderCall[] = []
  for await (const parsed of parser.parse()) calls.push(parsed)
  if (calls.length === 0 && provider.name === 'claude') {
    return loadClaudeCalls(source.path)
  }
  return calls
}

async function warmClaudeSpecialPath(dependencies: SourceReaderDependencies): Promise<void> {
  if (dependencies.parseAllSessions) {
    await dependencies.parseAllSessions(dependencies.dateRange, 'claude')
    return
  }
  const { parseAllSessions } = await import('../../src/parser.js')
  const { loadCache, monthScopeForRange } = await import('../../src/session-cache.js')
  await loadCache(monthScopeForRange(dependencies.dateRange.start, dependencies.dateRange.end))
  await parseAllSessions(dependencies.dateRange, 'claude')
}

async function peekNativeEvidence(source: SessionSource): Promise<NativeClassifierEvidence> {
  let isFile = false
  try {
    isFile = statSync(source.path).isFile()
  } catch {
    return {}
  }
  if (!isFile) return {}

  const stream = createReadStream(source.path, { encoding: 'utf8' })
  const lines = createInterface({ input: stream, crlfDelay: Infinity })
  let originator: string | undefined
  let entrypoint: string | undefined
  let seen = 0
  try {
    for await (const line of lines) {
      seen += 1
      const record = parseObjectLine(line)
      if (record) {
        originator ??= stringField(record, 'originator') ?? payloadString(record, 'originator')
        entrypoint ??= stringField(record, 'entrypoint') ?? payloadString(record, 'entrypoint')
      }
      if ((originator && entrypoint) || seen >= EVIDENCE_LINE_LIMIT) break
    }
  } finally {
    stream.destroy()
    lines.close()
  }
  return {
    ...(originator !== undefined ? { originator } : {}),
    ...(entrypoint !== undefined ? { entrypoint } : {}),
  }
}

function toEnvelope(
  harnessId: HarnessId,
  sourceKey: string,
  source: SessionSource,
  parsed: ParsedProviderCall,
  revisionToken: string,
): SourceRecordEnvelope {
  return {
    harnessId,
    sourceKey,
    source,
    nativeSessionId: parsed.sessionId,
    ...(parsed.turnId ? { nativeRecordId: parsed.turnId } : {}),
    timestamp: parsed.timestamp,
    call: parsed,
    revisionToken,
  }
}

function parseObjectLine(line: string): Record<string, unknown> | undefined {
  const trimmed = line.trim()
  if (trimmed === '') return undefined
  try {
    const parsed: unknown = JSON.parse(trimmed)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return undefined
    return parsed as Record<string, unknown>
  } catch {
    return undefined
  }
}

function stringField(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key]
  return typeof value === 'string' && value.trim() !== '' ? value : undefined
}

function payloadString(record: Record<string, unknown>, key: string): string | undefined {
  const payload = record['payload']
  if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) return undefined
  return stringField(payload as Record<string, unknown>, key)
}
