// The one KyberDash engine per user.
//
// `kyberdash web` is the engine: it hosts the dashboard, the scheduled jobs and the OTLP
// receiver. Exactly one may run per state directory. Exclusivity comes from the operating
// system, not from a file somebody has to remember to clean up: the engine binds a local
// IPC endpoint (a Unix domain socket, or a named pipe on Windows). A second bind fails
// while the first process lives, and the kernel releases the endpoint the instant that
// process dies, however it dies. `engine.json` next to it records who holds it (pid,
// executable, version, url) so a newcomer can decide whether to attach or to replace.
//
// A caller that finds the endpoint held asks the holder for its record over the endpoint
// itself, so the answer always comes from the live process and never from a leftover file.

import { createHash, randomBytes } from 'crypto'
import { closeSync, fchmodSync, lstatSync, openSync, readFileSync, renameSync, rmSync, writeFileSync } from 'fs'
import { createConnection, createServer, type Server, type Socket } from 'net'
import { dirname, join } from 'path'

export const ENGINE_RECORD_FILE = 'engine.json'
const ENGINE_SOCKET_FILE = 'engine.sock'
/** `sun_path` is 104 bytes on macOS and 108 on Linux; stay under both. */
const MAX_SOCKET_PATH = 100
const QUERY_TIMEOUT_MS = 1500

/** Who holds the engine, as the holder itself reports it. */
export type EngineRecord = {
  pid: number
  /** The executable the engine was started from (`process.execPath`). */
  exe: string
  version: string
  startedAt: string
  /** The dashboard URL once the engine is listening; absent while it is still starting. */
  url?: string
}

type EngineRequest = { op: 'info' } | { op: 'shutdown' }

/** Where the endpoint lives for a state dir. */
export function enginePipePath(stateDir: string, platform: NodeJS.Platform = process.platform): string {
  const digest = createHash('sha256').update(stateDir).digest('hex').slice(0, 16)
  if (platform === 'win32') return `\\\\.\\pipe\\kyberdash-engine-${digest}`
  const inState = join(stateDir, ENGINE_SOCKET_FILE)
  // A long temp-dir path (macOS test homes) would not fit in `sun_path`; fall back to a
  // short per-state-dir name that is still unique to this state dir.
  return inState.length <= MAX_SOCKET_PATH ? inState : `/tmp/kyberdash-engine-${digest}.sock`
}

export type EngineLockOptions = {
  stateDir: string
  version: string
  exe?: string
  /** Called when a peer asks this engine to shut down (a newer version is replacing it). */
  onShutdownRequested?: () => void
  platform?: NodeJS.Platform
}

export type EngineOwner = {
  kind: 'owner'
  record: EngineRecord
  /** Merge fields into the published record (typically `url` once listening). */
  update(patch: Partial<Pick<EngineRecord, 'url'>>): void
  release(): Promise<void>
  /** Synchronous cleanup for `process.on('exit')`, where an async release cannot finish. */
  releaseSync(): void
}

export type EngineAttached = { kind: 'attached'; record: EngineRecord }

function recordPath(stateDir: string): string {
  return join(stateDir, ENGINE_RECORD_FILE)
}

function writeRecord(path: string, record: EngineRecord): void {
  try {
    if (lstatSync(path).isSymbolicLink()) return
  } catch {
    /* absent: the write below creates it */
  }
  const temp = join(dirname(path), `.${ENGINE_RECORD_FILE}.${process.pid}.${randomBytes(6).toString('hex')}.tmp`)
  try {
    const fd = openSync(temp, 'wx', 0o600)
    try {
      writeFileSync(fd, JSON.stringify(record))
      fchmodSync(fd, 0o600)
    } finally {
      closeSync(fd)
    }
    renameSync(temp, path)
  } catch {
    rmSync(temp, { force: true })
  }
}

/** The record file, as a hint only. The live holder's answer over the endpoint is the truth. */
export function readEngineRecordFile(stateDir: string): EngineRecord | null {
  try {
    const parsed = JSON.parse(readFileSync(recordPath(stateDir), 'utf-8')) as Partial<EngineRecord>
    if (typeof parsed.pid !== 'number' || typeof parsed.version !== 'string' || typeof parsed.exe !== 'string') return null
    // Optional fields flow into error messages and the browser launch, so a wrong type is a bad record.
    if (parsed.url !== undefined && typeof parsed.url !== 'string') return null
    if (parsed.startedAt !== undefined && typeof parsed.startedAt !== 'string') return null
    return parsed as EngineRecord
  } catch {
    return null
  }
}

