// The live Factory counters.
//
// WHY: Factory's plugin captures the session's TURNS and nothing else. The
// counters live in the session's own companion file — the
// `<session-id>.settings.json` beside the transcript — and the file source is
// what reads them. The file source is read by the refresh, and an unattended
// refresh reads no folder sources at all (`settings.folder_import.scheduled` is
// off by default), so a live Factory session reached the dashboard with its
// turns and none of its numbers. The numbers are the counters; a session with
// no counters is not a measured session.
//
// WHAT: this pass reads the same companion file the file source reads, for the
// sessions the plugin captured, while those sessions are still running. It
// writes the same records the file source writes — the same span identity, so a
// later folder import replaces them instead of adding to them — and the
// projection pairs them with the plugin's turns on exact session-id equality,
// which is the join ADR 0055 describes.
//
// The file source stays the token source. Nothing here fabricates a counter,
// guesses a model, or reads a session the plugin did not capture.

import { existsSync, type Dirent } from 'node:fs'
import { readdir } from 'node:fs/promises'
import { homedir } from 'node:os'
import { isAbsolute, join, relative, resolve } from 'node:path'

import type { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { acquireStoreRefreshLock, stateDir } from '../refresh/lock.js'
import { createDroidProvider, sessionProjectOf } from '../providers/droid.js'
import type { ParsedProviderCall, SessionSource } from '../providers/types.js'
import { Synthesizer } from '../synth/synth.js'

/** The harness identity Factory's records carry. */
const FACTORY_HARNESS = 'droid'

/**
 * The telemetry source the Kyber-owned Factory plugin's export arrives under.
 *
 * The plugin names itself in its OTLP resource, and that name is what becomes a
 * record's `source`. It is the same name `canon/droid-pairing.ts` splits on, so
 * the live pass and the pairing agree on which records are the plugin's turns.
 */
const FACTORY_PLUGIN_SOURCE = 'kyberdash-factory-plugin'

/** How long a captured session stays eligible for a live counter read. */
const DEFAULT_MAX_AGE_MS = 6 * 60 * 60 * 1000

/** How long an active session waits between two counter reads. */
const DEFAULT_MIN_INTERVAL_MS = 60 * 1000

/** How many sessions one pass reads. */
const DEFAULT_MAX_SESSIONS = 20

/**
 * How far ahead of the reader's clock a call's timestamp may sit and still be
 * read. A Factory transcript can name an instant a moment ahead of the machine
 * that wrote it; a call that far out has not happened yet, and the file
 * source quarantines it for the same reason.
 */
const FUTURE_SKEW_MS = 60 * 1000

/** What one pass read, and what it could not read. */
export type FactoryLiveCountersOutcome =
  | {
      readonly outcome: 'completed'
      /** Sessions whose counters this pass read. */
      readonly sessions: number
      /** Records the pass wrote. */
      readonly records: number
    }
  | { readonly outcome: 'busy' }
  | { readonly outcome: 'failed'; readonly reason: string }

export type FactoryLiveCountersOptions = {
  readonly store: CanonStore
  /** The Factory root. Defaults to the harness's own root. */
  readonly factoryRoot?: string
  /** Where the refresh lock lives. */
  readonly stateDir?: string
  readonly now?: () => number
  /** How long a captured session stays eligible. */
  readonly maxAgeMs?: number
  /** How long an active session waits between two reads. */
  readonly minIntervalMs?: number
  /** How many sessions one pass reads. */
  readonly maxSessions?: number
  /**
   * The throttle's own state. Defaults to this process's map; a caller that
   * needs its own (a test, or a second reader) passes one.
   */
  readonly readStates?: Map<string, SessionReadState>
}

/** What the pass remembers about a session it read. */
export type SessionReadState = {
  /** The plugin's newest record timestamp the pass has already read for. */
  lastSeen: string
  /** When the pass last read the session. */
  readAtMs: number
  /**
   * Whether the session had nothing to read.
   *
   * A session with no transcript — or one whose transcript carries no calls
   * yet — is not read again on the interval: it is read again when the
   * plugin's own records advance, which is when the transcript can have grown.
   * Without this, one absent companion file cost a scan on every tick for the
   * whole window the session stays eligible for.
   */
  readonly empty: boolean
}

/**
 * What this process has already read, per session.
 *
 * The state is a throttle, not a cache: it says which sessions were read and
 * when, so an active session is re-read as its counters move and a finished
 * one is not read again. It is process-local on purpose. A second process
 * re-reads once and then converges on the same state, and a lost map costs one
 * extra read — never a missed one.
 */
const processReadStates = new Map<string, SessionReadState>()

/** How many sessions the throttle remembers. */
const MAX_READ_STATES = 500

/** The most sessions one pass may read, whatever the caller asks for. */
const HARD_MAX_SESSIONS = 1_000

/**
 * A session count the pass can act on.
 *
 * The count reaches a SQL `LIMIT`, so a value that is not a positive safe
 * integer would reach SQLite as an unbounded or negative limit. The caller's
 * value is bounded here instead.
 */
function boundedSessionCount(value: number, fallback: number): number {
  if (!Number.isFinite(value) || value <= 0) return fallback
  return Math.min(Math.floor(value), HARD_MAX_SESSIONS)
}

/**
 * Remember what the pass read for a session.
 *
 * The map is bounded: the oldest entry is dropped when it grows past its
 * limit, and a dropped entry costs one extra read.
 */
function rememberRead(
  readStates: Map<string, SessionReadState>,
  sessionId: string,
  lastSeen: string,
  readAtMs: number,
  empty: boolean,
): void {
  readStates.set(sessionId, { lastSeen, readAtMs, empty })
  if (readStates.size > MAX_READ_STATES) {
    const oldest = readStates.keys().next()
    if (oldest.done !== true) readStates.delete(oldest.value)
  }
}

/**
 * The Factory root the pass reads.
 *
 * An empty `FACTORY_DIR` is an absent root, not the current directory: a
 * relative `sessions` path would read whatever happens to sit beside the
 * process.
 */
export function factoryRootOf(explicit: string | undefined): string {
  if (explicit !== undefined && explicit.trim() !== '') return explicit
  const fromEnv = process.env['FACTORY_DIR']
  if (fromEnv !== undefined && fromEnv.trim() !== '') return fromEnv
  return join(homedir(), '.factory')
}

/**
 * Whether a session id may be used as a file name.
 *
 * The id is read out of the plugin's own span, so it is data and never a
 * path: a value carrying a separator or a `..` would name a file outside the
 * Factory root, and the pass would read whatever it found there. The
 * harness's session ids are UUIDs, so the accepted shape is narrow on purpose.
 */
function isSafeSessionId(sessionId: string): boolean {
  return /^[A-Za-z0-9._-]+$/.test(sessionId) && sessionId !== '.' && sessionId !== '..'
}

/** Whether a path stays under a root, after both are resolved. */
function isUnder(root: string, path: string): boolean {
  const rel = relative(resolve(root), resolve(path))
  return rel !== '' && !rel.startsWith('..') && !isAbsolute(rel)
}

/**
 * The session's own file in the Factory root, or undefined when the harness
 * has no such session.
 *
 * The transcript lives in a per-workspace directory whose name the harness
 * chooses, so the file is found by name rather than by a derived path: a
 * derived path that stopped matching would silently read nothing. The
 * workspace directories are read once per pass and shared by every candidate.
 */
function findSessionTranscript(
  sessionsRoot: string,
  workspaces: readonly string[],
  sessionId: string,
): string | undefined {
  // A session id that is not a plain file name is not a session of ours.
  if (!isSafeSessionId(sessionId)) return undefined
  for (const workspace of workspaces) {
    const path = join(sessionsRoot, workspace, `${sessionId}.jsonl`)
    // Belt and braces: the id is already a plain name, and the path must
    // still resolve under the sessions root.
    if (!isUnder(sessionsRoot, path)) continue
    if (existsSync(path)) return path
  }
  return undefined
}

/** The workspace directories under the Factory session root. */
async function workspaceDirectories(sessionsRoot: string): Promise<string[]> {
  let entries: Dirent<string>[]
  try {
    entries = await readdir(sessionsRoot, { withFileTypes: true })
  } catch (err) {
    // A root that is not there is a root with no sessions. A root that is
    // there and cannot be read is a failure the owner must see: reporting it
    // as an empty list would read as "no Factory sessions", which is a
    // different statement.
    if (isAbsentPathError(err)) return []
    throw new Error(
      `the Factory sessions root could not be read: ${sessionsRoot} (${messageOf(err)})`,
    )
  }
  return entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name)
}

