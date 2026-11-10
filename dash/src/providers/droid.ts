import { readdir, stat, readFile } from 'fs/promises'
import { join, basename, dirname } from 'path'
import { homedir } from 'os'

import { readSessionFile, readSessionLines } from '../ingest/fs-utils.js'
import { calculateCost, getShortModelName } from '../pricing/models.js'
import { extractBashCommands } from '../ingest/bash-utils.js'
import { normalizeContentBlocks } from '../ingest/content-utils.js'
import type {
  Provider,
  SessionSource,
  SessionParser,
  ParsedProviderCall,
  ProbeRoot,
} from './types.js'

const toolNameMap: Record<string, string> = {
  Read: 'Read',
  Create: 'Create',
  Edit: 'Edit',
  MultiEdit: 'MultiEdit',
  LS: 'LS',
  Glob: 'Glob',
  Grep: 'Grep',
  Execute: 'Bash',
  AskUser: 'AskUser',
  TodoWrite: 'TodoWrite',
  Skill: 'Skill',
  Task: 'Agent',
  WebSearch: 'WebSearch',
  FetchUrl: 'FetchUrl',
  GenerateDroid: 'GenerateDroid',
  ExitSpecMode: 'ExitSpecMode',
}

type DroidSettings = {
  model?: string
  tokenUsage?: {
    inputTokens: number
    outputTokens: number
    cacheCreationTokens: number
    cacheReadTokens: number
    thinkingTokens: number
  }
}

type DroidContent = {
  type: string
  text?: string
  name?: string
  input?: Record<string, unknown>
}

type DroidMessage = {
  role: string
  content?: DroidContent[]
}

type DroidJsonlEntry = {
  type: string
  id?: string
  timestamp?: string
  message?: DroidMessage
  title?: string
  cwd?: string
}

function parseMs(timestamp: unknown): number | undefined {
  if (typeof timestamp !== 'string' || timestamp === '') return undefined
  const ms = Date.parse(timestamp)
  return Number.isNaN(ms) ? undefined : ms
}

function getFactoryDir(): string {
  return process.env['FACTORY_DIR'] ?? join(homedir(), '.factory')
}


// Strip Droid-specific wrapper to get the model's display name.
// e.g. "custom:GLM-5.1-[Proxy]-0" -> "GLM-5.1"
// Cost lookup is handled by codeburn's existing calculateCost/getCanonicalName
// which normalizes case and strips date suffixes automatically.
function stripModelPrefix(raw: string): string {
  return raw
    .replace(/^custom:/, '')
    .replace(/\[.*?\]/g, '')
    .replace(/-\d+$/, '')
    .replace(/-+$/, '')
    .replace(/^-/, '')
}

function parseModelForDisplay(raw: string): string {
  const stripped = stripModelPrefix(raw)
  const lower = stripped.toLowerCase()

  if (lower.includes('opus')) return getShortModelName(stripped)
  if (lower.includes('sonnet')) return getShortModelName(stripped)
  if (lower.includes('haiku')) return getShortModelName(stripped)
  if (lower.startsWith('gpt-')) return getShortModelName(stripped)
  if (lower.startsWith('o3') || lower.startsWith('o4')) return getShortModelName(stripped)
  if (lower.startsWith('gemini')) return getShortModelName(stripped)

  return stripped
}

/**
 * Extract meaningful shell command names from a Droid Execute call.
 * Droid frequently passes multi-line scripts (python -c "...", heredocs, etc.)
 * where splitting on ;/&&/| produces noise tokens like '}', 'await', 'import'.
 * Instead, extract only the primary command from each logical line.
 */
function extractDroidBashCommands(command: string): string[] {
  if (!command || !command.trim()) return []

  const firstLine = command.split('\n')[0]!.trim()
  return extractBashCommands(firstLine)
}

