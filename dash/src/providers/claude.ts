import { readFileSync, readdirSync, statSync } from 'fs'
import { readFile, readdir, stat } from 'fs/promises'
import { basename, delimiter as pathDelimiter, extname, join, resolve, sep } from 'path'
import { homedir } from 'os'
import { createHash } from 'crypto'

import type { Provider, ProbeRoot, SessionSource, SessionParser, ParsedProviderCall } from './types.js'
import type { DateRange, ToolCall } from '../types.js'
import { getShortModelName } from '../pricing/models.js'
import { readConfig } from '../config.js'
import { FS_SCAN_CONCURRENCY, mapWithConcurrency } from '../ingest/fs-utils.js'
import { extractBashCommands } from '../ingest/bash-utils.js'

export type ClaudeConfigSource = {
  id: string
  label: string
  path: string
}

function expandHome(p: string): string {
  if (p === '~') return homedir()
  if (p.startsWith('~/') || p.startsWith('~\\')) return join(homedir(), p.slice(2))
  return p
}

function dedupeResolved(paths: string[]): string[] {
  const seen = new Set<string>()
  const out: string[] = []
  for (const p of paths) {
    if (!seen.has(p)) {
      seen.add(p)
      out.push(p)
    }
  }
  return out
}

function claudeConfigSourceId(path: string): string {
  return 'claude-config:' + createHash('sha256').update(path).digest('hex').slice(0, 16)
}

function baseClaudeConfigLabel(path: string): string {
  const normalized = resolve(path)
  if (normalized === resolve(join(homedir(), '.claude'))) return 'Default Claude'
  const name = basename(normalized).replace(/^\./, '').trim()
  return name || normalized
}

function makeUniqueLabels(sources: ClaudeConfigSource[]): ClaudeConfigSource[] {
  const counts = new Map<string, number>()
  for (const source of sources) counts.set(source.label, (counts.get(source.label) ?? 0) + 1)
  if (![...counts.values()].some(count => count > 1)) return sources

  const seen = new Map<string, number>()
  return sources.map(source => {
    if ((counts.get(source.label) ?? 0) <= 1) return source
    const index = (seen.get(source.label) ?? 0) + 1
    seen.set(source.label, index)
    return { ...source, label: `${source.label} ${index}` }
  })
}

/// Returns every Claude config dir to scan, in priority order with duplicates
/// removed (resolved-path equality). Precedence: `CLAUDE_CONFIG_DIRS` (a
/// `path.delimiter`-separated list, ":" on POSIX, ";" on Windows), then
/// `CLAUDE_CONFIG_DIR` (single dir), then the `claudeConfigDirs` array in
/// `~/.config/codeburn/config.json` (how the macOS menubar configures
/// multi-account aggregation, since a GUI app can't inherit the shell env),
/// then `~/.claude`. Sessions from every returned dir are merged into one
/// ProjectSummary per project name in `src/parser.ts:scanProjectDirs`, so two
/// dirs holding the same sanitized project slug naturally aggregate (#208).
export async function getClaudeConfigDirs(): Promise<string[]> {
  const multi = process.env['CLAUDE_CONFIG_DIRS']
  if (multi !== undefined && multi !== '') {
    const dirs = multi
      .split(pathDelimiter)
      .map(s => s.trim())
      .filter(s => s.length > 0)
      .map(s => resolve(expandHome(s)))
    if (dirs.length > 0) return dedupeResolved(dirs)
  }
  const single = process.env['CLAUDE_CONFIG_DIR']
  if (single !== undefined && single !== '') return [resolve(expandHome(single))]

  // Config-file fallback (menubar-driven). Env vars always win so a power user
  // can still override per-shell. A non-array or empty value falls through to
  // the ~/.claude default, matching the "unset" behavior.
  const config = await readConfig()
  if (Array.isArray(config.claudeConfigDirs)) {
    const dirs = config.claudeConfigDirs
      .filter((s): s is string => typeof s === 'string' && s.trim().length > 0)
      .map(s => resolve(expandHome(s.trim())))
    if (dirs.length > 0) return dedupeResolved(dirs)
  }

  return [join(homedir(), '.claude')]
}

