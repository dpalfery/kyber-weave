// Write side of the KyberDash OpenCode consent file (plan §3.4, Q1/Q2).
//
// Kept apart from `consent.ts` on purpose: that module is bundled into the plugin file and
// must stay read-only and tiny; the installer and the capture engine own the writes here.
// The file is `{ "version": 1, "content": <boolean> }`.

import { randomBytes } from 'node:crypto'
import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

const MAX_CONSENT_BYTES = 4096

/** absent: no file. on/off: a valid file. invalid: not our shape. unreadable: exists but cannot be read as a file. */
export type ConsentState = 'absent' | 'on' | 'off' | 'invalid' | 'unreadable'

/** Never throws, and never returns the file's text. */
export function readConsentState(path: string): ConsentState {
  if (!existsSync(path)) return 'absent'
  let text: string
  try {
    text = readFileSync(path, 'utf8')
  } catch {
    return 'unreadable'
  }
  if (text.length > MAX_CONSENT_BYTES) return 'invalid'
  try {
    const parsed: unknown = JSON.parse(text)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return 'invalid'
    const record = parsed as Record<string, unknown>
    if (record['version'] !== 1 || typeof record['content'] !== 'boolean') return 'invalid'
    return record['content'] ? 'on' : 'off'
  } catch {
    return 'invalid'
  }
}

function consentBody(content: boolean): string {
  return `${JSON.stringify({ version: 1, content }, null, 2)}\n`
}

/**
 * Write `data` to `path` atomically: an unpredictable sibling temp file opened with `wx` (never
 * follows a pre-planted name), flushed to disk, then renamed over the target. A reader never
 * sees half a file, and a symlink at `path` is replaced rather than written through.
 */
export function writeFileAtomic(path: string, data: string): void {
  mkdirSync(dirname(path), { recursive: true })
  const temp = `${path}.${randomBytes(8).toString('hex')}.tmp`
  try {
    const fd = openSync(temp, 'wx', 0o644)
    try {
      writeFileSync(fd, data)
      fsyncSync(fd)
    } finally {
      closeSync(fd)
    }
    renameSync(temp, path)
  } catch (error) {
    rmSync(temp, { force: true })
    throw error
  }
}

/** Create or replace the file with `content`. Atomic: a reader never sees half a file. */
export function writeContentConsent(path: string, content: boolean): void {
  writeFileAtomic(path, consentBody(content))
}

/**
 * Create the file with content off only if nothing is at the path. Whatever is there
 * (either value, malformed, a directory) is left byte-for-byte alone.
 */
export function createConsentIfAbsent(path: string): 'created' | 'present' {
  mkdirSync(dirname(path), { recursive: true })
  try {
    writeFileSync(path, consentBody(false), { flag: 'wx' })
    return 'created'
  } catch (error) {
    const code = (error as NodeJS.ErrnoException).code
    if (code === 'EEXIST' || code === 'EISDIR') return 'present'
    throw error
  }
}