function request(path: string, body: EngineRequest, timeoutMs = QUERY_TIMEOUT_MS): Promise<EngineRecord | 'refused' | 'unresponsive'> {
  return new Promise((resolve) => {
    const socket: Socket = createConnection(path)
    let buffer = ''
    let settled = false
    const finish = (value: EngineRecord | 'refused' | 'unresponsive'): void => {
      if (settled) return
      settled = true
      socket.destroy()
      resolve(value)
    }
    socket.setTimeout(timeoutMs, () => finish('unresponsive'))
    socket.on('connect', () => socket.write(`${JSON.stringify(body)}\n`))
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf-8')
      const newline = buffer.indexOf('\n')
      if (newline === -1) return
      try {
        finish(JSON.parse(buffer.slice(0, newline)) as EngineRecord)
      } catch {
        finish('unresponsive')
      }
    })
    socket.on('error', (err: NodeJS.ErrnoException) => {
      // Nothing is bound (or a leftover socket file nobody listens on): not a live engine.
      finish(err.code === 'ECONNREFUSED' || err.code === 'ENOENT' ? 'refused' : 'unresponsive')
    })
    socket.on('close', () => finish('unresponsive'))
  })
}

/**
 * The live engine's own record, or `null` when no engine holds the endpoint. A holder that
 * accepts the connection but never answers is reported as `'unresponsive'`: something owns
 * the endpoint, so a newcomer must not bind over it.
 */
export async function queryEngine(stateDir: string, platform?: NodeJS.Platform): Promise<EngineRecord | null | 'unresponsive'> {
  const answer = await request(enginePipePath(stateDir, platform), { op: 'info' })
  if (answer === 'refused') return null
  return answer
}

/** Asks the live engine to shut down. True when it acknowledged. */
export async function requestEngineShutdown(stateDir: string, platform?: NodeJS.Platform): Promise<boolean> {
  const answer = await request(enginePipePath(stateDir, platform), { op: 'shutdown' })
  return typeof answer === 'object'
}

function bind(path: string, handler: (socket: Socket) => void): Promise<Server | 'in-use' | Error> {
  return new Promise((resolve) => {
    const server = createServer(handler)
    server.once('error', (err: NodeJS.ErrnoException) => {
      resolve(err.code === 'EADDRINUSE' ? 'in-use' : err)
    })
    server.listen(path, () => resolve(server))
  })
}

/**
 * Takes the engine for this state dir, or reports who already holds it.
 *
 * `attached` means a live engine answered: the caller must not start a second one. A stale
 * socket file left by a SIGKILLed engine refuses connections, is removed, and the bind is
 * retried once - the kernel already freed the endpoint, only the name lingers on POSIX.
 */
export async function acquireEngine(options: EngineLockOptions): Promise<EngineOwner | EngineAttached> {
  const platform = options.platform ?? process.platform
  const path = enginePipePath(options.stateDir, platform)
  const record: EngineRecord = {
    pid: process.pid,
    exe: options.exe ?? process.execPath,
    version: options.version,
    startedAt: new Date().toISOString(),
  }

  const handler = (socket: Socket): void => {
    let buffer = ''
    socket.on('error', () => socket.destroy())
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf-8')
      const newline = buffer.indexOf('\n')
      if (newline === -1) return
      let op: EngineRequest['op'] | null = null
      try {
        const body: unknown = JSON.parse(buffer.slice(0, newline))
        const candidate = typeof body === 'object' && body !== null ? (body as { op?: unknown }).op : undefined
        if (candidate === 'info' || candidate === 'shutdown') op = candidate
      } catch {
        /* malformed: dropped below */
      }
      if (op === null) {
        socket.destroy()
        return
      }
      socket.end(`${JSON.stringify(record)}\n`)
      if (op === 'shutdown') options.onShutdownRequested?.()
    })
  }

  for (let attempt = 0; attempt < 2; attempt++) {
    const bound = await bind(path, handler)
    if (bound instanceof Error) throw bound
    if (bound !== 'in-use') {
      const server = bound
      server.unref()
      writeRecord(recordPath(options.stateDir), record)
      return {
        kind: 'owner',
        record,
        update(patch) {
          if (patch.url !== undefined) record.url = patch.url
          writeRecord(recordPath(options.stateDir), record)
        },
        releaseSync() {
          if (platform !== 'win32') rmSync(path, { force: true })
          const published = readEngineRecordFile(options.stateDir)
          if (published !== null && published.pid === process.pid) rmSync(recordPath(options.stateDir), { force: true })
        },
        async release() {
          await new Promise<void>((resolve) => server.close(() => resolve()))
          if (platform !== 'win32') rmSync(path, { force: true })
          const published = readEngineRecordFile(options.stateDir)
          if (published !== null && published.pid === process.pid) rmSync(recordPath(options.stateDir), { force: true })
        },
      }
    }
    const holder = await request(path, { op: 'info' })
    if (typeof holder === 'object') return { kind: 'attached', record: holder }
    if (holder === 'unresponsive') {
      // Something owns the endpoint and will not talk. Never bind over it.
      return { kind: 'attached', record: readEngineRecordFile(options.stateDir) ?? { pid: -1, exe: '', version: 'unknown', startedAt: '' } }
    }
    // Refused: a leftover name with no process behind it. Clear it and bind again.
    if (platform !== 'win32') rmSync(path, { force: true })
  }
  throw new Error(`could not take the KyberDash engine endpoint at ${path}`)
}
