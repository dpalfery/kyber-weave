// JSON and JSONC edits for harness capture config (T8, D3/D9/D11).
//
// JSON and JSONC go through `jsonc-parser`, which preserves comments and
// formatting: `modify` computes minimal text edits against the original
// document instead of reprinting it, so a fixture comment above the edited
// key survives `enable`. Dot-separated keys address nested objects
// (`otel.endpoint` writes `{ "otel": { "endpoint": ... } }`).

import { applyEdits, modify, parse } from 'jsonc-parser'

export type JsonPrior = { present: boolean; value?: unknown }

function keyPath(key: string): Array<string | number> {
  return key.split('.')
}

/**
 * Resolve the jsonc-parser path for a key against the current document. A
 * literal property wins over a dotted path (`{ "otel.endpoint": ... }`
 * addresses that property, not `{ "otel": { "endpoint": ... } }`); otherwise
 * dots address nested objects so writers can target nested exporter fields.
 */
function resolveKeyPath(parsed: unknown, key: string): Array<string | number> {
  if (isRecord(parsed) && key in parsed) return [key]
  return keyPath(key)
}

/** Read a resolved path out of already-parsed JSON. */
function getByPath(parsed: unknown, path: Array<string | number>): JsonPrior {
  let current: unknown = parsed
  for (const segment of path) {
    if (typeof segment === 'number') {
      if (!Array.isArray(current) || segment >= current.length) return { present: false }
      current = current[segment]
      continue
    }
    if (!isRecord(current) || !(segment in current)) return { present: false }
    current = current[segment]
  }
  return { present: true, value: current }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

/** Read one key out of already-parsed JSON. */
export function getJsonValue(parsed: unknown, key: string): JsonPrior {
  return getByPath(parsed, resolveKeyPath(parsed, key))
}

/** Read the presence and value of each key in a JSON/JSONC document. */
export function readJsonKeys(content: string, keys: readonly string[]): Record<string, JsonPrior> {
  const parsed: unknown = content.trim() === '' ? {} : parse(content)
  const result: Record<string, JsonPrior> = {}
  for (const key of keys) result[key] = getJsonValue(parsed, key)
  return result
}

export type JsonApplyResult = {
  content: string
  prior: Record<string, JsonPrior>
  changed: boolean
}

/**
 * Set each desired key, recording the prior value (or absence) per key for
 * the receipt. A missing or empty document starts from `{}`.
 */
export function applyJsonEdits(
  content: string,
  desired: Record<string, string>,
): JsonApplyResult {
  const base = content.trim() === '' ? '{}\n' : content
  const parsed: unknown = parse(base)
  const prior: Record<string, JsonPrior> = {}
  for (const key of Object.keys(desired)) prior[key] = getJsonValue(parsed, key)

  let next = base
  for (const [key, value] of Object.entries(desired)) {
    // Resolved against the evolving document so a literal property keeps
    // its literal addressing from the prior read above.
    const path = resolveKeyPath(parse(next), key)
    const edits = modify(next, path, value, {
      formattingOptions: { insertSpaces: true, tabSize: 2 },
    })
    next = applyEdits(next, edits)
  }
  return { content: next, prior, changed: next !== content }
}

export type JsonDrift = { key: string; written: unknown; current: unknown }

export type JsonRevertResult = {
  content: string
  drift: JsonDrift[]
  changed: boolean
}

/**
 * Restore each key to its prior value (D11): a key that was absent before
 * `enable` is removed, otherwise it is set back. When any recorded key no
 * longer holds the value KyberDash wrote, nothing is changed and the drift
 * is returned so the caller can refuse, print it, and exit non-zero. The
 * file is never restored wholesale.
 */
export function revertJsonEdits(
  content: string,
  records: Record<string, { written: unknown; prior: JsonPrior }>,
): JsonRevertResult {
  const parsed: unknown = content.trim() === '' ? {} : parse(content)
  const drift: JsonDrift[] = []
  for (const [key, record] of Object.entries(records)) {
    const current = getJsonValue(parsed, key)
    const same = JSON.stringify(current.present ? current.value : undefined) === JSON.stringify(record.written)
    if (!current.present || !same) {
      drift.push({
        key,
        written: record.written,
        current: current.present ? current.value : undefined,
      })
    }
  }
  if (drift.length > 0) return { content, drift, changed: false }

  let next = content
  for (const [key, record] of Object.entries(records)) {
    const value = record.prior.present ? record.prior.value : undefined
    const path = resolveKeyPath(parse(next), key)
    const edits = modify(next, path, value, {
      formattingOptions: { insertSpaces: true, tabSize: 2 },
    })
    next = applyEdits(next, edits)
  }
  return { content: next, drift, changed: next !== content }
}
