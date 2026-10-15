/**
 * Naming Service: Session naming, machine-label detection, and honest title synthesis.
 *
 * Grounded in honest unobservability: absent telemetry is never fabricated into
 * synthetic prompt summaries or fake agent identities.
 */

const UUID_RE = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i
const UUID_EXACT_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const SPAN_OP_RE = /\.(?:llm_request|llm_invoke|generate|completion)$/i
const CHAT_MODEL_RE = /^chat\s+\S+/i
const SYNTH_PREFIX_RE = /^(?:synth|derived):/i
const UNSPECIFIED_MODEL_RE = /<(?:synthetic|unknown|unspecified)>|(?::|<)(?:synthetic|unknown)(?:>|$)/i

/**
 * Detects machine-generated telemetry labels (span ops, chat <model>, raw UUIDs,
 * synthetic placeholders) that must not leak as human-readable session titles.
 */
export function isMachineLabel(label: string | null | undefined): boolean {
  if (label == null || typeof label !== 'string') return false
  const v = label.trim()
  if (v === '') return false
  if (SPAN_OP_RE.test(v)) return true
  if (CHAT_MODEL_RE.test(v)) return true
  if (UUID_EXACT_RE.test(v)) return true
  if (SYNTH_PREFIX_RE.test(v)) return true
  if (UNSPECIFIED_MODEL_RE.test(v)) return true
  return false
}

/**
 * Format a session ID for readable display without losing the discriminator.
 */
export function formatSessionId(id: string | null | undefined): string {
  const trimmed = (id ?? '').trim()
  if (trimmed === '') return 'Session'
  let rest = trimmed.replace(SYNTH_PREFIX_RE, '')
  rest = rest.replace(SYNTH_PREFIX_RE, '')
  const uuid = rest.match(UUID_RE)
  if (uuid !== null) {
    const u = uuid[0]
    const prefix = rest.slice(0, rest.indexOf(u)).replace(/[:_-]+$/, '')
    const short = `${u.slice(0, 8)}…${u.slice(-4)}`
    return prefix !== '' ? `${prefix} ${short}` : short
  }
  if (rest.length <= 18) return rest
  return `${rest.slice(0, 9)}…${rest.slice(-8)}`
}

function prettyHarness(raw: string): string {
  const cleaned = raw.replace(/^codeburn\//, '')
  return cleaned
    .replace(/[-_]/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase())
}

export interface FormatSessionTitleInput {
  agentName?: string | null
  agent_name?: string | null
  taskSummary?: string | null
  task_summary?: string | null
  rawLabel?: string | null
  label?: string | null
  harness?: string | null
  sessionId?: string | null
  session_id?: string | null
}

/**
 * Synthesizes a display title for a session:
 * - `<agent name> · <task summary>` when both are available
 * - `<agent name> · <human label>` when task summary is absent but label is human
 * - `<agent name> · session` when task summary is absent or machine label
 * - `<task summary>` when agent name is absent
 * - `<human label>` when agent name and task summary are absent
 * - `<harness> session` or shortened session ID as fallbacks
 *
 * Respects honest unobservability: never fabricates synthetic titles or fake agent names.
 */
export function formatSessionTitle(input: FormatSessionTitleInput = {}): string {
  const agent = (input.agentName ?? input.agent_name)?.trim() || undefined
  const task = (input.taskSummary ?? input.task_summary)?.trim() || undefined
  const raw = (input.rawLabel ?? input.label)?.trim() || undefined
  const humanLabel = raw && !isMachineLabel(raw) ? raw : undefined

  if (agent) {
    if (task) {
      return `${agent} · ${task}`
    }
    if (humanLabel) {
      return `${agent} · ${humanLabel}`
    }
    return `${agent} · session`
  }

  if (task) {
    return task
  }

  if (humanLabel) {
    return humanLabel
  }

  const harness = input.harness?.trim()
  if (harness) {
    return `${prettyHarness(harness)} session`
  }

  const sid = (input.sessionId ?? input.session_id)?.trim()
  if (sid) {
    return formatSessionId(sid)
  }

  return 'Session'
}
