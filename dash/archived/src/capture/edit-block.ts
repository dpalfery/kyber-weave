// Marker-delimited managed blocks for TOML and YAML harness config (T8).
//
// TOML and YAML get a KyberDash-owned block at the end of the file instead
// of an in-place key edit: neither format has a comment-preserving editor
// this small that both dialects share, and a delimited block keeps KyberDash
// bytes separable from the owner's — `disable` removes exactly the block it
// added. A conflicting key outside the block refuses with a snippet rather
// than writing a file two writers claim.

export const MANAGED_BLOCK_BEGIN = '# BEGIN KYBERDASH MANAGED BLOCK (do not edit between these markers)'
export const MANAGED_BLOCK_END = '# END KYBERDASH MANAGED BLOCK'
const MANAGED_BLOCK_HEADER = '# Managed by `kyberdash kyber capture`. Safe to delete the whole block.'

export type BlockPrior = { present: boolean; value?: unknown }

export type BlockFormat = 'toml' | 'yaml'

function renderEntry(key: string, value: string, format: BlockFormat): string {
  // JSON double-quoting is a valid basic string in TOML and a valid
  // double-quoted scalar in YAML, so one rendering serves both dialects.
  return format === 'toml' ? `${key} = ${JSON.stringify(value)}` : `${key}: ${JSON.stringify(value)}`
}

function unquote(value: string): string {
  const trimmed = value.trim().replace(/,$/, '').trim()
  if (trimmed.length >= 2 && trimmed.startsWith('"')) {
    try {
      return JSON.parse(trimmed) as string
    } catch {
      return trimmed
    }
  }
  if (trimmed.length >= 2 && trimmed.startsWith("'") && trimmed.endsWith("'")) {
    return trimmed.slice(1, -1)
  }
  return trimmed
}

function parseBlockEntries(block: string): Record<string, string> {
  const entries: Record<string, string> = {}
  for (const line of block.split('\n')) {
    const trimmed = line.trim()
    if (trimmed === '' || trimmed.startsWith('#')) continue
    const match = /^([^=:]+?)\s*[=:]\s*(.+)$/.exec(trimmed)
    if (match === null) continue
    entries[match[1]!.trim()] = unquote(match[2]!)
  }
  return entries
}

function findBlockBounds(content: string): { start: number; end: number } | null {
  const start = content.indexOf(MANAGED_BLOCK_BEGIN)
  if (start < 0) return null
  const endMarker = content.indexOf(MANAGED_BLOCK_END, start)
  if (endMarker < 0) return null
  const endOfLine = content.indexOf('\n', endMarker)
  return { start, end: endOfLine < 0 ? content.length : endOfLine + 1 }
}

/** Read the keys KyberDash manages inside the block (empty when no block). */
export function readBlockKeys(content: string): Record<string, string> {
  const bounds = findBlockBounds(content)
  if (bounds === null) return {}
  return parseBlockEntries(content.slice(bounds.start, bounds.end))
}

