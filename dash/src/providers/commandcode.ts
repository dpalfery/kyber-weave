// Command Code session provider.
//
// Format verified against the published `command-code` npm package 1.84.0
// (2026-10-17). Sessions are JSONL at
// `~/.commandcode/projects/<slugified-cwd>/<session-id>.jsonl`, opening with a
// `{type:"session", version:3, id, timestamp, cwd}` header, the same tree
// shape Pi writes. Sidecar files beside a transcript (`.meta.json`,
// `.checkpoints.jsonl`, `.prompts.jsonl`, `.v2.bak`) are not transcripts.
//
// Unlike Pi, usage sits on the entry, not the message: an assistant
// `message` entry carries `usage: {inputTokens, outputTokens, cacheReadTokens,
// cacheWriteTokens, costUsd?}` and `model`. The counters come from the AI SDK's
// `totalUsage`, whose `inputTokens` INCLUDES both cache classes, so fresh input
// is recovered by subtracting them before the call is yielded.

import { readdir, stat } from 'fs/promises'
import { basename, join } from 'path'
import { homedir } from 'os'

import { readSessionFile, readSessionLines } from '../ingest/fs-utils.js'
import { calculateCost } from '../pricing/models.js'
import { extractBashCommands } from '../ingest/bash-utils.js'
import { normalizeContentBlocks } from '../ingest/content-utils.js'
import type { ProbeRoot, Provider, SessionSource, SessionParser, ParsedProviderCall } from './types.js'

const toolNameMap: Record<string, string> = {
  read_file: 'Read',
  read_directory: 'LS',
  write_file: 'Write',
  edit_file: 'Edit',
  glob: 'Glob',
  grep: 'Grep',
  shell_command: 'Bash',
  web_search: 'WebSearch',
  web_fetch: 'WebFetch',
  todo_write: 'TodoWrite',
  agent: 'Agent',
  activate_skill: 'Skill',
  enter_plan_mode: 'EnterPlanMode',
  exit_plan_mode: 'ExitPlanMode',
}

type CommandCodeContentBlock = {
  type?: string
  text?: string
  name?: string
  input?: Record<string, unknown>
}

type CommandCodeEntry = {
  type: string
  id?: string
  timestamp?: string
  cwd?: string
  model?: string
  message?: {
    role?: string
    content?: CommandCodeContentBlock[] | string
  }
  usage?: {
    inputTokens?: number
    outputTokens?: number
    cacheReadTokens?: number
    cacheWriteTokens?: number
    costUsd?: number
  }
}

function getProjectsDir(override?: string): string {
  return override ?? join(homedir(), '.commandcode', 'projects')
}

/** Sidecars share the `.jsonl` suffix but are not transcripts (the CLI's own rule). */
function isTranscriptFileName(name: string): boolean {
  return name.endsWith('.jsonl') &&
    !name.includes('.checkpoints.') &&
    !name.includes('.prompts.') &&
    !name.includes('.v2.bak')
}

const MAX_HEADER_LINES_SCANNED = 20

async function readSessionHeader(filePath: string): Promise<CommandCodeEntry | null> {
  let linesScanned = 0
  for await (const line of readSessionLines(filePath)) {
    if (linesScanned >= MAX_HEADER_LINES_SCANNED) break
    linesScanned++
    const trimmed = line.trim()
    if (!trimmed) continue
    try {
      const entry = JSON.parse(trimmed) as CommandCodeEntry
      if (entry.type === 'session') return entry
    } catch {
      continue
    }
  }
  return null
}

async function discoverSessionsInDir(projectsDir: string): Promise<SessionSource[]> {
  const sources: SessionSource[] = []

  let projectDirs: string[]
  try {
    projectDirs = await readdir(projectsDir)
  } catch {
    return sources
  }

  for (const dirName of projectDirs) {
    const dirPath = join(projectsDir, dirName)
    const dirStat = await stat(dirPath).catch(() => null)
    if (!dirStat?.isDirectory()) continue

    const files = await readdir(dirPath).catch(() => [] as string[])
    for (const file of files) {
      if (!isTranscriptFileName(file)) continue
      const filePath = join(dirPath, file)
      const header = await readSessionHeader(filePath)
      if (!header) continue
      sources.push({
        path: filePath,
        project: basename(header.cwd ?? dirName),
        provider: 'commandcode',
      })
    }
  }

  return sources
}

function nonNegative(value: number | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : 0
}