export async function discoverClaudeConfigSources(): Promise<ClaudeConfigSource[]> {
  const dirs = await getClaudeConfigDirs()
  return makeUniqueLabels(dirs.map(path => ({
    id: claudeConfigSourceId(path),
    label: baseClaudeConfigLabel(path),
    path,
  })))
}

// Filesystem changes under an unchanged input key intentionally do not invalidate this cache.
const desktopSessionsDirsCache = new Map<string, string[]>()

function cacheDesktopSessionsDirs(key: string, candidates: string[]): string[] {
  const dirs = dedupeResolved(candidates.map(candidate => resolve(candidate)))
  desktopSessionsDirsCache.set(key, dirs)
  return [...dirs]
}

export function getDesktopSessionsDirs(): string[] {
  const override = process.env['KYBERDASH_DESKTOP_SESSIONS_DIR']
  const appDataInput = process.env['APPDATA']
  const localAppDataInput = process.env['LOCALAPPDATA']
  const platform = process.platform
  const cacheKey = JSON.stringify([platform, override ?? null, appDataInput ?? null, localAppDataInput ?? null])
  const cached = desktopSessionsDirsCache.get(cacheKey)
  if (cached) return [...cached]

  if (override) return cacheDesktopSessionsDirs(cacheKey, [override])
  if (platform === 'darwin') {
    return cacheDesktopSessionsDirs(
      cacheKey,
      [join(homedir(), 'Library', 'Application Support', 'Claude', 'local-agent-mode-sessions')],
    )
  }
  if (platform === 'win32') {
    const appData = appDataInput?.trim()
    const candidates = [
      join(appData || join(homedir(), 'AppData', 'Roaming'), 'Claude', 'local-agent-mode-sessions'),
    ]

    const localAppData = localAppDataInput?.trim()
    const packagesDir = join(localAppData || join(homedir(), 'AppData', 'Local'), 'Packages')
    try {
      const entries = readdirSync(packagesDir, { withFileTypes: true })
        .filter(entry =>
          entry.isDirectory() &&
          (entry.name.startsWith('Claude_') || entry.name.includes('.Claude_')),
        )
        .sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0)

      for (const entry of entries) {
        const sessionsDir = join(
          packagesDir,
          entry.name,
          'LocalCache',
          'Roaming',
          'Claude',
          'local-agent-mode-sessions',
        )
        try {
          if (statSync(sessionsDir).isDirectory()) candidates.push(sessionsDir)
        } catch {
          // A package may disappear or be unreadable while Packages is scanned.
        }
      }
    } catch {
      // Missing or unreadable Packages is equivalent to no MSIX candidates.
    }

    return cacheDesktopSessionsDirs(cacheKey, candidates)
  }
  return cacheDesktopSessionsDirs(
    cacheKey,
    [join(homedir(), '.config', 'Claude', 'local-agent-mode-sessions')],
  )
}

async function findDesktopProjectDirs(base: string): Promise<string[]> {
  const results: string[] = []
  async function walk(dir: string, depth: number): Promise<void> {
    if (depth > 8) return
    const entries = await readdir(dir).catch(() => [])
    for (const entry of entries) {
      if (entry === 'node_modules' || entry === '.git') continue
      const full = join(dir, entry)
      const s = await stat(full).catch(() => null)
      if (!s?.isDirectory()) continue
      if (entry === 'projects') {
        const projectDirs = await readdir(full).catch(() => [])
        for (const pd of projectDirs) {
          const pdFull = join(full, pd)
          const pdStat = await stat(pdFull).catch(() => null)
          if (pdStat?.isDirectory()) results.push(pdFull)
        }
      } else {
        await walk(full, depth + 1)
      }
    }
  }
  await walk(base, 0)
  return results
}

