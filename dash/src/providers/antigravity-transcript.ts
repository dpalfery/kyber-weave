// Antigravity tool and skill invocations, read from the conversation transcript
// at <root>/brain/<conversationId>/.system_generated/logs/transcript.jsonl.
//
// Each PLANNER_RESPONSE step carries `tool_calls: [{ name, args }]`; every `args`
// value is a JSON-encoded string. The transcript holds no token counts, so the
// steps are attached to calls by segment, not by timestamp: a call built from
// gen_metadata carries `agy-step:<last_step_index>`, and that index bounds the
// steps its model generation owned (see segmentTranscript).
//
// A skill invocation is INFERRED, not reported: Antigravity has no dedicated skill
// step, so a `view_file` whose AbsolutePath ends in `SKILL.md` counts as an
// invocation of the skill named by that file's parent directory. The result feeds
// `kyber.skills.invoked`.

import { basename, dirname, join } from 'path'
import { homedir } from 'os'

import { readSessionFile } from '../ingest/fs-utils.js'
import type { ToolCall } from '../types.js'
import type { ParsedProviderCall } from './types.js'

const APP_DATA_DIRS = ['antigravity', 'antigravity-cli', 'antigravity-ide'] as const
const SAFE_CONVERSATION_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/
const SKILL_FILE = 'SKILL.md'
const SKILL_NAME = /^[\w.-]+$/
const FILE_ARGS = ['AbsolutePath', 'TargetFile', 'SearchPath', 'DirectoryPath', 'Path', 'File'] as const
const AGY_STEP_PREFIX = 'agy-step:'

/**
 * The pairing id of a call built from a gen_metadata row. Both the provider that
 * stamps it and the reader that pairs turns on it go through this one format.
 */
export function agyStepId(lastStepIndex: number): string {
  return `${AGY_STEP_PREFIX}${lastStepIndex}`
}

/** The last_step_index a pairing id names, or undefined for any other id. */
export function agyStepIndexOf(pairingId: string | undefined): number | undefined {
  if (pairingId === undefined || !pairingId.startsWith(AGY_STEP_PREFIX)) return undefined
  const digits = pairingId.slice(AGY_STEP_PREFIX.length)
  return /^\d+$/.test(digits) ? Number(digits) : undefined
}

/** One tool call as the model made it, args decoded to plain values. */
export type TranscriptCall = {
  name: string
  args: Record<string, unknown>
}

export type TranscriptStep = {
  /** The transcript's step_index, the unit a segment's bounds are measured in. */
  index: number
  tools: string[]
  toolSequence: ToolCall[]
  skills: string[]
  /** The step's own tool calls. The inferred Skill entries are not among them. */
  calls: TranscriptCall[]
}

/** A GENERIC step, which carries the output of a tool call. */
export type TranscriptResult = {
  index: number
  content: string
}

export type TranscriptRows = {
  steps: TranscriptStep[]
  results: TranscriptResult[]
}

/** The transcript steps one call owns, and the tool result among them when it has one. */
export type TranscriptSegment = {
  steps: TranscriptStep[]
  /**
   * True when the transcript reaches this segment: it holds a step at or before
   * the segment's first step and one after the segment's start. A call before
   * a mid-conversation transcript start is uncovered, so its empty steps are
   * absence, not a measured none (D30).
   */
  covered: boolean
  calls: TranscriptCall[]
  /**
   * Set only when the segment holds exactly one tool call and a GENERIC step: a
   * segment with several calls cannot say which call a shared output belongs to.
   */
  result?: string
}

export function antigravityTranscriptPath(appDataDir: string, conversationId: string, home = homedir()): string {
  return transcriptFile(join(home, '.gemini', appDataDir), conversationId)
}

function transcriptFile(appRoot: string, conversationId: string): string {
  return join(appRoot, 'brain', conversationId, '.system_generated', 'logs', 'transcript.jsonl')
}

/**
 * The home directory above the `.gemini` folder a conversation database sits in,
 * or undefined when the path is not under one. Attachment reads the transcript
 * beside the database it belongs to, not from the machine's home directory.
 */
export function homeOfConversationPath(path: string): string | undefined {
  const marker = path.replace(/\\/g, '/').lastIndexOf('/.gemini/')
  return marker < 0 ? undefined : path.slice(0, marker)
}

