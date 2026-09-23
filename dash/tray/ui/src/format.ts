/**
 * Rendering rules shared by every panel.
 *
 * Requirement 14.1 is the one that matters most here: an absent figure renders
 * as `— (reason)` and never as `0`. `formatMeasured` is the engine's own
 * function rather than a copy, so the popover and the CLI renderers cannot
 * disagree about what an unmeasurable figure looks like.
 */

import { formatMeasured, NOT_MEASURABLE } from '../../../src/analysis/report/types.ts'
import type { BucketKey, Measured } from '../../../src/analysis/report/types.ts'

export { formatMeasured, NOT_MEASURABLE }

/** Thousands separators, so a six-figure token count is readable at a glance. */
export function formatTokens(value: number): string {
  return value.toLocaleString('en-US')
}

/** A measured token figure, or `— (reason)`. */
export function tokens(measure: Measured<number>): string {
  return formatMeasured(measure, formatTokens)
}

/** A measured fraction as a whole percentage, or `— (reason)`. */
export function percent(measure: Measured<number>): string {
  return formatMeasured(measure, (value) => `${Math.round(value * 100)}%`)
}

/**
 * The five canonical buckets in the order the composition bar stacks them,
 * largest structural cost first, so the bar reads the same way every time.
 */
export const BUCKET_ORDER: readonly BucketKey[] = [
  'system_prompt',
  'tool_definitions',
  'instruction_context',
  'conversation_history',
  'tool_result_content',
]

export const BUCKET_LABELS: Record<BucketKey, string> = {
  system_prompt: 'System prompt',
  tool_definitions: 'Tool definitions',
  instruction_context: 'Instruction context',
  conversation_history: 'Conversation history',
  tool_result_content: 'Tool result content',
}

/** Coarse age, matching the status item's tooltip. */
export function formatAge(isoTimestamp: string | null, now: Date): string {
  if (isoTimestamp === null) return 'never'
  const at = Date.parse(isoTimestamp)
  if (Number.isNaN(at)) return 'unknown'
  const seconds = Math.floor((now.getTime() - at) / 1000)
  if (seconds < 0) return 'unknown'
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}
