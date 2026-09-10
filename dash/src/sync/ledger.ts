/**
 * codeburn sync — sent-ledger.
 *
 * Client-side deduplication: tracks which calls have been successfully pushed.
 * Push logic: window minus ledger = what to send.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync, unlinkSync, renameSync } from 'fs'
import { join, resolve } from 'path'
import { getCodeburnCacheDir } from '../cache-dir.js'

export interface LedgerEntry {
  key: string  // deduplicationKey
  ts: string   // call timestamp (for pruning)
}

const SIX_MONTHS_MS = 180 * 24 * 60 * 60 * 1000

function ledgerCacheDir(): string {
  return getCodeburnCacheDir()
}

function ledgerPath(): string {
  return join(ledgerCacheDir(), 'sync-ledger.json')
}

// Before the shared cache resolver existed, sync alone wrote beneath
// XDG_CACHE_HOME. Treat that location as a one-time migration source only;
// CODEBURN_CACHE_DIR (when non-empty) is authoritative and must never import
// from an unrelated XDG tree.
function legacyXdgLedgerPath(): string | null {
  if (process.env.CODEBURN_CACHE_DIR?.trim()) return null
  const xdg = process.env.XDG_CACHE_HOME
  if (!xdg?.trim()) return null
  const legacy = join(xdg, 'codeburn', 'sync-ledger.json')
  return resolve(legacy) === resolve(ledgerPath()) ? null : legacy
}

function readLedgerFile(path: string): LedgerEntry[] | null {
  try {
    const entries = JSON.parse(readFileSync(path, 'utf-8')) as unknown
    if (!Array.isArray(entries)) return null
    return entries.filter(
      (e): e is LedgerEntry => typeof e === 'object' && e !== null && typeof e.key === 'string'
    )
  } catch {
    return null
  }
}

export function readLedger(): LedgerEntry[] {
  const path = ledgerPath()
  const legacyPath = legacyXdgLedgerPath()
  const canonicalEntries = existsSync(path) ? readLedgerFile(path) : null
  if (!legacyPath || !existsSync(legacyPath)) return canonicalEntries ?? []
  const legacyEntries = readLedgerFile(legacyPath)
  if (!legacyEntries) return canonicalEntries ?? []

  // Canonical wins for duplicate keys, but retain every key that exists only
  // in the historical ledger so an upgrade cannot re-upload old calls.
  const merged = [...(canonicalEntries ?? [])]
  const keys = new Set(merged.map(entry => entry.key))
  for (const entry of legacyEntries) {
    if (keys.has(entry.key)) continue
    keys.add(entry.key)
    merged.push(entry)
  }

  // Publish the canonical copy before retiring the legacy source. If the
  // write fails, keep and return the old ledger so deduplication still works.
  try {
    writeLedger(merged)
    try { unlinkSync(legacyPath) } catch { /* canonical copy already wins */ }
  } catch {
    return merged
  }
  return merged
}

export function writeLedger(entries: LedgerEntry[]): void {
  const dir = ledgerCacheDir()
  mkdirSync(dir, { recursive: true })
  // Atomic write: a crash mid-write must not corrupt the ledger — a corrupt
  // ledger reads as empty and the next push re-sends the whole window.
  const path = ledgerPath()
  const tmp = `${path}.tmp`
  writeFileSync(tmp, JSON.stringify(entries))
  renameSync(tmp, path)
}

/** Append new entries after a successful push. Also prunes entries older than 6 months. */
export function appendToLedger(newEntries: LedgerEntry[]): void {
  const existing = readLedger()
  const cutoff = new Date(Date.now() - SIX_MONTHS_MS).toISOString()

  // Prune old + dedupe
  const keySet = new Set(existing.map(e => e.key))
  const pruned = existing.filter(e => !e.ts || e.ts > cutoff)

  for (const entry of newEntries) {
    if (!keySet.has(entry.key)) {
      pruned.push(entry)
      keySet.add(entry.key)
    }
  }

  writeLedger(pruned)
}

/** Get the set of already-sent deduplication keys for fast lookup. */
export function ledgerKeySet(): Set<string> {
  return new Set(readLedger().map(e => e.key))
}

function isMissingFileError(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'code' in error && error.code === 'ENOENT'
}

/** Clear every eligible ledger (for sync reset). Returns the number of unique
 * entries removed. This deliberately bypasses readLedger(): reset must delete
 * canonical and legacy files independently, never migrate one into the other. */
export function clearLedger(): number {
  const canonicalPath = ledgerPath()
  const legacyPath = legacyXdgLedgerPath()
  const targets = [canonicalPath, ...(legacyPath ? [legacyPath] : [])].map(path => ({
    path,
    entries: readLedgerFile(path) ?? [],
  }))
  const removedKeys = new Set<string>()
  let deletionError: unknown

  // Attempt every target even if one unlink fails. A retry then has only the
  // actual remainder to remove, while ENOENT is the idempotent success case.
  for (const target of targets) {
    try {
      unlinkSync(target.path)
      for (const entry of target.entries) removedKeys.add(entry.key)
    } catch (error) {
      if (!isMissingFileError(error) && deletionError === undefined) deletionError = error
    }
  }

  if (deletionError !== undefined) throw deletionError
  return removedKeys.size
}