/**
 * Decodes one arg value for attribution. The transcript encodes each value twice, so the first pass
 * yields a JSON string literal and the second yields the plain value. The second pass
 * is taken only when it gives a string: a single-encoded path or command fails it and
 * is kept as is, and a number-looking one is never turned into a number.
 */
function decodeArg(value: unknown): unknown {
  if (typeof value !== 'string') return value
  let decoded: unknown
  try {
    decoded = JSON.parse(value)
  } catch {
    return value
  }
  if (typeof decoded !== 'string') return decoded
  try {
    const inner: unknown = JSON.parse(decoded)
    return typeof inner === 'string' ? inner : decoded
  } catch {
    return decoded
  }
}

/**
 * Decodes one arg value to the plain value the model passed, whatever its type.
 * The reader keeps these arguments, so `StartLine` stays a number. Attribution
 * keeps decodeArg's string rule, because a path or command is only ever a string.
 */
function decodeArgValue(value: unknown): unknown {
  if (typeof value !== 'string') return value
  let first: unknown
  try {
    first = JSON.parse(value)
  } catch {
    return value
  }
  if (typeof first !== 'string') return first
  try {
    return JSON.parse(first) as unknown
  } catch {
    return first
  }
}

function stringArg(args: Record<string, unknown>, names: readonly string[]): string | undefined {
  for (const name of names) {
    const value = decodeArg(args[name])
    if (typeof value === 'string' && value !== '') return value
  }
  return undefined
}

/**
 * The skill a SKILL.md path names (its directory), or undefined for any other file.
 *
 * <remarks>
 * The name is the directory, so it is checked as a name: `.` and `..` match the
 * character class but are path segments, and a directory with a space or an at-sign
 * is not a skill name the engine would ever list.
 * </remarks>
 */
export function skillNameFromPath(path: string): string | undefined {
  const normalized = path.replace(/\\/g, '/')
  if (basename(normalized) !== SKILL_FILE) return undefined
  const name = basename(dirname(normalized))
  if (name === '.' || name === '..' || !SKILL_NAME.test(name)) return undefined
  return name
}

/**
 * The one transcript parser. It keeps PLANNER_RESPONSE steps that hold tool calls
 * and GENERIC steps, and skips any line that is not a row with a step_index.
 */
export function parseTranscriptRows(text: string): TranscriptRows {
  const steps: TranscriptStep[] = []
  const results: TranscriptResult[] = []
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
    const index = record['step_index']
    if (typeof index !== 'number' || !Number.isSafeInteger(index)) continue

    if (record['type'] === 'GENERIC') {
      if (typeof record['content'] === 'string') results.push({ index, content: record['content'] })
      continue
    }
    if (record['type'] !== 'PLANNER_RESPONSE' || !Array.isArray(record['tool_calls'])) continue

    const step: TranscriptStep = { index, tools: [], toolSequence: [], skills: [], calls: [] }
    for (const raw of record['tool_calls']) {
      if (typeof raw !== 'object' || raw === null) continue
      const name = (raw as { name?: unknown }).name
      if (typeof name !== 'string' || name === '') continue
      const argsRaw = (raw as { args?: unknown }).args
      const args = typeof argsRaw === 'object' && argsRaw !== null ? (argsRaw as Record<string, unknown>) : {}
      const plainArgs: Record<string, unknown> = {}
      for (const [key, value] of Object.entries(args)) plainArgs[key] = decodeArgValue(value)
      step.calls.push({ name, args: plainArgs })
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
    if (step.calls.length > 0) steps.push(step)
  }
  return { steps, results }
}

/** The steps of a transcript, for callers that need only the tool-call steps. */
export function parseTranscript(text: string): TranscriptStep[] {
  return parseTranscriptRows(text).steps
}

/**
 * Splits a transcript into the segments of one conversation's calls. `lastStepIndexes`
 * is ascending and distinct, one per call. Call N owns the steps with index in
 * (lsi[N], lsi[N+1]]: the model behind call N+1 saw step lsi[N+1] as input, so that
 * step is call N's own output, and the last call's range is open above.
 */