/** The file without its managed block: the owner's bytes, where conflicts live. */
function outsideBlock(content: string): string {
  const bounds = findBlockBounds(content)
  if (bounds === null) return content
  return content.slice(0, bounds.start) + content.slice(bounds.end)
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

export type BlockConflict = { key: string; snippet: string }

/**
 * Find the first desired key that already exists outside the managed block —
 * either as a `key =`/`key:` line or, for a dotted key, as a TOML table
 * header for its first segment. The snippet is the conflicting line with one
 * line of context on each side.
 */
export function findBlockConflict(content: string, desiredKeys: readonly string[]): BlockConflict | null {
  const owner = outsideBlock(content)
  const lines = owner.split('\n')
  for (const key of desiredKeys) {
    const firstSegment = key.split('.')[0]!
    const keyPattern = new RegExp(`^\\s*${escapeRegExp(key)}\\s*[=:]`)
    const leafPattern = new RegExp(`^\\s*${escapeRegExp(key.split('.').slice(-1)[0]!)}\\s*[=:]`)
    const tablePattern = new RegExp(`^\\s*\\[\\s*${escapeRegExp(firstSegment)}\\s*[\\].]`)
    for (let index = 0; index < lines.length; index++) {
      const line = lines[index]!
      if (keyPattern.test(line) || leafPattern.test(line) || tablePattern.test(line)) {
        const snippet = lines.slice(Math.max(0, index - 1), index + 2).join('\n')
        return { key, snippet }
      }
    }
  }
  return null
}

export type BlockApplyResult = {
  content: string
  prior: Record<string, BlockPrior>
  changed: boolean
  conflict?: BlockConflict
}

/** Append (or refresh) the managed block, refusing on an outside conflict. */
export function applyBlockEdits(
  content: string,
  desired: Record<string, string>,
  format: BlockFormat,
): BlockApplyResult {
  const current = readBlockKeys(content)
  const prior: Record<string, BlockPrior> = {}
  for (const key of Object.keys(desired)) {
    prior[key] = key in current ? { present: true, value: current[key] } : { present: false }
  }

  const upToDate = Object.entries(desired).every(([key, value]) => current[key] === value)
  if (upToDate && Object.keys(current).length === Object.keys(desired).length) {
    return { content, prior, changed: false }
  }

  const conflict = findBlockConflict(content, Object.keys(desired))
  if (conflict !== null) return { content, prior, changed: false, conflict }

  // A key inside the block that this call did not ask to write is a foreign
  // edit in KyberDash-owned bytes: rewriting the block would silently drop
  // it, so refuse with the block as the snippet instead.
  for (const key of Object.keys(current)) {
    if (!(key in desired)) {
      const bounds = findBlockBounds(content)
      const snippet =
        bounds === null ? '' : content.slice(bounds.start, bounds.end).trimEnd()
      return { content, prior, changed: false, conflict: { key, snippet } }
    }
  }

  const entries = Object.entries(desired)
    .map(([key, value]) => renderEntry(key, value, format))
    .join('\n')
  const block = `${MANAGED_BLOCK_BEGIN}\n${MANAGED_BLOCK_HEADER}\n${entries}\n${MANAGED_BLOCK_END}\n`

  const bounds = findBlockBounds(content)
  if (bounds !== null) {
    const next = content.slice(0, bounds.start) + block + content.slice(bounds.end)
    return { content: next, prior, changed: next !== content }
  }
  const normalized = content === '' || content.endsWith('\n') ? content : `${content}\n`
  const next = `${normalized}${block}`
  return { content: next, prior, changed: next !== content }
}

export type BlockDrift = { key: string; written: unknown; current: unknown }

export type BlockRevertResult = {
  content: string
  drift: BlockDrift[]
  changed: boolean
}

/**
 * Restore each recorded key (D11): absent-before keys leave the block, and an
 * emptied block is removed so the file returns to the owner's bytes. Any key
 * that no longer holds the written value is drift: nothing is changed.
 */
export function revertBlockEdits(
  content: string,
  records: Record<string, { written: unknown; prior: BlockPrior }>,
  format: BlockFormat,
): BlockRevertResult {
  const current = readBlockKeys(content)
  const drift: BlockDrift[] = []
  for (const [key, record] of Object.entries(records)) {
    const value = current[key]
    if (value === undefined || value !== String(record.written)) {
      drift.push({ key, written: record.written, current: value })
    }
  }
  if (drift.length > 0) return { content, drift, changed: false }

  const restored: Record<string, string> = {}
  for (const [key, record] of Object.entries(records)) {
    if (record.prior.present && record.prior.value !== undefined) {
      restored[key] = String(record.prior.value)
    }
  }
  // Keys the block holds that this receipt does not know are left alone only
  // when they were never ours: every block key came from a receipt record, so
  // anything unaccounted for is a foreign edit and therefore drift. The check
  // above already refused when a recorded key moved; an extra unknown key is
  // reported the same way rather than silently kept or dropped.
  for (const key of Object.keys(current)) {
    if (!(key in records)) {
      drift.push({ key, written: undefined, current: current[key] })
    }
  }
  if (drift.length > 0) return { content, drift, changed: false }

  const bounds = findBlockBounds(content)
  if (bounds === null) return { content, drift, changed: false }

  if (Object.keys(restored).length === 0) {
    const next = content.slice(0, bounds.start) + content.slice(bounds.end)
    return { content: next, drift, changed: next !== content }
  }
  const entries = Object.entries(restored)
    .map(([key, value]) => renderEntry(key, value, format))
    .join('\n')
  const block = `${MANAGED_BLOCK_BEGIN}\n${MANAGED_BLOCK_HEADER}\n${entries}\n${MANAGED_BLOCK_END}\n`
  const next = content.slice(0, bounds.start) + block + content.slice(bounds.end)
  return { content: next, drift, changed: next !== content }
}
