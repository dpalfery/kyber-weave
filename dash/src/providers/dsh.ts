import { open, readdir, readFile, stat } from 'fs/promises'
import { join } from 'path'
import { homedir } from 'os'
import zlib from 'zlib'

import { MAX_SESSION_FILE_BYTES, readSessionFile } from '../fs-utils.js'
import { calculateCost, getShortModelName } from '../models.js'
import { extractBashCommands } from '../bash-utils.js'
import type { ProbeRoot, Provider, SessionSource, SessionParser, ParsedProviderCall } from './types.js'

// DeepSeek Harness (dsh) stores one session per directory:
//   <DSH_HOME|~/.dsh>/sessions/<encoded-cwd>/session-<uuid>/session.jsonl.zstd
// (or an uncompressed session.jsonl when compression=none). The .zstd file is
// a concatenation of INDEPENDENT zstd frames — one per appended event batch —
// so node:zlib's one-shot zstdDecompressSync (which decodes a single frame)
// must be driven frame-by-frame behind a structural frame-boundary scan. The
// scan below is a port of scanZstdFrames from the official
// @deepseek-ai/dsh-session-persistence-jsonl package, which is third-party code
// under its own license - see THIRD_PARTY_NOTICES.md.

// zstd landed in node:zlib in 22.15 / 23.8; the package floor is lower, so the
// provider degrades with a notice instead of assuming the export exists.
const zstdDecompress = (zlib as { zstdDecompressSync?: (buf: Buffer, opts?: { maxOutputLength?: number }) => Buffer }).zstdDecompressSync

const ZSTD_MAGIC = 0xfd2fb528

// SESSION_FORMAT_VERSION in @deepseek-ai/dsh-session. DSH refuses to load a log
// stamped with any other version, and a bump means an event's meaning changed,
// so a foreign version is skipped rather than read with today's assumptions.
// A zstd frame's declared content size is attacker-controlled, so a few KB of
// crafted input can expand to gigabytes. Every decode is capped: no single
// frame may exceed this, and no file may decode to more than it would have been
// allowed to occupy uncompressed (MAX_SESSION_FILE_BYTES). Overflow throws, and
// the caller skips the WHOLE file rather than counting the frames it got to.
const MAX_FRAME_DECODED_BYTES = 64 * 1024 * 1024

const SESSION_FORMAT_VERSION = 0

const MIN_REASONABLE_TIMESTAMP_MS = 1_000_000_000_000

// Discovery walks every session, so a per-file notice would repeat once per
// log; each distinct message is worth saying exactly once.
const noticed = new Set<string>()

function notice(message: string): void {
  if (noticed.has(message)) return
  noticed.add(message)
  process.stderr.write(message)
}

type ZstdFrame = { start: number; end: number }

// Locate complete frames without decompressing their blocks. An EOF inside the
// final frame (a torn append from a crashed writer) returns its start so the
// caller can ignore the tail; invalid complete structure rejects.
function scanZstdFrames(buffer: Buffer, maxFrames = Number.POSITIVE_INFINITY): { frames: ZstdFrame[]; tornStart?: number } {
  const frames: ZstdFrame[] = []
  let offset = 0
  while (offset < buffer.length) {
    const start = offset
    if (buffer.length - offset < 4) return { frames, tornStart: start }
    if (buffer.readUInt32LE(offset) !== ZSTD_MAGIC) {
      throw new Error(`invalid zstd frame magic at byte ${offset}`)
    }
    offset += 4
    if (offset === buffer.length) return { frames, tornStart: start }
    const descriptor = buffer.readUInt8(offset)!
    offset += 1
    if ((descriptor & 24) !== 0) throw new Error(`reserved frame-header bit at byte ${offset - 1}`)
    const contentSizeFlag = descriptor >>> 6
    const singleSegment = (descriptor & 32) !== 0
    const checksum = (descriptor & 4) !== 0
    const dictionaryFlag = descriptor & 3
    const dictionaryBytes = dictionaryFlag === 3 ? 4 : dictionaryFlag
    const contentSizeBytes = contentSizeFlag === 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag
    const remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes
    if (buffer.length - offset < remainingHeaderBytes) return { frames, tornStart: start }
    offset += remainingHeaderBytes
    for (;;) {
      if (buffer.length - offset < 3) return { frames, tornStart: start }
      const blockHeader = buffer.readUIntLE(offset, 3)
      offset += 3
      const lastBlock = (blockHeader & 1) !== 0
      const blockType = (blockHeader >>> 1) & 3
      const blockSize = blockHeader >>> 3
      if (blockType === 3) throw new Error(`reserved block type at byte ${offset - 3}`)
      const payloadBytes = blockType === 1 ? 1 : blockSize
      if (buffer.length - offset < payloadBytes) return { frames, tornStart: start }
      offset += payloadBytes
      if (lastBlock) break
    }
    if (checksum) {
      if (buffer.length - offset < 4) return { frames, tornStart: start }
      offset += 4
    }
    frames.push({ start, end: offset })
    if (frames.length === maxFrames) return { frames }
  }
  return { frames }
}