function createParser(
  source: SessionSource,
  seenKeys: Set<string>,
): SessionParser {
  return {
    async *parse(): AsyncGenerator<ParsedProviderCall> {
      const content = await readSessionFile(source.path)
      if (content === null) return

      // Read the companion settings file for token usage
      const settingsPath = source.path.replace(/\.jsonl$/, '.settings.json')
      let settings: DroidSettings = {}
      try {
        const raw = await readFile(settingsPath, 'utf-8')
        settings = JSON.parse(raw) as DroidSettings
      } catch {
        // No settings file or parse error
      }

      const lines = content.split('\n').filter(l => l.trim())
      let sessionId = ''
      const sessionModelDisplay = settings.model ? stripModelPrefix(settings.model) : 'unknown'
      let currentUserMessage = ''
      let sessionTitle: string | undefined
      // Last point active time is measured from: a human prompt or the previous emitted call.
      let boundaryMs: number | undefined

      // Collect all assistant messages with their tools
      const assistantCalls: Array<{
        id: string
        timestamp: string
        activeDurationMs?: number
        tools: string[]
        bashCommands: string[]
      }> = []

      let pendingTools: string[] = []
      let pendingBashCommands: string[] = []

      for (const line of lines) {
        let entry: DroidJsonlEntry
        try {
          entry = JSON.parse(line) as DroidJsonlEntry
        } catch {
          continue
        }

        if (entry.type === 'session_start') {
          sessionId = entry.id ?? ''
          if (typeof entry.title === 'string' && entry.title.trim() !== '') sessionTitle = entry.title.trim()
          continue
        }

        if (entry.type !== 'message' || !entry.message) continue

        const msg = entry.message

        if (msg.role === 'user') {
          // Extract user text from content
          const texts = normalizeContentBlocks(msg.content)
            .filter(c => c.type === 'text' && c.text)
            .map(c => c.text!)
            .filter(Boolean)
          // Skip system-reminder-only messages
          const nonSystemTexts = texts.filter(t => !t.startsWith('<system-reminder>'))
          if (nonSystemTexts.length > 0) {
            currentUserMessage = nonSystemTexts.join(' ').slice(0, 500)
            const userMs = parseMs(entry.timestamp)
            if (userMs !== undefined) boundaryMs = userMs
          }
          continue
        }

        if (msg.role === 'assistant') {
          const toolUses = normalizeContentBlocks(msg.content).filter(c => c.type === 'tool_use')

          for (const tu of toolUses) {
            const toolName = tu.name ?? ''
            pendingTools.push(toolNameMap[toolName] ?? toolName)

            if (toolName === 'Execute' && tu.input && typeof tu.input['command'] === 'string') {
              pendingBashCommands.push(...extractDroidBashCommands(tu.input['command'] as string))
            }
          }

          // Check if this assistant message has any text content (non-thinking)
          const hasText = normalizeContentBlocks(msg.content).some(c => c.type === 'text' && c.text)

          // Only emit a call entry if there are tools or substantial text
          if (pendingTools.length > 0 || hasText) {
            const callMs = parseMs(entry.timestamp)
            const interval = callMs !== undefined && boundaryMs !== undefined ? callMs - boundaryMs : undefined
            assistantCalls.push({
              id: typeof entry.id === 'string' && entry.id !== '' ? entry.id : `msg-${assistantCalls.length}`,
              timestamp: entry.timestamp ?? '',
              ...(interval !== undefined && interval >= 0 ? { activeDurationMs: interval } : {}),
              tools: [...pendingTools],
              bashCommands: [...pendingBashCommands],
            })
            pendingTools = []
            pendingBashCommands = []
            if (callMs !== undefined && (boundaryMs === undefined || callMs >= boundaryMs)) boundaryMs = callMs
          }
          continue
        }
      }

      if (assistantCalls.length === 0) return

      // KNOWN LIMITATION: Droid records token usage only at session level
      // (settings.tokenUsage), not per-message. We split evenly across the
      // emitted assistant calls and price all of them at settings.model
      // (the latest model the session used). For sessions where the user
      // switched models mid-stream, costs are approximate — we have no
      // ground-truth breakdown to attribute tokens per model.
      const totalTokens = settings.tokenUsage
      if (!totalTokens) return

      const totalInput = totalTokens.inputTokens ?? 0
      const totalOutput = totalTokens.outputTokens ?? 0
      const totalCacheCreation = totalTokens.cacheCreationTokens ?? 0
      const totalCacheRead = totalTokens.cacheReadTokens ?? 0
      const totalThinking = totalTokens.thinkingTokens ?? 0
      const numCalls = assistantCalls.length

      // Distribute evenly across calls
      const inputPerCall = Math.floor(totalInput / numCalls)
      const outputPerCall = Math.floor(totalOutput / numCalls)
      const cacheCreationPerCall = Math.floor(totalCacheCreation / numCalls)
      const cacheReadPerCall = Math.floor(totalCacheRead / numCalls)
      const thinkingPerCall = Math.floor(totalThinking / numCalls)

      for (let i = 0; i < assistantCalls.length; i++) {
        const call = assistantCalls[i]

        // Assign remainder to the last call
        const isLast = i === assistantCalls.length - 1
        const inputTokens = isLast
          ? totalInput - inputPerCall * (numCalls - 1)
          : inputPerCall
        const outputTokens = isLast
          ? totalOutput - outputPerCall * (numCalls - 1)
          : outputPerCall
        const cacheCreationTokens = isLast
          ? totalCacheCreation - cacheCreationPerCall * (numCalls - 1)
          : cacheCreationPerCall
        const cacheReadTokens = isLast
          ? totalCacheRead - cacheReadPerCall * (numCalls - 1)
          : cacheReadPerCall
        const thinkingTokens = isLast
          ? totalThinking - thinkingPerCall * (numCalls - 1)
          : thinkingPerCall

        const dedupKey = `droid:${sessionId}:${call.id}`
        if (seenKeys.has(dedupKey)) continue
        seenKeys.add(dedupKey)

        const costUSD = calculateCost(
          sessionModelDisplay.toLowerCase(),
          inputTokens,
          outputTokens + thinkingTokens,
          cacheCreationTokens,
          cacheReadTokens,
          0,
        )

        // Use the call's timestamp, or session_start timestamp
        const timestamp = call.timestamp || ''

        yield {
          provider: 'droid',
          model: sessionModelDisplay,
          inputTokens,
          outputTokens,
          cacheCreationInputTokens: cacheCreationTokens,
          cacheReadInputTokens: cacheReadTokens,
          cachedInputTokens: cacheReadTokens,
          reasoningTokens: thinkingTokens,
          webSearchRequests: 0,
          costUSD,
          tools: call.tools,
          bashCommands: call.bashCommands,
          timestamp,
          speed: 'standard',
          deduplicationKey: dedupKey,
          userMessage: i === 0 ? currentUserMessage : '',
          sessionId,
          pairingId: call.id,
          ...(call.activeDurationMs !== undefined ? { activeDurationMs: call.activeDurationMs } : {}),
          ...(sessionTitle !== undefined ? { sessionTitle } : {}),
        }
      }
    },
  }
}