// ── Cowork space resolution ────────────────────────────────────────────
// Claude Desktop's local-agent-mode creates one directory per session under
//   <desktopSessionsDir>/<appId>/<workspaceId>/local_<sessionId>/
// Inside each session directory Claude Code stores its own config at
//   .claude/projects/<sanitized-cwd>/
// which is what findDesktopProjectDirs picks up. The actual project name
// lives in the sibling <workspaceId>/local_<sessionId>.json (spaceId field)
// and <workspaceId>/spaces.json (id → name mapping).

interface CoworkSpace { id: string; name: string }
interface CoworkSpacesFile { spaces: CoworkSpace[] }

// Cache spaces.json per workspace directory to avoid redundant reads.
const spacesJsonCache = new Map<string, CoworkSpacesFile | null>()

async function loadSpacesJson(workspaceDir: string): Promise<CoworkSpacesFile | null> {
  if (spacesJsonCache.has(workspaceDir)) return spacesJsonCache.get(workspaceDir) ?? null
  try {
    const raw = await readFile(join(workspaceDir, 'spaces.json'), 'utf-8')
    const parsed: unknown = JSON.parse(raw)
    if (
      parsed !== null &&
      typeof parsed === 'object' &&
      'spaces' in parsed &&
      Array.isArray((parsed as { spaces: unknown }).spaces)
    ) {
      const result = parsed as CoworkSpacesFile
      spacesJsonCache.set(workspaceDir, result)
      return result
    }
  } catch {
    // unreadable or malformed — treat as no spaces
  }
  spacesJsonCache.set(workspaceDir, null)
  return null
}

async function resolveCoworkSpaceName(workspaceDir: string, sessionId: string): Promise<string | null> {
  const [spacesFile, sessionMetaRaw] = await Promise.all([
    loadSpacesJson(workspaceDir),
    readFile(join(workspaceDir, `${sessionId}.json`), 'utf-8').catch(() => null),
  ])
  if (!sessionMetaRaw) return null
  let sessionMeta: unknown
  try { sessionMeta = JSON.parse(sessionMetaRaw) } catch { return null }
  if (sessionMeta === null || typeof sessionMeta !== 'object') return null
  const meta = sessionMeta as Record<string, unknown>

  const spaceId = meta['spaceId']
  if (typeof spaceId === 'string' && spacesFile) {
    const spaceName = spacesFile.spaces.find(s => s.id === spaceId)?.name
    if (spaceName) return spaceName
  }

  // No spaceId (standalone session): fall back to selected folder then title.
  const folders = meta['userSelectedFolders']
  if (Array.isArray(folders) && folders.length > 0 && typeof folders[0] === 'string') {
    return basename(folders[0])
  }
  const title = meta['title']
  if (typeof title === 'string' && title.trim().length > 0) return title.trim()

  return null
}

/** The `message.usage` block of one transcript line, when it is an assistant turn. */
export function claudeUsageOf(rawLine: string): Record<string, unknown> | undefined {
  let record: Record<string, unknown>
  try {
    const parsed: unknown = JSON.parse(rawLine)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return undefined
    record = parsed as Record<string, unknown>
  } catch {
    return undefined
  }
  if (record['type'] !== 'assistant') return undefined
  const message = record['message']
  if (message === null || typeof message !== 'object' || Array.isArray(message)) return undefined
  const usage = (message as Record<string, unknown>)['usage']
  if (usage === null || usage === undefined || typeof usage !== 'object' || Array.isArray(usage)) {
    return undefined
  }
  return usage as Record<string, unknown>
}

/** A finite non-negative counter, or 0 — never a fabricated estimate. */
export function claudeCount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : 0
}

export function claudeText(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value.trim() : undefined
}

/**
 * Read one Claude Code transcript as provider calls — one per assistant turn.
 */