/** Whether an error says the path is not there, rather than that it failed. */
function isAbsentPathError(err: unknown): boolean {
  const code = (err as NodeJS.ErrnoException | undefined)?.code
  return code === 'ENOENT' || code === 'ENOTDIR'
}

/** The message of an error, whatever kind it is. */
function messageOf(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}

/** A call's timestamp as an instant, or undefined when it names none. */
function instantOf(call: ParsedProviderCall): number | undefined {
  // The value comes out of a JSON transcript, so its type is the file's
  // claim and not the parser's: a transcript that names a number must not
  // abort the whole pass.
  const raw: unknown = call.timestamp
  if (typeof raw !== 'string') return undefined
  const text = raw.trim()
  if (text === '') return undefined
  const parsed = Date.parse(text)
  return Number.isNaN(parsed) ? undefined : parsed
}

/**
 * The records the file source would write for a session, read live.
 *
 * The provider and the synthesizer are the file source's own, so the records
 * carry the identity the file source gives them: the same span ids, the same
 * source, the same even split of the session's totals. A later folder import
 * therefore replaces these records instead of adding a second copy of the same
 * counters.
 */
async function readCounterRecords(
  transcriptPath: string,
  project: string,
  factoryRoot: string,
  nowMs: number,
): Promise<CanonicalRecord[]> {
  const provider = createDroidProvider(factoryRoot)
  const source: SessionSource = { path: transcriptPath, project, provider: FACTORY_HARNESS }
  const parser = provider.createSessionParser(source, new Set<string>())

  const calls: ParsedProviderCall[] = []
  for await (const call of parser.parse()) {
    const instant = instantOf(call)
    // A call that names an instant ahead of the clock has not happened yet.
    if (instant !== undefined && instant > nowMs + FUTURE_SKEW_MS) continue
    calls.push(call)
  }
  if (calls.length === 0) return []
  return new Synthesizer().synthesize(calls)
}