function isInternalSession(cwd: string, factoryDir: string): boolean {
  // Skip sessions whose cwd is the .factory directory itself (internal housekeeping)
  const normalized = cwd.replace(/\/+$/, '')
  return normalized === factoryDir
}

/**
 * The project a Factory session belongs to, derived from its working directory.
 *
 * Exported so the live counter sync names a session's project the same way the
 * file source's own discovery does: two derivations of the same name would let
 * the same session be filed under two projects.
 *
 * The home directory is stripped only at a path boundary: a directory whose
 * name merely starts with the same characters (`/home/alice2` beside
 * `/home/alice`) is not under the home, and stripping the prefix there would
 * file the session under a project that belongs to someone else's tree.
 */
export function deriveProjectName(cwd: string, home: string = homedir()): string {
  const normalized = cwd.replace(/\/+$/, '')

  // Strip home directory prefix
  let relative = normalized
  if (normalized === home) {
    relative = ''
  } else {
    const boundary = [home + '/', home + '\\'].find((prefix) => normalized.startsWith(prefix))
    relative = boundary !== undefined ? normalized.slice(boundary.length) : normalized.replace(/^\/+/, '')
  }

  if (!relative) relative = '~'

  // Walk from the right: use the "projects/<name>" segment if present,
  // otherwise the last meaningful path component.
  const parts = relative.split('/')
  const projectsIdx = parts.lastIndexOf('projects')
  if (projectsIdx !== -1 && projectsIdx + 1 < parts.length) {
    return parts.slice(projectsIdx + 1).join('/')
  }

  return parts.join('/')
}