export function loadClaudeCalls(filePath: string): ParsedProviderCall[] {
  let lines: string[]
  try {
    lines = readFileSync(filePath, 'utf-8').split(/\r?\n/)
  } catch {
    return []
  }

  const calls: ParsedProviderCall[] = []
  const fileStem = basename(filePath, extname(filePath))
  let index = 0

  for (const rawLine of lines) {
    const usage = claudeUsageOf(rawLine)
    if (usage === undefined) continue

    let record: Record<string, unknown>
    try {
      record = JSON.parse(rawLine) as Record<string, unknown>
    } catch {
      continue
    }
    const message = record['message'] as Record<string, unknown> | undefined
    if (!message) continue
    const sessionId = claudeText(record['sessionId']) ?? fileStem
    // `uuid` is the transcript's own per-record identity; the index keeps the
    // key unique for a transcript that omits it.
    const messageId = claudeText(record['uuid']) ?? claudeText(message['id']) ?? `turn-${index}`
    index += 1

    const serverToolUse = usage['server_tool_use']
    const webSearchRequests =
      serverToolUse !== null && typeof serverToolUse === 'object' && !Array.isArray(serverToolUse)
        ? claudeCount((serverToolUse as Record<string, unknown>)['web_search_requests'])
        : 0

    const tools: string[] = []
    const bashCommands: string[] = []
    const toolSequence: ToolCall[][] = []
    const turnToolCalls: ToolCall[] = []

    const content = message['content']
    if (Array.isArray(content)) {
      for (const block of content) {
        if (block !== null && typeof block === 'object' && !Array.isArray(block)) {
          const blockObj = block as Record<string, unknown>
          if (blockObj['type'] === 'tool_use') {
            const name = typeof blockObj['name'] === 'string' ? blockObj['name'].trim() : ''
            if (name) {
              tools.push(name)
              const tc: ToolCall = { tool: name }
              const input = blockObj['input']
              if (input !== null && typeof input === 'object' && !Array.isArray(input)) {
                const inputObj = input as Record<string, unknown>
                if (typeof inputObj['file_path'] === 'string') tc.file = inputObj['file_path']
                else if (typeof inputObj['path'] === 'string') tc.file = inputObj['path']

                if (name === 'Bash') {
                  const cmd = inputObj['command']
                  if (typeof cmd === 'string' && cmd.trim()) {
                    tc.command = cmd
                    bashCommands.push(...extractBashCommands(cmd))
                  }
                }
              }
              turnToolCalls.push(tc)
            }
          }
        }
      }
    }
    if (turnToolCalls.length > 0) {
      toolSequence.push(turnToolCalls)
    }

    const newCall: ParsedProviderCall = {
      provider: 'claude',
      model: claudeText(message['model']) ?? 'unknown',
      inputTokens: claudeCount(usage['input_tokens']),
      outputTokens: claudeCount(usage['output_tokens']),
      cacheCreationInputTokens: claudeCount(usage['cache_creation_input_tokens']),
      cacheReadInputTokens: claudeCount(usage['cache_read_input_tokens']),
      cachedInputTokens: claudeCount(usage['cache_read_input_tokens']),
      reasoningTokens: 0,
      webSearchRequests,
      costUSD: 0,
      costIsEstimated: true,
      tools,
      bashCommands,
      ...(toolSequence.length > 0 ? { toolSequence } : {}),
      timestamp: claudeText(record['timestamp']) ?? new Date(0).toISOString(),
      speed: claudeText(usage['speed']) === 'fast' ? 'fast' : 'standard',
      deduplicationKey: `claude:${sessionId}:${messageId}`,
      sessionId,
      userMessage: '',
    }

    const prevCall = calls[calls.length - 1]
    if (prevCall !== undefined && isContiguousPair(prevCall, newCall)) {
      prevCall.tools = Array.from(new Set([...prevCall.tools, ...newCall.tools]))
      prevCall.bashCommands = Array.from(new Set([...prevCall.bashCommands, ...newCall.bashCommands]))
      if (newCall.toolSequence && newCall.toolSequence.length > 0) {
        prevCall.toolSequence = [...(prevCall.toolSequence ?? []), ...newCall.toolSequence]
      }
      prevCall.webSearchRequests = Math.max(prevCall.webSearchRequests, newCall.webSearchRequests)
    } else {
      calls.push(newCall)
    }
  }

  return calls
}