type DshUsage = {
  inputTokens?: number
  outputTokens?: number
  cacheReadTokens?: number
  cacheWriteTokens?: number
  reasoningTokens?: number
}

type DshEvent = {
  type?: string
  seq?: number
  time?: number
  // Session header fields live at the top level of the first event.
  version?: number
  id?: string
  cwd?: string
  createdAt?: number
  parentSession?: string
  seedLength?: number
  data?: {
    turn?: number
    step?: number
    content?: Array<{ type?: string; text?: string }>
    // `user/message` carries the message author: a real prompt is
    // `{ kind: 'user' }`, agent-injected context is `{ kind: 'plugin' }`.
    source?: { kind?: string }
    header?: { config?: { model?: string; provider?: string } }
    message?: { source?: { kind?: string; model?: string; provider?: string } }
    chunk?: { type?: string; usage?: DshUsage }
    usage?: DshUsage
    name?: string
    arguments?: string
  }
}

type StepBucket = {
  usage: DshUsage
  // A usage report from assistant/message is the final value for its
  // (turn, step) and replaces an earlier assistant/chunk sample (the two are
  // adjacent reports of the same API call, per dsh-token-meter's usage
  // projection). Time follows the winning report.
  final: boolean
  time?: number
  // Model that produced this step: the reporting assistant/message's own
  // `message.source` when it names one, else the most recent request/header
  // config (a header can change the model mid-turn between steps).
  model: string
  tools: string[]
  skills: string[]
  bashCommands: string[]
}

const toolNameMap: Record<string, string> = {
  bash: 'Bash',
  pwsh: 'Bash',
  read: 'Read',
  write: 'Write',
  edit: 'Edit',
  str_replace_editor: 'Edit',
  glob: 'Glob',
  grep: 'Grep',
  todo_write: 'TodoWrite',
  todo: 'TodoWrite',
  web_search: 'WebSearch',
  skill: 'Skill',
  agent: 'Agent',
  ask_user_question: 'AskUserQuestion',
}

function mapToolName(raw: string): string {
  return toolNameMap[raw] ?? raw
}

// Usage fields are whatever the JSON held. A string or array would flow
// straight into the global token totals and the persisted cache, where
// `0 + [1, 2]` silently becomes "01,2". Same semantics as copilot.ts.
function numberOrZero(raw: unknown): number {
  return typeof raw === 'number' && Number.isFinite(raw) && raw > 0 ? raw : 0
}

// A log stamped with a version this parser was not written against is skipped
// whole: a bump means an event's meaning changed, so reading it with today's
// assumptions would report confident wrong numbers.
function isReadableVersion(header: DshEvent): boolean {
  if (header.version === SESSION_FORMAT_VERSION) return true
  // Keyed on the version, not the path: a DSH upgrade makes EVERY session
  // unreadable at once, and one line per session log is noise, not a report.
  notice(`codeburn: skipping DSH sessions written in session format version ${String(header.version)}; upgrade codeburn.\n`)
  return false
}

// DSH writes epoch milliseconds; promote a seconds-resolution value and reject
// what stays implausible, matching the guard cline-cli.ts uses on the hazard.
function isoTimestamp(value: number | undefined, fallback: string): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || value <= 0) return fallback
  const ms = value < MIN_REASONABLE_TIMESTAMP_MS ? value * 1000 : value
  const date = new Date(ms)
  if (Number.isNaN(date.getTime()) || date.getTime() < MIN_REASONABLE_TIMESTAMP_MS) return fallback
  return date.toISOString()
}

function getDshHome(override?: string): string {
  // An empty-string DSH_HOME is treated as unset.
  return override ?? (process.env['DSH_HOME'] || undefined) ?? join(homedir(), '.dsh')
}