/**
 * The project a Factory session belongs to, from the session's own start entry.
 *
 * The file source's discovery derives it here and the live counter pass
 * derives it through the same function, so one session is never filed under
 * two project names by the two paths that read it.
 *
 * `undefined` means the session is the harness's own housekeeping — its
 * working directory is the Factory root itself — and the file source keeps no
 * counters for it.
 */
export function deriveSessionProject(
  start: { cwd?: unknown },
  fallbackName: string,
  factoryDir: string,
): string | undefined {
  // The working directory is the transcript's own claim, and a claim is only
  // text when it is text: a number or a list is not a path, so the session
  // falls back to the name of the directory it was found in.
  const cwd = typeof start.cwd === 'string' && start.cwd !== '' ? start.cwd : fallbackName
  if (isInternalSession(cwd, factoryDir)) return undefined
  return deriveProjectName(cwd)
}

/**
 * The project a Factory session belongs to, read from the session's own file.
 *
 * The live counter pass reads it here, so the rows it writes carry the
 * project the file source would give the same session.
 */
export async function sessionProjectOf(
  transcriptPath: string,
  factoryDir: string,
): Promise<string | undefined> {
  const first = await readFirstJsonlLine(transcriptPath)
  if (first === null) return undefined
  let parsed: unknown
  try {
    parsed = JSON.parse(first)
  } catch {
    return undefined
  }
  // The first line is the file's own claim, and it can be any JSON value. A
  // value that is not a session start says nothing about the session, and it
  // must not throw: one corrupt line must not fail the pass that reads it.
  if (typeof parsed !== 'object' || parsed === null) return undefined
  const start = parsed as DroidJsonlEntry
  if (start.type !== 'session_start') return undefined
  return deriveSessionProject(start, basename(dirname(transcriptPath)), factoryDir)
}

async function readFirstJsonlLine(filePath: string): Promise<string | null> {
  for await (const line of readSessionLines(filePath)) {
    return line
  }
  return null
}

async function discoverSessionsInDir(
  sessionsDir: string,
  factoryDir: string,
): Promise<SessionSource[]> {
  const sources: SessionSource[] = []

  let entries: string[]
  try {
    entries = await readdir(sessionsDir)
  } catch {
    return sources
  }

  for (const entry of entries) {
    const subDir = join(sessionsDir, entry)
    const s = await stat(subDir).catch(() => null)
    if (!s?.isDirectory()) continue

    const files = await readdir(subDir).catch(() => [] as string[])
    for (const file of files) {
      if (!file.endsWith('.jsonl')) continue
      const filePath = join(subDir, file)

      const firstLine = await readFirstJsonlLine(filePath)
      if (!firstLine?.trim()) continue

      let startEntry: DroidJsonlEntry
      try {
        startEntry = JSON.parse(firstLine) as DroidJsonlEntry
      } catch {
        continue
      }

      if (startEntry.type !== 'session_start') continue

      const cwd = startEntry.cwd ?? entry
      if (isInternalSession(cwd, factoryDir)) continue
      const project = deriveSessionProject(startEntry, entry, factoryDir)
      if (project === undefined) continue

      sources.push({
        path: filePath,
        project,
        provider: 'droid',
      })
    }
  }

  return sources
}

export function createDroidProvider(factoryDir?: string): Provider {
  const base = factoryDir ?? getFactoryDir()
  const sessionsDir = join(base, 'sessions')

  return {
    name: 'droid',
    displayName: 'Droid',

    modelDisplayName(model: string): string {
      return parseModelForDisplay(model)
    },

    toolDisplayName(rawTool: string): string {
      return toolNameMap[rawTool] ?? rawTool
    },

    async probeRoots(): Promise<ProbeRoot[]> {
      return [{ path: sessionsDir, label: 'sessions' }]
    },

    async discoverSessions(): Promise<SessionSource[]> {
      return discoverSessionsInDir(sessionsDir, base)
    },

    createSessionParser(
      source: SessionSource,
      seenKeys: Set<string>,
    ): SessionParser {
      return createParser(source, seenKeys)
    },
  }
}

export const droid = createDroidProvider()
