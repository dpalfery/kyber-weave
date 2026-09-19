import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs))
}

export function usd(n: number | undefined | null): string {
  if (n == null || !isFinite(n)) return '—'
  const v = n
  const sign = v < 0 ? '-' : ''
  const a = Math.abs(v)
  const s = a >= 1 || a === 0 ? a.toFixed(2) : a >= 0.01 ? a.toFixed(3) : a.toFixed(2)
  const [int, dec] = s.split('.')
  return sign + '$' + int!.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + (dec ? '.' + dec : '')
}

export function fmtTokens(n: number | undefined | null): string {
  if (n == null || !isFinite(n)) return '—'
  const v = n
  if (v >= 1e9) return (v / 1e9).toFixed(2) + 'B'
  if (v >= 1e6) return (v / 1e6).toFixed(1) + 'M'
  if (v >= 1e3) return (v / 1e3).toFixed(1) + 'K'
  return String(Math.round(v))
}

export function fmtNum(n: number | undefined | null): string {
  if (n == null || !isFinite(n)) return '—'
  const v = n
  return v.toLocaleString()
}

export function fmtMeasured(
  value: number | undefined | null,
  reason?: string,
): { text: string; measured: boolean; reason?: string } {
  if (value == null || !isFinite(value)) {
    return { text: '—', measured: false, reason }
  }

  return { text: fmtNum(value), measured: true }
}

export function compactUsd(n: number): string {
  if (!isFinite(n)) return '$0'
  const sign = n < 0 ? '-' : ''
  const a = Math.abs(n)
  if (a >= 1e6) return sign + '$' + (a / 1e6).toFixed(1) + 'M'
  if (a >= 1e3) return sign + '$' + (a / 1e3).toFixed(a >= 1e4 ? 0 : 1) + 'k'
  return sign + '$' + Math.round(a)
}

// Forest green -> gold -> terracotta ramp for stacked series. Referenced as CSS
// custom properties so the palette follows the active theme (light or dark).
export const CHART_COLORS = [
  'var(--chart-1)', 'var(--chart-2)', 'var(--chart-3)', 'var(--chart-4)', 'var(--chart-5)',
  'var(--chart-6)', 'var(--chart-7)', 'var(--chart-8)', 'var(--chart-9)', 'var(--chart-10)',
]

const MODEL_LABELS: Record<string, string> = {
  'claude-opus-4-8': 'Opus 4.8',
  'claude-opus-4-6': 'Opus 4.6',
  'claude-opus-4-7': 'Opus 4.7',
  'claude-sonnet-4-6': 'Sonnet 4.6',
  'claude-sonnet-4-5': 'Sonnet 4.5',
  'claude-haiku-4-5-20251001': 'Haiku 4.5',
  'grok-build-0.1': 'Grok Build',
  'cursor-auto': 'Cursor',
  'composer-2.5': 'Composer 2.5',
}

// Prettify a model id for chart legends. Display-name fields (current.topModels)
// already arrive clean; history rows carry raw ids, so we map the common ones
// and lightly clean the rest.
export function label(key: string): string {
  if (MODEL_LABELS[key]) return MODEL_LABELS[key]
  if (key === 'Other' || key === 'unknown') return key
  return key
    .replace(/^gpt-/i, 'GPT-')
    .replace(/-(\d{8,})$/, '')
    .replace(/-/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase())
}

/**
 * Shorten a run id for a dense table without collapsing distinct runs into one
 * label. Derived ids are `derived:<harness>:<discriminator>`, so a blind head
 * truncation renders every run on a harness page as the same string; the
 * discriminator is the only part that identifies the run. The redundant prefix
 * is dropped (the grouping-basis column already states `derived`) and anything
 * still over budget is elided in the middle, keeping both ends legible.
 *
 * Always pair with the full id in a `title` — this is a display form, not an id.
 */
export function shortRunId(runId: string, harness?: string): string {
  const prefix = harness ? `derived:${harness}:` : 'derived:'
  let rest = runId.startsWith(prefix) ? runId.slice(prefix.length) : runId
  if (harness === undefined && rest !== runId) {
    // Prefix known only as `derived:` — drop the harness segment too.
    const sep = rest.indexOf(':')
    if (sep !== -1) rest = rest.slice(sep + 1)
  }
  // File-sourced records carry a synthesized trace id, so a derived run over
  // them reads `derived:<harness>:synth:<provider>:<id>`. That second prefix is
  // shared by every run on the harness too, and hiding the discriminator behind
  // it is the same defect as the first.
  rest = rest.replace(/^synth:[^:]+:/, '')
  if (rest.length === 0) return runId
  if (rest.length <= 18) return rest
  return `${rest.slice(0, 9)}…${rest.slice(-8)}`
}

/**
 * Split an ISO timestamp into the date and time halves the run table stacks.
 * Invalid or absent input yields em dashes rather than a fabricated date.
 */
export function fmtRunTimestamp(iso: string | null | undefined): {
  date: string
  time: string
  full: string
} {
  if (!iso) return { date: '—', time: '', full: 'No start timestamp recorded' }
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return { date: '—', time: '', full: `Unparseable timestamp: ${iso}` }
  return {
    date: d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: '2-digit' }),
    time: d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' }),
    full: d.toISOString(),
  }
}

/**
 * The short label for a metric availability, which arrives either as a bare
 * string (`'measured'`, `'derived'`) or as a `{ availability, reason }` object
 * when the source declared it unreportable. Rendering the object form straight
 * into JSX throws React error #31, so every read of one goes through here.
 */
export function availabilityLabel(value: unknown): string {
  if (typeof value === 'string') return value
  if (value !== null && typeof value === 'object' && 'availability' in value) {
    const availability = (value as { availability?: unknown }).availability
    if (typeof availability === 'string') return availability
  }
  return 'unknown'
}

/** The stated reason a metric is unreportable, when the source gave one. */
export function availabilityReason(value: unknown): string | undefined {
  if (value !== null && typeof value === 'object' && 'reason' in value) {
    const reason = (value as { reason?: unknown }).reason
    if (typeof reason === 'string' && reason.trim() !== '') return reason
  }
  return undefined
}