function isContiguousPair(prev: ParsedProviderCall, next: ParsedProviderCall): boolean {
  if (prev.sessionId !== next.sessionId) return false
  if (prev.model !== next.model) return false
  if (
    prev.inputTokens !== next.inputTokens ||
    prev.outputTokens !== next.outputTokens ||
    prev.cacheReadInputTokens !== next.cacheReadInputTokens ||
    prev.cacheCreationInputTokens !== next.cacheCreationInputTokens
  ) {
    return false
  }
  const isZero =
    prev.inputTokens === 0 &&
    prev.outputTokens === 0 &&
    prev.cacheReadInputTokens === 0 &&
    prev.cacheCreationInputTokens === 0
  if (isZero) return false

  const prevTime = new Date(prev.timestamp).getTime()
  const nextTime = new Date(next.timestamp).getTime()
  if (Number.isFinite(prevTime) && Number.isFinite(nextTime)) {
    if (Math.abs(nextTime - prevTime) > 60_000) return false
  }
  return true
}

export const claude: Provider = {
  name: 'claude',
  displayName: 'Claude',

  modelDisplayName(model: string): string {
    return getShortModelName(model)
  },

  toolDisplayName(rawTool: string): string {
    return rawTool
  },

  // Each config dir's `projects/` subdir is what discoverSessions readdir's,
  // plus the Claude Desktop sessions base. Resolved via the same helpers so a
  // CLAUDE_CONFIG_DIR(S) override is reflected exactly.
  async probeRoots(): Promise<ProbeRoot[]> {
    const dirs = await getClaudeConfigDirs()
    const roots: ProbeRoot[] = dirs.map(dir => ({ path: join(dir, 'projects'), label: 'projects' }))
    roots.push(...getDesktopSessionsDirs().map(path => ({ path, label: 'desktop' })))
    return roots
  },

  async discoverSessions(): Promise<SessionSource[]> {
    const sources: SessionSource[] = []
    const seenProjectDirs = new Set<string>()
    const configSources = await discoverClaudeConfigSources()
    let anyDirReadable = false

    for (const configSource of configSources) {
      const claudeDir = configSource.path
      const projectsDir = join(claudeDir, 'projects')
      let entries: string[]
      try {
        entries = await readdir(projectsDir)
        anyDirReadable = true
      } catch {
        // Missing or unreadable dir is not fatal: a user can configure both
        // a real and a stale path in CLAUDE_CONFIG_DIRS without breaking.
        continue
      }
      // stat() (not the readdir Dirent) decides directory-ness so a symlinked
      // project dir still counts; issue them concurrently, then apply the
      // order-sensitive dedup serially.
      const dirStats = await mapWithConcurrency(entries, FS_SCAN_CONCURRENCY, dirName =>
        stat(join(projectsDir, dirName)).catch(() => null))
      for (const [i, dirName] of entries.entries()) {
        const dirPath = join(projectsDir, dirName)
        // Resolve before deduping so two CLAUDE_CONFIG_DIRS entries that
        // reach the same projects/<slug> directory (via symlinks or
        // overlapping configs) emit only one SessionSource.
        const resolved = resolve(dirPath)
        if (seenProjectDirs.has(resolved)) continue
        const dirStat = dirStats[i]
        if (!dirStat?.isDirectory()) continue
        seenProjectDirs.add(resolved)
        // `project: dirName` is identical across config dirs for the same
        // sanitized slug, which is exactly what makes the parser merge
        // their sessions into a single ProjectSummary.
        sources.push({
          path: dirPath,
          project: dirName,
          provider: 'claude',
          sourceId: configSource.id,
          sourceLabel: configSource.label,
          sourcePath: configSource.path,
          sourceKind: 'claude-config',
        })
      }
    }

    // If the user explicitly set CLAUDE_CONFIG_DIRS and every entry was
    // unreadable, emit a one-line stderr hint. Catches the most common
    // misconfiguration: a Windows user typing `:` (POSIX delimiter) when
    // the platform expects `;`, which produces a single bogus path that
    // silently resolves to nothing on disk.
    const explicitMulti = process.env['CLAUDE_CONFIG_DIRS']
    if (!anyDirReadable && explicitMulti !== undefined && explicitMulti !== '' && configSources.length > 0) {
      process.stderr.write(
        `kyberdash: CLAUDE_CONFIG_DIRS was set but no listed directory could be read. ` +
        `Tried: ${configSources.map(s => s.path).join(', ')}. ` +
        `Use "${pathDelimiter}" as the separator on this platform.\n`,
      )
    }

    for (const desktopBase of getDesktopSessionsDirs()) {
      const desktopDirs = await findDesktopProjectDirs(desktopBase)
      for (const dirPath of desktopDirs) {
        const resolved = resolve(dirPath)
        if (seenProjectDirs.has(resolved)) continue
        seenProjectDirs.add(resolved)

        const desktopSourceId = 'claude-desktop:' + createHash('sha256').update(resolved).digest('hex').slice(0, 16)

        // Path structure: <desktopBase>/<appId>/<workspaceId>/local_<id>/.claude/projects/<slug>
        let projectName = basename(dirPath)
        const resolvedBase = resolve(desktopBase)
        if (resolved.startsWith(resolvedBase + sep) || resolved.startsWith(resolvedBase + '/')) {
          const rel = resolved.slice(resolvedBase.length + 1)
          const parts = rel.split(/[/\\]/)
          // parts = [appId, workspaceId, local_sessionId, .claude, projects, slug]
          if (
            parts.length >= 6 &&
            parts[2]?.startsWith('local_') &&
            parts[3] === '.claude' &&
            parts[4] === 'projects'
          ) {
            const workspaceDir = join(resolvedBase, parts[0]!, parts[1]!)
            const sessionId = parts[2]!
            const spaceName = await resolveCoworkSpaceName(workspaceDir, sessionId)
            if (spaceName) projectName = spaceName
          }
        }

        sources.push({
          path: dirPath,
          project: projectName,
          provider: 'claude',
          sourceId: desktopSourceId,
          sourceLabel: 'Claude Desktop',
          sourcePath: desktopBase,
          sourceKind: 'claude-desktop',
        })
      }
    }

    return sources
  },

  createSessionParser(source: SessionSource, seenKeys: Set<string>, dateRange?: DateRange): SessionParser {
    return {
      async *parse(): AsyncGenerator<ParsedProviderCall> {
        let isFile = false
        try {
          isFile = statSync(source.path).isFile()
        } catch {
          return
        }

        const files: string[] = []
        if (isFile) {
          files.push(source.path)
        } else {
          try {
            const entries = readdirSync(source.path, { recursive: true, encoding: 'utf8' })
            for (const entry of entries) {
              const str = typeof entry === 'string' ? entry : String(entry)
              if (str.endsWith('.jsonl')) {
                files.push(join(source.path, str))
              }
            }
          } catch {
            return
          }
        }

        for (const file of files) {
          for (const call of loadClaudeCalls(file)) {
            if (seenKeys.has(call.deduplicationKey)) continue
            if (dateRange) {
              const ts = new Date(call.timestamp).getTime()
              if (dateRange.start && ts < new Date(dateRange.start).getTime()) continue
              if (dateRange.end && ts > new Date(dateRange.end).getTime()) continue
            }
            seenKeys.add(call.deduplicationKey)
            yield call
          }
        }
      },
    }
  },
}

export default claude