export function segmentTranscript(transcript: TranscriptRows, lastStepIndexes: readonly number[]): TranscriptSegment[] {
  return lastStepIndexes.map((from, i) => {
    const to = lastStepIndexes[i + 1]
    const owns = (index: number): boolean => index > from && (to === undefined || index <= to)
    const steps = transcript.steps.filter((step) => owns(step.index))
    const calls = steps.flatMap((step) => step.calls)
    const output = transcript.results.find((result) => owns(result.index))
    const covered =
      transcript.steps.some((step) => step.index <= from + 1) && transcript.steps.some((step) => step.index > from)
    return {
      steps,
      covered,
      calls,
      ...(calls.length === 1 && output !== undefined ? { result: output.content } : {}),
    }
  })
}

function appRootsFor(home: string, preferredRoot: string | undefined): string[] {
  const dirs = [...(preferredRoot ? [preferredRoot] : []), ...APP_DATA_DIRS.filter((d) => d !== preferredRoot)]
  return dirs.map((dir) => join(home, '.gemini', dir))
}

/**
 * The parsed transcript of one conversation, from the first app-data root that
 * holds one. Undefined when none does, or when the conversation id is unsafe.
 *
 * A transcript over the shared session-file cap, or one that cannot be read, is
 * no transcript at all (F2). Reading it would hold the whole file in memory on
 * every refresh, and a partial read would attribute steps from a file that is not
 * the one the calls were paired with.
 */
export async function readTranscript(
  conversationId: string,
  appRoots: readonly string[],
): Promise<TranscriptRows | undefined> {
  if (!SAFE_CONVERSATION_ID.test(conversationId)) return undefined
  for (const root of appRoots) {
    const text = await readSessionFile(transcriptFile(root, conversationId))
    if (text !== null) return parseTranscriptRows(text)
  }
  return undefined
}

/**
 * Attaches the transcript's tool calls and inferred skill invocations to the calls
 * that carry an agy-step pairing id. Each call takes the steps of its own segment
 * (see segmentTranscript). Calls without a pairing id, such as status-line snapshots,
 * are left alone, and so is any conversation with no transcript: nothing is fabricated.
 */
export async function attachTranscriptActivity(
  calls: ParsedProviderCall[],
  options: { preferredRoot?: string; home?: string },
): Promise<void> {
  const byConversation = new Map<string, ParsedProviderCall[]>()
  for (const call of calls) {
    if (agyStepIndexOf(call.pairingId) === undefined) continue
    // Transcript-derived fields are cleared on every pairing call before anything is
    // looked up, so a transcript that is now absent, unreadable or oversize leaves no
    // activity behind, even on a call served from the cache (F1). The raw parser sets
    // all three to empty, so the reset is the baseline, not a loss.
    call.tools = []
    call.toolSequence = []
    delete call.skills
    const list = byConversation.get(call.sessionId) ?? []
    list.push(call)
    byConversation.set(call.sessionId, list)
  }
  for (const [conversationId, group] of byConversation) {
    const transcript = await readTranscript(conversationId, appRootsFor(options.home ?? homedir(), options.preferredRoot))
    if (transcript === undefined) continue

    // One segment per distinct last_step_index. The first call carrying it takes the
    // segment; a repeat keeps empty attribution rather than doubling the first's tools.
    const callByStep = new Map<number, ParsedProviderCall>()
    for (const call of group) {
      const lastStepIndex = agyStepIndexOf(call.pairingId)
      if (lastStepIndex !== undefined && !callByStep.has(lastStepIndex)) callByStep.set(lastStepIndex, call)
    }
    const ordered = [...callByStep.entries()].sort(([a], [b]) => a - b)
    const segments = segmentTranscript(transcript, ordered.map(([lastStepIndex]) => lastStepIndex))

    ordered.forEach(([, call], i) => {
      const segment = segments[i]!
      call.tools = segment.steps.flatMap((s) => s.tools)
      call.toolSequence = segment.steps.map((s) => s.toolSequence)
      // An empty list on a covered call is a measured none (D6): the transcript
      // was read for this segment and named no skill. An uncovered call has no
      // transcript behind it, so it never gets a list at all.
      if (!segment.covered) {
        delete call.skills
        return
      }
      call.skills = [...new Set(segment.steps.flatMap((s) => s.skills))]
    })
  }
}
