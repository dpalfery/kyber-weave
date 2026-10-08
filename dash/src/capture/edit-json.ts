// JSON and JSONC edits for harness capture config (T8, D3/D9/D11).
//
// JSON and JSONC go through `jsonc-parser`, which preserves comments and
// formatting: `modify` computes minimal text edits against the original
// document instead of reprinting it, so a fixture comment above the edited
// key survives `enable`. Dot-separated keys address nested objects when no
// existing property name consumes those dots (`otel.endpoint` writes
// `{ "otel": { "endpoint": ... } }`). A property that already exists and whose
// name contains dots stays one segment, so `vendor.tool.otel.enabled` updates
// that object's `enabled` child instead of a parallel `vendor`/`tool`/`otel` tree.

import { applyEdits, modify, parse } from 'jsonc-parser'

/**
 * A JSON/JSONC value `enable` may write. The JSON type is the value: a
 * boolean must stay a boolean, not the string `"true"`.
 */
export type JsonScalar = string | boolean | number | null

export type JsonPrior = { present: boolean; value?: unknown }

/**
 * Resolve the jsonc-parser path for a key against the current document.
 *
 * An existing property whose name contains dots is one segment
 * (`{ "otel.endpoint": ... }` addresses that property, not
 * `{ "otel": { "endpoint": ... } }`). The same rule applies to a prefix:
 * `vendor.tool.otel.enabled` must update `enabled` on an existing
 * `"vendor.tool.otel"` object. Splitting every dot would create a parallel
 * nested tree and a later read of `protocol` would miss the value already
 * stored there. The longest existing name wins so a shorter prefix such as
 * `vendor` cannot hide that property. A key that matches nothing is still
 * split, so writers can create nested exporter fields.
 */
function resolveKeyPath(parsed: unknown, key: string): Array<string | number> {
  const segments = key.split('.')
  const path: Array<string | number> = []
  let current: unknown = parsed
  let index = 0
  while (index < segments.length) {
    if (!isRecord(current)) {
      path.push(...segments.slice(index))
      break
    }
    const matched = longestExistingProperty(current, segments, index)
    if (matched === 0) {
      path.push(...segments.slice(index))
      break
    }
    const name = segments.slice(index, index + matched).join('.')
    path.push(name)
    current = current[name]
    index += matched
  }
  return path
}

/**
 * How many leading segments from `start` already exist as one property.
 * Longest first: `"vendor.tool.otel"` must beat a nested `vendor` object
 * when both are present, or enable writes the wrong tree.
 */
function longestExistingProperty(
  record: Record<string, unknown>,
  segments: readonly string[],
  start: number,
): number {
  for (let length = segments.length - start; length >= 1; length--) {
    const name = segments.slice(start, start + length).join('.')
    if (Object.hasOwn(record, name)) return length
  }
  return 0
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
 * the receipt. A missing or empty document starts from `{}`. Scalars are
 * passed through so jsonc-parser emits a JSON literal; stringifying first
 * would store `"true"` or `"0"` and the receipt would not match the file.
 */
export function applyJsonEdits(
  content: string,
  desired: Record<string, JsonScalar>,
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
  // Paths whose leaf was absent before enable. Removing the leaf leaves the
  // objects a dotted path created (`otel: {}`), which are not the owner's
  // bytes; those empty ancestors are pruned after every leaf is gone so a
  // shared parent is not removed while a sibling key is still in it.
  const removedPaths: Array<Array<string | number>> = []
  for (const [key, record] of Object.entries(records)) {
    const value = record.prior.present ? record.prior.value : undefined
    const path = resolveKeyPath(parse(next), key)
    const edits = modify(next, path, value, {
      formattingOptions: { insertSpaces: true, tabSize: 2 },
    })
    next = applyEdits(next, edits)
    if (!record.prior.present) removedPaths.push(path)
  }
  next = pruneEmptyAncestors(next, removedPaths)
  return { content: next, drift, changed: next !== content }
}

/**
 * Drop empty objects left behind by removing a dotted key. Stops at the
 * first ancestor that still has a property, so a pre-existing sibling is kept.
 */
function pruneEmptyAncestors(
  content: string,
  paths: readonly (readonly (string | number)[])[],
): string {
  let next = content
  for (const path of paths) {
    for (let length = path.length - 1; length >= 1; length--) {
      const parentPath = path.slice(0, length)
      const parent = getByPath(parse(next), parentPath)
      if (!parent.present || !isRecord(parent.value) || Object.keys(parent.value).length > 0) break
      const edits = modify(next, parentPath, undefined, {
        formattingOptions: { insertSpaces: true, tabSize: 2 },
      })
      next = applyEdits(next, edits)
    }
  }
  return next
}