// DSH writes native-platform paths into the header (backslashes on Windows);
// split on both separators so discovery is correct on any host.
function projectFromCwd(cwd: string, fallback: string): string {
  const segments = cwd.split(/[\\/]/).filter(Boolean)
  return segments[segments.length - 1] ?? fallback
}

// Decode every complete frame and yield its JSONL lines. A torn final frame is
// ignored; a structurally corrupt file, or one that decodes past `budget`,
// throws for the caller to report. Exported for the decode-budget test.
export function* readZstdLines(
  buffer: Buffer,
  maxFrames = Number.POSITIVE_INFINITY,
  budget = MAX_SESSION_FILE_BYTES,
): Generator<string> {
  const { frames } = scanZstdFrames(buffer, maxFrames)
  let remaining = budget
  for (const frame of frames) {
    if (remaining <= 0) throw new Error(`decodes past the ${budget}-byte cap`)
    // node throws ERR_BUFFER_TOO_LARGE without allocating past the cap, so the
    // per-frame limit doubles as the running budget for the frames after it.
    const decoded = zstdDecompress!(buffer.subarray(frame.start, frame.end), {
      maxOutputLength: Math.min(remaining, MAX_FRAME_DECODED_BYTES),
    })
    remaining -= decoded.length
    for (const line of decoded.toString('utf-8').split('\n')) {
      if (line.trim()) yield line
    }
  }
}

async function readEventLines(filePath: string): Promise<string[] | null> {
  if (filePath.endsWith('.zstd')) {
    if (!zstdDecompress) {
      notice('codeburn: DSH sessions need Node >= 22.15 (zstd support); skipping DSH usage.\n')
      return null
    }
    let buffer: Buffer
    try {
      // The whole log is buffered to scan its frames, so it needs the same
      // oversize guard readSessionFile applies to the uncompressed variant.
      const size = (await stat(filePath)).size
      if (size > MAX_SESSION_FILE_BYTES) {
        notice(`codeburn: skipped oversize DSH session log ${filePath} (${size} bytes)\n`)
        return null
      }
      buffer = await readFile(filePath)
    } catch {
      return null
    }
    try {
      return [...readZstdLines(buffer)]
    } catch (err) {
      notice(`codeburn: skipped corrupt DSH session log ${filePath}: ${err instanceof Error ? err.message : err}\n`)
      return null
    }
  }
  const content = await readSessionFile(filePath)
  if (content === null) return null
  return content.split('\n').filter(l => l.trim())
}

// Cheap discovery probe: decompress ONLY the first frame (the session header
// batch) instead of the whole log. The header frame is tiny, so a bounded head
// read almost always contains it; fall back to a full read when it does not.
async function readSessionHeader(filePath: string): Promise<DshEvent | null> {
  const firstLine = async (): Promise<string | null> => {
    if (filePath.endsWith('.zstd')) {
      if (!zstdDecompress) return null
      let head: Buffer
      try {
        const handle = await open(filePath, 'r')
        try {
          const size = (await handle.stat()).size
          const length = Math.min(size, 256 * 1024)
          head = Buffer.alloc(length)
          await handle.read(head, 0, length, 0)
        } finally {
          await handle.close()
        }
      } catch {
        return null
      }
      let { frames } = scanZstdFrames(head, 1)
      if (frames.length === 0) {
        // Head read did not cover one full frame; take the whole file. A fork's
        // first batch carries the whole inherited seed, so this is reachable on
        // a real log and needs the same oversize guard as the parse read.
        try {
          if ((await stat(filePath)).size > MAX_SESSION_FILE_BYTES) return null
          const full = await readFile(filePath)
          frames = scanZstdFrames(full, 1).frames
          if (frames.length === 0) return null
          head = full
        } catch {
          return null
        }
      }
      const text = zstdDecompress(head.subarray(frames[0]!.start, frames[0]!.end), {
        maxOutputLength: MAX_FRAME_DECODED_BYTES,
      }).toString('utf-8')
      return text.split('\n').find(l => l.trim()) ?? null
    }
    const content = await readSessionFile(filePath)
    return content?.split('\n').find(l => l.trim()) ?? null
  }

  try {
    const line = await firstLine()
    if (!line) return null
    const event = JSON.parse(line) as DshEvent
    if (event.type !== 'session') return null
    return isReadableVersion(event) ? event : null
  } catch {
    return null
  }
}