function createParser(source: SessionSource, seenKeys: Set<string>): SessionParser {
  return {
    async *parse(): AsyncGenerator<ParsedProviderCall> {
      const content = await readSessionFile(source.path)
      if (content === null) return
      const lines = content.split('\n').filter(l => l.trim())
      let sessionId = basename(source.path, '.jsonl')
      let sessionModel = ''
      let sessionTimestamp = ''
      let pendingUserMessage = ''

      for (const [lineIdx, line] of lines.entries()) {
        let entry: CommandCodeEntry
        try {
          entry = JSON.parse(line) as CommandCodeEntry
        } catch {
          continue
        }

        if (entry.type === 'session') {
          sessionId = entry.id ?? sessionId
          if (typeof entry.timestamp === 'string' && entry.timestamp) sessionTimestamp = entry.timestamp
          continue
        }
        if (entry.type === 'model_change') {
          if (typeof entry.model === 'string' && entry.model) sessionModel = entry.model
          continue
        }
        if (entry.type !== 'message' || !entry.message) continue

        const msg = entry.message
        if (msg.role === 'user') {
          const texts = normalizeContentBlocks(msg.content)
            .filter(c => c.type === 'text')
            .map(c => c.text ?? '')
            .filter(Boolean)
          if (texts.length > 0) pendingUserMessage = texts.join(' ')
          continue
        }

        if (msg.role !== 'assistant' || !entry.usage) continue

        const cacheRead = nonNegative(entry.usage.cacheReadTokens)
        const cacheWrite = nonNegative(entry.usage.cacheWriteTokens)
        const input = Math.max(0, nonNegative(entry.usage.inputTokens) - cacheRead - cacheWrite)
        const output = nonNegative(entry.usage.outputTokens)
        if (input === 0 && output === 0 && cacheRead === 0 && cacheWrite === 0) continue

        const dedupKey = `${source.provider}:${source.path}:${entry.id || entry.timestamp || String(lineIdx)}`
        if (seenKeys.has(dedupKey)) continue
        seenKeys.add(dedupKey)

        const model = entry.model || sessionModel || 'unknown'
        const toolCalls = (normalizeContentBlocks(msg.content) as CommandCodeContentBlock[])
          .filter(c => c.type === 'tool_use' && c.name)

        const tools: string[] = []
        const skills: string[] = []
        const subagentTypes: string[] = []
        const bashCommands: string[] = []
        for (const call of toolCalls) {
          const name = call.name!
          tools.push(toolNameMap[name] ?? name)
          const skill = call.input?.['name']
          if (name === 'activate_skill' && typeof skill === 'string' && skill) skills.push(skill)
          const subagent = call.input?.['subagent_type']
          if (name === 'agent' && typeof subagent === 'string' && subagent) subagentTypes.push(subagent)
          const command = call.input?.['command']
          if (name === 'shell_command' && typeof command === 'string') bashCommands.push(...extractBashCommands(command))
        }

        const reportedCost = entry.usage.costUsd
        const costReported = typeof reportedCost === 'number' && Number.isFinite(reportedCost) && reportedCost > 0
        const costUSD = costReported ? reportedCost : calculateCost(model, input, output, cacheWrite, cacheRead, 0)
        const timestamp = entry.timestamp || sessionTimestamp
        if (!timestamp) continue

        yield {
          provider: source.provider,
          model,
          inputTokens: input,
          outputTokens: output,
          cacheCreationInputTokens: cacheWrite,
          cacheReadInputTokens: cacheRead,
          cachedInputTokens: cacheRead,
          reasoningTokens: 0,
          webSearchRequests: 0,
          costUSD,
          ...(costReported ? {} : { costIsEstimated: true }),
          tools,
          bashCommands,
          skills,
          subagentTypes,
          timestamp,
          speed: 'standard',
          deduplicationKey: dedupKey,
          // The assistant message id is a pairing key only (D5). It stays out of
          // `turnId` so record identity keeps the digest it shipped with; the reader
          // pairs on it, and envelopes never carry it as nativeRecordId.
          ...(entry.id ? { pairingId: entry.id } : {}),
          userMessage: pendingUserMessage,
          sessionId,
        }

        pendingUserMessage = ''
      }
    },
  }
}

export function createCommandCodeProvider(projectsDir?: string): Provider {
  const dir = getProjectsDir(projectsDir)

  return {
    name: 'commandcode',
    displayName: 'Command Code',

    async probeRoots(): Promise<ProbeRoot[]> {
      return [{ path: dir, label: 'projects' }]
    },

    modelDisplayName(model: string): string {
      return model
    },

    toolDisplayName(rawTool: string): string {
      return toolNameMap[rawTool] ?? rawTool
    },

    async discoverSessions(): Promise<SessionSource[]> {
      return discoverSessionsInDir(dir)
    },

    createSessionParser(source: SessionSource, seenKeys: Set<string>): SessionParser {
      return createParser(source, seenKeys)
    },
  }
}

export const commandcode = createCommandCodeProvider()
