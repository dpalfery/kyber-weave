// Write side of the KyberDash consent file (plan §3.4, Q1/Q2).
//
// Shared by the capture plugins: the OpenCode installer and the Factory installer write the
// same file shape, so they share this writer instead of forking it. The OpenCode plugin's own
// module is now a re-export of this one.
//
// Kept apart from the read-only consent modules on purpose: those are bundled into a plugin
// file and must stay read-only and tiny; the installers and the capture engine own the writes
// here. The file is `{ "version": 1, "content": <boolean> }`.
//
// The writer does not decide the value: it writes what the caller asks for, so content is on
// only because the caller asked for it.
//
// This module is a LEAF: it imports only `node:` built-ins, so neither plugin drags engine
// code into the small bounded process it runs in.

import { randomBytes } from 'node:crypto'
import { closeSync, fstatSync, fsyncSync, mkdirSync, openSync, readSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { dirname } from 'node:path'

/**
 * The cap on one consent file, in UTF-8 BYTES.
 *
 * The unit is bytes, not characters, because the two readers that decide whether the
 * prompt text is captured — the plugin's own consent reader and the canon gate's — bound
 * the file by bytes. A state reader that counted characters would call a multi-byte file
 * under the bound `on` while both of those readers called it off, and the status row would
 * then claim a capture that is not happening.
 */
const MAX_CONSENT_BYTES = 4096

/** absent: no file. on/off: a valid file. invalid: not our shape. unreadable: exists but cannot be read as a file. */
export type ConsentState = 'absent' | 'on' | 'off' | 'invalid' | 'unreadable'

/** Never throws, and never returns the file's text. */
export function readConsentState(path: string): ConsentState {
  let handle: number
  try {
    handle = openSync(path, 'r')
  } catch (error) {
    // The OPEN is the check: a path that names nothing at all is 'absent', and one that
    // cannot be opened for any other reason is 'unreadable'.
    return (error as NodeJS.ErrnoException | undefined)?.code === 'ENOENT' ? 'absent' : 'unreadable'
  }
  try {
    // The file is measured through the descriptor BEFORE its content is read, so a file
    // that is already over the bound is refused without being read at all; the read itself
    // is bounded too, so a file that grows after the measure is still never materialised.
    const stat = fstatSync(handle)
    if (!stat.isFile()) return 'unreadable'
    if (stat.size > MAX_CONSENT_BYTES) return 'invalid'
    const text = readBoundedText(handle, MAX_CONSENT_BYTES)
    return text === undefined ? 'invalid' : parseConsentText(text)
  } catch {
    return 'unreadable'
  } finally {
    closeSync(handle)
  }
}

/**
 * At most `max` bytes of the open file, as UTF-8 text, or undefined when it holds more.
 *
 * The READ itself is bounded: it asks for no more than `max + 1` bytes, so a file that grew
 * between the size check and the read is still never materialised past the bound.
 */
export function readBoundedText(handle: number, max: number): string | undefined {
  const buffer = Buffer.alloc(max + 1)
  let filled = 0
  while (filled < buffer.length) {
    const got = readSync(handle, buffer, filled, buffer.length - filled, null)
    if (got === 0) break
    filled += got
  }
  if (filled > max) return undefined
  return buffer.subarray(0, filled).toString('utf8')
}

/** The state the consent file's text names, or 'invalid' when the text is not our shape. */
function parseConsentText(text: string): ConsentState {
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
    // The errno code is read through the optional chain: a failure that is not an object
    // at all has no code, and reading one anyway raises a second failure over the first.
    const code = (error as NodeJS.ErrnoException | undefined)?.code
    if (code === 'EEXIST' || code === 'EISDIR') return 'present'
    throw error
  }
}