/**
 * Read the counters of the sessions the plugin captured, while they run.
 *
 * One pass reads the sessions whose plugin records advanced since the last
 * read, plus the sessions that are still active and are due another read. It
 * writes the counters as the file source's own records, so the projection
 * pairs them with the plugin's turns exactly as it pairs the file source's.
 */
export async function syncFactoryLiveCounters(
  options: FactoryLiveCountersOptions,
): Promise<FactoryLiveCountersOutcome> {
  const store = options.store
  const nowMs = options.now?.() ?? Date.now()
  const maxAgeMs = options.maxAgeMs ?? DEFAULT_MAX_AGE_MS
  const minIntervalMs = options.minIntervalMs ?? DEFAULT_MIN_INTERVAL_MS
  const maxSessions = boundedSessionCount(options.maxSessions ?? DEFAULT_MAX_SESSIONS, DEFAULT_MAX_SESSIONS)
  const factoryRoot = factoryRootOf(options.factoryRoot)
  const sessionsRoot = join(factoryRoot, 'sessions')
  const readStates = options.readStates ?? processReadStates

  // The lock, not a peek at it: a refresh that starts after a probe would
  // otherwise write the same sessions under this pass.
  const lockDirectory = options.stateDir ?? stateDir()
  const lock = await acquireStoreRefreshLock({
    directory: lockDirectory,
    waitMs: 0,
  })
  if (lock.outcome === 'unavailable') {
    return {
      outcome: 'failed',
      reason:
        `the store refresh lock could not be read or created under ${lockDirectory}` +
        (lock.reason !== undefined ? ` (${lock.reason})` : ''),
    }
  }
  if (lock.outcome !== 'acquired') return { outcome: 'busy' }

  try {
    const db = store.getDatabase()
    const candidates = db
      .prepare(
        `SELECT session_id AS session_id, MAX(timestamp) AS last_seen
           FROM records
          WHERE harness = ? AND source = ? AND session_id IS NOT NULL
          GROUP BY session_id
          ORDER BY last_seen DESC
          LIMIT ?`,
      )
      .all(FACTORY_HARNESS, FACTORY_PLUGIN_SOURCE, maxSessions * 4) as Array<{
      session_id: string
      last_seen: string
    }>

    let sessions = 0
    let written = 0
    const workspaces = await workspaceDirectories(sessionsRoot)

    for (const candidate of candidates) {
      if (sessions >= maxSessions) break
      const lastSeenMs = Date.parse(candidate.last_seen)
      if (!Number.isFinite(lastSeenMs) || nowMs - lastSeenMs > maxAgeMs) continue

      const state = readStates.get(candidate.session_id)
      // The throttle compares INSTANTS, not the strings they were written
      // as: the same instant written two ways is the same read, and a string
      // comparison would re-read the session on every tick.
      const advanced = state === undefined || Date.parse(state.lastSeen) !== lastSeenMs
      const dueAgain =
        state !== undefined && !state.empty && nowMs - state.readAtMs >= minIntervalMs
      // A session that is still producing records is re-read on the interval; a
      // session that stopped is read once more when its last record arrives and
      // not again.
      const stillActive = nowMs - lastSeenMs <= minIntervalMs
      if (!advanced && !(dueAgain && stillActive)) continue

      const transcriptPath = findSessionTranscript(sessionsRoot, workspaces, candidate.session_id)
      if (transcriptPath === undefined) {
        // Nothing to read for this session: remember it, so the pass does not
        // look for it again on every tick. The session is read again when its
        // own records advance, which is when a transcript can have appeared.
        rememberRead(readStates, candidate.session_id, candidate.last_seen, nowMs, true)
        continue
      }

      // The project is derived the way the file source derives it, from the
      // session's own file: the two paths must name one session the same way,
      // or the rows they write are two rows for one session.
      const project = await sessionProjectOf(transcriptPath, factoryRoot)
      if (project === undefined) {
        // The session is the harness's own housekeeping, or its transcript
        // names no session: the file source keeps no counters for it either.
        rememberRead(readStates, candidate.session_id, candidate.last_seen, nowMs, true)
        continue
      }

      const records = await readCounterRecords(transcriptPath, project, factoryRoot, nowMs)
      rememberRead(readStates, candidate.session_id, candidate.last_seen, nowMs, records.length === 0)
      if (records.length === 0) continue

      store.upsertMany(records)
      written += records.length
      sessions += 1
    }

    return { outcome: 'completed', sessions, records: written }
  } catch (err) {
    return { outcome: 'failed', reason: err instanceof Error ? err.message : String(err) }
  } finally {
    await lock.handle.release()
  }
}