async function discoverSessionsInDir(sessionsDir: string): Promise<SessionSource[]> {
  const sources: SessionSource[] = []

  let projectDirs: string[]
  try {
    projectDirs = await readdir(sessionsDir)
  } catch {
    return sources
  }

  for (const dirName of projectDirs) {
    const dirPath = join(sessionsDir, dirName)
    const dirStat = await stat(dirPath).catch(() => null)
    if (!dirStat?.isDirectory()) continue

    let sessionDirs: string[]
    try {
      sessionDirs = await readdir(dirPath)
    } catch {
      continue
    }

    for (const sessionDir of sessionDirs) {
      const sessionPath = join(dirPath, sessionDir)
      const sessionStat = await stat(sessionPath).catch(() => null)
      if (!sessionStat?.isDirectory()) continue

      // Compressed log first; the uncompressed variant exists when
      // compression=none. Never both for the same session.
      let filePath: string | null = null
      for (const name of ['session.jsonl.zstd', 'session.jsonl']) {
        const candidate = join(sessionPath, name)
        const fileStat = await stat(candidate).catch(() => null)
        if (fileStat?.isFile()) {
          filePath = candidate
          break
        }
      }
      if (!filePath) continue

      const header = await readSessionHeader(filePath)
      if (!header) continue

      const cwd = typeof header.cwd === 'string' && header.cwd.trim() ? header.cwd : dirName
      sources.push({ path: filePath, project: projectFromCwd(cwd, dirName), provider: 'dsh' })
    }
  }

  return sources
}

function parseToolArguments(raw: string | undefined): Record<string, unknown> | null {
  if (!raw) return null
  try {
    const parsed = JSON.parse(raw) as unknown
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null
  } catch {
    return null
  }
}

