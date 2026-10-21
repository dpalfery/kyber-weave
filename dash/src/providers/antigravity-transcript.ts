// Antigravity tool and skill invocations, read from the conversation transcript
// at <root>/brain/<conversationId>/.system_generated/logs/transcript.jsonl.
//
// Each PLANNER_RESPONSE step carries `tool_calls: [{ name, args }]`; every `args`
// value is a JSON-encoded string. The transcript holds no token counts, so a step
// is attached to a parsed call by timestamp (an inference, see attachTranscriptActivity).
//
// A skill invocation is INFERRED, not reported: Antigravity has no dedicated skill
// step, so a `view_file` whose AbsolutePath ends in `SKILL.md` counts as an
// invocation of the skill named by that file's parent directory. The result feeds
// `kyber.skills.invoked`.

import { readFile } from 'fs/promises'
import { basename, dirname, join } from 'path'
import { homedir } from 'os'

import type { ToolCall } from '../types.js'
import type { ParsedProviderCall } from './types.js'

const APP_DATA_DIRS = ['antigravity', 'antigravity-cli', 'antigravity-ide'] as const
const SAFE_CONVERSATION_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/
const SKILL_FILE = 'SKILL.md'
const FILE_ARGS = ['AbsolutePath', 'TargetFile', 'SearchPath', 'DirectoryPath', 'Path', 'File'] as const

export type TranscriptStep = {
  at: number
  tools: string[]
  toolSequence: ToolCall[]
  skills: string[]
}

export function antigravityTranscriptPath(appDataDir: string, conversationId: string, home = homedir()): string {
  return join(home, '.gemini', appDataDir, 'brain', conversationId, '.system_generated', 'logs', 'transcript.jsonl')
}

function decodeArg(value: unknown): unknown {
  if (typeof value !== 'string') return value
  try {
    return JSON.parse(value)
  } catch {
    return value
  }
}

function stringArg(args: Record<string, unknown>, names: readonly string[]): string | undefined {
  for (const name of names) {
    const value = decodeArg(args[name])
    if (typeof value === 'string' && value !== '') return value
  }
  return undefined
}

/** The skill a SKILL.md path names (its directory), or undefined for any other file. */
export function skillNameFromPath(path: string): string | undefined {
  const normalized = path.replace(/\\/g, '/')
  if (basename(normalized) !== SKILL_FILE) return undefined
  const name = basename(dirname(normalized))
  return name === '' || name === '.' || name === '/' ? undefined : name
}

export function parseTranscript(text: string): TranscriptStep[] {
  const steps: TranscriptStep[] = []
  for (const line of text.split(/\r?\n/)) {
    if (!line.trim()) continue
    let row: unknown
    try {
      row = JSON.parse(line)
    } catch {
      continue
    }
    if (typeof row !== 'object' || row === null) continue
    const record = row as Record<string, unknown>
    if (record['type'] !== 'PLANNER_RESPONSE' || !Array.isArray(record['tool_calls'])) continue
    const at = typeof record['created_at'] === 'string' ? Date.parse(record['created_at']) : Number.NaN
    if (Number.isNaN(at)) continue

    const step: TranscriptStep = { at, tools: [], toolSequence: [], skills: [] }
    for (const raw of record['tool_calls']) {
      if (typeof raw !== 'object' || raw === null) continue
      const name = (raw as { name?: unknown }).name
      if (typeof name !== 'string' || name === '') continue
      const argsRaw = (raw as { args?: unknown }).args
      const args = typeof argsRaw === 'object' && argsRaw !== null ? (argsRaw as Record<string, unknown>) : {}
      const file = stringArg(args, FILE_ARGS)
      const command = stringArg(args, ['CommandLine'])
      step.tools.push(name)
      step.toolSequence.push({ tool: name, ...(file ? { file } : {}), ...(command ? { command } : {}) })
      const skill = name === 'view_file' && file ? skillNameFromPath(file) : undefined
      if (skill) {
        step.skills.push(skill)
        step.tools.push('Skill')
        step.toolSequence.push({ tool: 'Skill', file: skill })
      }
    }
    if (step.tools.length > 0) steps.push(step)
  }
  return steps
}

async function readSteps(conversationId: string, preferredRoot: string | undefined, home: string): Promise<TranscriptStep[]> {
  if (!SAFE_CONVERSATION_ID.test(conversationId)) return []
  const roots = [...(preferredRoot ? [preferredRoot] : []), ...APP_DATA_DIRS.filter((d) => d !== preferredRoot)]
  for (const root of roots) {
    const text = await readFile(antigravityTranscriptPath(root, conversationId, home), 'utf-8').catch(() => null)
    if (text !== null) return parseTranscript(text)
  }
  return []
}

/**
 * Attaches transcript tool calls and inferred skill invocations to the calls of
 * one conversation. The transcript has no usage, so each step goes to a call by
 * timestamp: `nearest` (SQLite / RPC calls, stamped at the generation) or
 * `following` (status-line snapshots, stamped after the work they summarize; the
 * last snapshot takes any later step). When the transcript is missing, calls are
 * left untouched: nothing is fabricated.
 */
export async function attachTranscriptActivity(
  calls: ParsedProviderCall[],
  options: { preferredRoot?: string; mode: 'nearest' | 'following'; home?: string },
): Promise<void> {
  const byConversation = new Map<string, ParsedProviderCall[]>()
  for (const call of calls) {
    const list = byConversation.get(call.sessionId) ?? []
    list.push(call)
    byConversation.set(call.sessionId, list)
  }
  for (const [conversationId, group] of byConversation) {
    const steps = await readSteps(conversationId, options.preferredRoot, options.home ?? homedir())
    if (steps.length === 0) continue
    const times = group.map((c) => Date.parse(c.timestamp))
    const order = group.map((_, i) => i).filter((i) => !Number.isNaN(times[i]!)).sort((a, b) => times[a]! - times[b]!)
    if (order.length === 0) continue

    const buckets = new Map<number, TranscriptStep[]>()
    for (const step of steps) {
      let target = order[order.length - 1]!
      if (options.mode === 'following') {
        target = order.find((i) => times[i]! >= step.at) ?? target
      } else {
        let best = Number.POSITIVE_INFINITY
        for (const i of order) {
          const d = Math.abs(times[i]! - step.at)
          if (d < best) {
            best = d
            target = i
          }
        }
      }
      const list = buckets.get(target) ?? []
      list.push(step)
      buckets.set(target, list)
    }
    for (const [index, list] of buckets) {
      const call = group[index]!
      call.tools = list.flatMap((s) => s.tools)
      call.toolSequence = list.map((s) => s.toolSequence)
      const skills = [...new Set(list.flatMap((s) => s.skills))]
      if (skills.length > 0) call.skills = skills
      else delete call.skills
    }
  }
}
