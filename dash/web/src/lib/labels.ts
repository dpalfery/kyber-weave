import { label } from './utils.js'

const UUID_RE = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i
const SYNTH_PREFIX_RE = /^(?:synth|derived):[^:]+:/i
const SPAN_OP_RE = /\.(llm_request|llm_invoke|generate|completion)$/i
const UNSPECIFIED_MODEL_RE = /<(?:synthetic|unknown|unspecified)>|(?::|<)(?:synthetic|unknown)(?:>|$)/i

const RATIO_DIMENSIONS = new Set([
  'contextHygiene',
  'cacheEfficiency',
  'delegationOverhead',
  'continuity',
])

/** True when a stored label is an identity or span op, not a human title. */
export function isMachineLabel(value: string | null | undefined): boolean {
  const v = value?.trim() ?? ''
  if (v === '') return true
  if (/^(?:synth|derived):/i.test(v)) return true
  if (SPAN_OP_RE.test(v)) return true
  if (UNSPECIFIED_MODEL_RE.test(v)) return true
  if (UUID_RE.test(v) && v.length > 36) return true
  return false
}

/**
 * Shorten a session / span / run identity without taking a blind 8-character
 * prefix — that is what turned `claude-desktop-…` into `claude-d`.
 */
export function formatSessionId(id: string | null | undefined): string {
  const trimmed = (id ?? '').trim()
  if (trimmed === '') return 'Unknown session'
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

function prettyProvider(raw: string): string {
  const cleaned = raw.replace(/^codeburn\//, '')
  return label(cleaned)
}

/**
 * Human model label. Synthetic / unknown placeholders and span-op names are
 * not model ids — say so rather than rendering `claude:<synthetic>`.
 */
export function formatModelLabel(model: string | null | undefined): string {
  const raw = model?.trim() ?? ''
  if (raw === '') return 'Unknown model'
  if (SPAN_OP_RE.test(raw)) return 'LLM request'
  if (UNSPECIFIED_MODEL_RE.test(raw)) {
    const provider = raw.split(/[:/]/)[0] ?? ''
    if (provider !== '' && !UNSPECIFIED_MODEL_RE.test(provider) && !provider.startsWith('<')) {
      return `${prettyProvider(provider)} (unspecified)`
    }
    return 'Unspecified model'
  }
  return label(raw)
}

/**
 * Timeline / request name. File-synthesized spans arrive as
 * `synth:<provider>:<uuid>:<uuid>` or `provider:<synthetic>` — those are
 * identities, not titles.
 */
export function formatSpanLabel(name: string | null | undefined): string {
  const raw = name?.trim() ?? ''
  if (raw === '') return 'unnamed'
  if (SPAN_OP_RE.test(raw)) return 'LLM request'
  if (/^(?:synth|derived):/i.test(raw)) return formatSessionId(raw)
  if (UNSPECIFIED_MODEL_RE.test(raw)) return formatModelLabel(raw)
  return raw
}

export function formatSessionTitle(input: {
  label?: string | null
  sessionId?: string
  session_id?: string
  agentName?: string | null
  agent_name?: string | null
  harness?: string
}): string {
  const title = input.label?.trim()
  if (title && !isMachineLabel(title)) return title
  const agent = (input.agentName ?? input.agent_name)?.trim()
  if (agent) return agent
  const harness = input.harness?.trim()
  if (harness) return `${prettyProvider(harness)} session`
  return formatSessionId(input.sessionId || input.session_id)
}

/** Ratio as a percent, including overflow past 100% (honest pressure). */
export function formatPercentRatio(value: number): string {
  if (!Number.isFinite(value)) return '—'
  return `${Math.round(value * 100)}%`
}

/** Bar fill 0–100. Overflow is shown in the label, not by clipping the number. */
export function barPercent(value: number): number {
  if (!Number.isFinite(value)) return 0
  return Math.min(100, Math.max(0, Math.round(value * 100)))
}

export function isRatioDimension(key: string | undefined): boolean {
  return key !== undefined && RATIO_DIMENSIONS.has(key)
}

/**
 * Duration for display. A stored 0 is the synthesizer's "none recorded"
 * default, not a measured instant — render the gap, never `0ms`.
 */
export function formatDuration(ms?: number | null): string {
  if (ms == null || !Number.isFinite(ms) || ms <= 0) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const minutes = Math.floor(ms / 60000)
  const seconds = Math.round((ms % 60000) / 1000)
  return `${minutes}m ${seconds}s`
}
