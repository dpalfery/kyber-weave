// Stable classification of host fs errors for capture (T2).
//
// The settings API reports why a harness toggle failed using a fixed reason
// vocabulary — "drift" | "conflict" | "unparseable" | "unreadable" | "busy" —
// and a raw Node exception must map onto it without leaking its message,
// which can embed the owner's absolute paths. errno codes are stable
// identifiers and safe to return; message text is not.

import { describe, expect, it } from 'vitest'

import { classifyFsError } from './errno.js'

/** Node fs failures are Error instances carrying a `code` property. */
type ErrnoError = Error & { code: string }

/** A realistic fs error: the code plus a message that names a (synthetic) path. */
function fsError(code: string, message: string): ErrnoError {
  return Object.assign(new Error(message), { code })
}

describe('classifyFsError (errno.ts)', () => {
  it('maps permission-class codes EACCES and EPERM to "unreadable"', () => {
    const eacces = fsError(
      'EACCES',
      "EACCES: permission denied, open 'C:\\Users\\synthetic\\.claude\\settings.json'",
    )
    expect(classifyFsError(eacces)).toEqual({ code: 'EACCES', reason: 'unreadable' })

    const eperm = fsError(
      'EPERM',
      "EPERM: operation not permitted, unlink 'C:\\Users\\synthetic\\.claude\\settings.json'",
    )
    expect(classifyFsError(eperm)).toEqual({ code: 'EPERM', reason: 'unreadable' })
  })

  it('maps EBUSY to "busy" — the file is held by another process or lock', () => {
    const ebusy = fsError(
      'EBUSY',
      "EBUSY: resource busy or locked, open 'C:\\Users\\synthetic\\.claude\\settings.json'",
    )
    expect(classifyFsError(ebusy)).toEqual({ code: 'EBUSY', reason: 'busy' })
  })

  it('maps ENOENT to "drift" — a path missing since enable is drift in the capture domain', () => {
    const enoent = fsError(
      'ENOENT',
      "ENOENT: no such file or directory, open 'C:\\Users\\synthetic\\.claude\\settings.json'",
    )
    expect(classifyFsError(enoent)).toEqual({ code: 'ENOENT', reason: 'drift' })
  })

  it('keeps an unrecognized errno code but falls back to a stable reason', () => {
    const eisdir = fsError('EISDIR', 'EISDIR: illegal operation on a directory, read')
    expect(classifyFsError(eisdir)).toEqual({ code: 'EISDIR', reason: 'unreadable' })
  })

  it('falls back to a stable classification for errors without an errno code', () => {
    expect(classifyFsError(new Error('synthetic failure without a code'))).toEqual({
      code: 'UNKNOWN',
      reason: 'unreadable',
    })
    expect(classifyFsError('thrown string, not an Error')).toEqual({
      code: 'UNKNOWN',
      reason: 'unreadable',
    })
    expect(classifyFsError(null)).toEqual({ code: 'UNKNOWN', reason: 'unreadable' })
  })

  it('never returns raw error message text', () => {
    const ownerPath = 'C:\\Users\\synthetic\\.claude\\settings.json'
    const classification = classifyFsError(
      fsError('EACCES', `EACCES: permission denied, open '${ownerPath}'`),
    )

    // The shape is exactly { code, reason }; no message field can exist.
    expect(Object.keys(classification).sort()).toEqual(['code', 'reason'])
    const serialized = JSON.stringify(classification)
    expect(serialized).not.toContain('permission denied')
    expect(serialized).not.toContain(ownerPath)
  })
})