function createParser(source: SessionSource, seenKeys: Set<string>): SessionParser {
  return {
    async *parse(): AsyncGenerator<ParsedProviderCall> {
      const lines = await readEventLines(source.path)
      if (!lines) return

      let sessionId = ''
      let cwd = ''
      let model = 'unknown'
      let currentTurn = 0
      let sessionStart = ''
      // Events a forked session inherited from its parent. They are a verbatim
      // copy of the parent's log, which codeburn parses as its own session, so
      // counting them here would bill the same calls twice.
      let seedLength = 0
      const userMessageByTurn = new Map<number, string>()
      const buckets = new Map<string, StepBucket>()

      for (const line of lines) {
        let event: DshEvent
        try {
          event = JSON.parse(line) as DshEvent
        } catch {
          continue
        }

        if (event.type === 'session') {
          if (!isReadableVersion(event)) return
          sessionId = event.id ?? sessionId
          cwd = event.cwd ?? cwd
          sessionStart = isoTimestamp(event.createdAt, sessionStart)
          if (typeof event.parentSession === 'string' && event.parentSession && typeof event.seedLength === 'number') {
            seedLength = event.seedLength
          }
          continue
        }

        if (typeof event.seq === 'number' && event.seq < seedLength) continue

        if (event.type === 'turn/start') {
          currentTurn = event.data?.turn ?? currentTurn
          continue
        }

        if (event.type === 'request/header') {
          // Emitted at most once per request; steps after the last header
          // inherit its config as their model.
          const headerModel = event.data?.header?.config?.model
          if (typeof headerModel === 'string' && headerModel) model = headerModel
          continue
        }

        if (event.type === 'user/message') {
          // Plugin-injected context (runtime snapshots, skill bodies, file-change
          // notices) rides the same event type as a typed prompt; only the latter
          // is a useful preview.
          if (event.data?.source?.kind !== 'user') continue
          if (userMessageByTurn.has(currentTurn)) continue
          const texts = (event.data?.content ?? [])
            .filter(c => c.type === 'text' && typeof c.text === 'string' && c.text)
            .map(c => c.text!)
          if (texts.length > 0) userMessageByTurn.set(currentTurn, texts.join(' ').slice(0, 500))
          continue
        }

        if (event.type === 'tool/call') {
          const turn = event.data?.turn ?? currentTurn
          const step = event.data?.step ?? 0
          const rawName = event.data?.name
          if (!rawName) continue
          const key = `${turn}:${step}`
          let bucket = buckets.get(key)
          if (!bucket) {
            bucket = { usage: {}, final: false, model, tools: [], skills: [], bashCommands: [] }
            buckets.set(key, bucket)
          }
          bucket.tools.push(mapToolName(rawName))
          const args = parseToolArguments(event.data?.arguments)
          if ((rawName === 'bash' || rawName === 'pwsh') && typeof args?.['command'] === 'string') {
            bucket.bashCommands.push(...extractBashCommands(args['command']))
          }
          if (rawName === 'skill' && typeof args?.['name'] === 'string') {
            bucket.skills.push(args['name'])
          }
          continue
        }

        let usage: DshUsage | undefined
        let isFinal = false
        // The model that actually served the call, when the message records it.
        // request/header only describes the request codeburn is about to see.
        let reportedModel = model
        if (event.type === 'assistant/chunk' && event.data?.chunk?.type === 'usage') {
          usage = event.data.chunk.usage
        } else if (event.type === 'assistant/message' && event.data?.usage) {
          usage = event.data.usage
          isFinal = true
          const messageModel = event.data.message?.source?.model
          if (typeof messageModel === 'string' && messageModel) reportedModel = messageModel
        } else {
          continue
        }
        if (!usage) continue

        const turn = event.data?.turn ?? currentTurn
        const step = event.data?.step ?? 0
        const key = `${turn}:${step}`
        let bucket = buckets.get(key)
        if (!bucket) {
          bucket = { usage: {}, final: false, model, tools: [], skills: [], bashCommands: [] }
          buckets.set(key, bucket)
        }
        // A final report replaces an earlier sample; a late sample never
        // overwrites a final one. The model snapshot follows the winning
        // report (a header can change the model mid-turn between steps).
        if (isFinal || !bucket.final) {
          bucket.usage = usage
          bucket.final = isFinal
          bucket.time = event.time
          bucket.model = reportedModel
        }
      }

      const sortedKeys = [...buckets.keys()].sort((a, b) => {
        const [ta, sa] = a.split(':').map(Number)
        const [tb, sb] = b.split(':').map(Number)
        return ta! - tb! || sa! - sb!
      })

      for (const key of sortedKeys) {
        const bucket = buckets.get(key)!
        const input = numberOrZero(bucket.usage.inputTokens)
        const output = numberOrZero(bucket.usage.outputTokens)
        const cacheRead = numberOrZero(bucket.usage.cacheReadTokens)
        const cacheWrite = numberOrZero(bucket.usage.cacheWriteTokens)
        const reasoning = numberOrZero(bucket.usage.reasoningTokens)
        if (input + output + cacheRead + cacheWrite + reasoning === 0) continue

        const dedupKey = `dsh:${sessionId || source.path}:${key}`
        if (seenKeys.has(dedupKey)) continue
        seenKeys.add(dedupKey)

        // DSH bills reasoning tokens at the output rate (same as Gemini).
        const costUSD = calculateCost(bucket.model, input, output + reasoning, cacheWrite, cacheRead, 0)
        const [turn] = key.split(':').map(Number)

        yield {
          provider: 'dsh',
          model: bucket.model,
          inputTokens: input,
          outputTokens: output,
          cacheCreationInputTokens: cacheWrite,
          cacheReadInputTokens: cacheRead,
          cachedInputTokens: cacheRead,
          reasoningTokens: reasoning,
          webSearchRequests: 0,
          costUSD,
          tools: [...new Set(bucket.tools)],
          bashCommands: bucket.bashCommands,
          skills: bucket.skills.length > 0 ? [...new Set(bucket.skills)] : undefined,
          timestamp: isoTimestamp(bucket.time, sessionStart),
          speed: 'standard',
          deduplicationKey: dedupKey,
          userMessage: userMessageByTurn.get(turn!) ?? '',
          sessionId: sessionId || source.path,
          project: cwd ? projectFromCwd(cwd, source.project) : source.project,
          projectPath: cwd || undefined,
          workingDirectory: cwd || undefined,
        }
      }
    },
  }
}

export function createDshProvider(dshHomeOverride?: string): Provider {
  const dshHome = getDshHome(dshHomeOverride)
  const sessionsDir = join(dshHome, 'sessions')

  return {
    name: 'dsh',
    displayName: 'DeepSeek Harness',

    modelDisplayName(model: string): string {
      return getShortModelName(model)
    },

    toolDisplayName(rawTool: string): string {
      return mapToolName(rawTool)
    },

    async probeRoots(): Promise<ProbeRoot[]> {
      return [{ path: sessionsDir, label: 'sessions' }]
    },

    async discoverSessions(): Promise<SessionSource[]> {
      return discoverSessionsInDir(sessionsDir)
    },

    createSessionParser(source: SessionSource, seenKeys: Set<string>): SessionParser {
      return createParser(source, seenKeys)
    },
  }
}

export const dsh = createDshProvider()
