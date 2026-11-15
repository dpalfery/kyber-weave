// The shared consent file: the writer both capture plugins use for their consent file.
//
// The consent file is `{ "version": 1, "content": <boolean> }`. The writer is shared so the
// Factory plugin and the OpenCode plugin agree on the shape, on the atomic write and on the
// "create only if absent" rule. The shared module is written by the next task, so every test here
// fails until it exists; the module is loaded inside each test, so the RED proof is per test.
//
// The contract this file pins:
//
//   - writing with content off produces exactly `{ "version": 1, "content": false }`;
//   - writing is ATOMIC: the file is replaced in one step, so a reader never sees half a file and
//     a failed write leaves neither a partial file nor a temporary one behind;
//   - the writer does not decide: it writes the value it is given, so content on is possible only
//     because the caller asked for it;
//   - the OpenCode plugin's own consent writer becomes a thin re-export shim of the shared one and
//     keeps writing the same shape.
//
// The API the shared module must expose:
//
//   writeContentConsent(path, content): void
//   createConsentIfAbsent(path): 'created' | 'present'
//   readConsentState(path): 'absent' | 'on' | 'off' | 'invalid' | 'unreadable'
//   writeFileAtomic(path, data): void

import { mkdirSync, mkdtempSync, openSync, closeSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

const made: string[] = []

afterEach(() => {
  for (const dir of made.splice(0)) rmSync(dir, { recursive: true, force: true })
  vi.doUnmock('node:fs')
  vi.resetModules()
})

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-consent-'))
  made.push(dir)
  return dir
}

/** The consent file of a plugin, inside an injected directory. */
function consentPathIn(dir: string, name = 'factory-plugin.json'): string {
  return join(dir, '.kyberdash', name)
}

/** The shared consent writer, loaded from the shared module. */
async function sharedWriter(): Promise<{
  writeContentConsent: (path: string, content: boolean) => void
  createConsentIfAbsent: (path: string) => 'created' | 'present'
}> {
  const module = await import('./consent-file.js')
  const write = (module as { writeContentConsent?: unknown }).writeContentConsent
  const create = (module as { createConsentIfAbsent?: unknown }).createConsentIfAbsent
  if (typeof write !== 'function' || typeof create !== 'function') {
    throw new Error(
      'capture/consent-file.ts must export writeContentConsent(path, content) and createConsentIfAbsent(path)',
    )
  }
  return {
    writeContentConsent: write as (path: string, content: boolean) => void,
    createConsentIfAbsent: create as (path: string) => 'created' | 'present',
  }
}

describe('the shared consent writer', () => {
  it('writes exactly { version: 1, content: false } when content is off', async () => {
    const { writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    writeContentConsent(path, false)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: false })
  })

  it('writes exactly { version: 1, content: true } when the caller asks for content on', async () => {
    const { writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    writeContentConsent(path, true)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: true })
  })

  // POSIX-only: Windows forbids replacing a file that a reader holds open (the rename
  // fails with EPERM), so the guarantee this case asserts — a reader keeps the whole old
  // file while the writer replaces the file in one step — cannot be exercised there.
  it.skipIf(process.platform === 'win32')('a reader that already opened the file keeps reading the whole old file', async () => {
    const { writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    writeContentConsent(path, false)
    const before = readFileSync(path, 'utf8')
    // The reader opens the file BEFORE the write, so it holds the old file: the writer
    // replaces the file instead of truncating and rewriting it in place.
    const handle = openSync(path, 'r')
    try {
      writeContentConsent(path, true)
      expect(readFileSync(handle, 'utf8')).toBe(before)
    } finally {
      closeSync(handle)
    }
  })

  it('replaces the file rather than rewriting it in place', async () => {
    const { writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    writeContentConsent(path, false)
    const beforeInode = statSync(path).ino
    writeContentConsent(path, true)
    expect(statSync(path).ino, 'the file is replaced, not rewritten in place').not.toBe(beforeInode)
  })

  it('leaves the new content on disk', async () => {
    const { writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    writeContentConsent(path, false)
    writeContentConsent(path, true)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: true })
  })

  it('leaves no partial file and no temporary file behind when a write fails', async () => {
    const { writeContentConsent } = await sharedWriter()
    const root = tempDir()
    // The target is a directory, so the replace cannot land.
    const target = join(root, 'consent.json')
    mkdirSync(target)
    expect(() => writeContentConsent(target, true)).toThrow()
    expect(statSync(target).isDirectory(), 'the target is untouched').toBe(true)
    expect(readdirSync(root), 'no temporary file is left beside the target').toEqual(['consent.json'])
    expect(readdirSync(target), 'no temporary file is left inside the target').toEqual([])
  })

  it('creates the file with content off only when nothing is at the path', async () => {
    const { createConsentIfAbsent, writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    expect(createConsentIfAbsent(path)).toBe('created')
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: false })

    writeContentConsent(path, true)
    expect(createConsentIfAbsent(path), 'an existing file is left alone').toBe('present')
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: true })
  })
})

describe('the OpenCode consent writer is a thin re-export of the shared one', () => {
  it('re-exports the shared writer, so both plugins write the same file', async () => {
    const shared = await import('./consent-file.js')
    const shim = await import('./harnesses/opencode-plugin/consent-write.js')
    expect(shim.writeContentConsent, 'the OpenCode module must re-export the shared writer').toBe(
      shared.writeContentConsent,
    )
    expect(shim.createConsentIfAbsent).toBe(shared.createConsentIfAbsent)
  })

  it('still writes the same shape through the OpenCode module', async () => {
    const shim = await import('./harnesses/opencode-plugin/consent-write.js')
    const path = consentPathIn(tempDir(), 'opencode-plugin.json')
    shim.writeContentConsent(path, false)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: false })
    shim.writeContentConsent(path, true)
    expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: true })
  })
})

describe('the consent file is bounded by bytes, and every reader bounds it the same way', () => {
  /**
   * The valid on-shape, with multi-byte padding in a key the shape does not forbid: the
   * file is still the on-shape the readers accept, and it is over the 4096-BYTE bound
   * while it is under the 4096-character bound.
   */
  function paddedOnShape(): string {
    return `{"version":1,"content":true,"note":"${'中'.repeat(3000)}"}`
  }

  it('reads an oversized multi-byte consent file as off, the way the two content readers read it', async () => {
    const { readConsentState } = await import('./consent-file.js')
    const { readContentConsent: readPluginConsent } = await import(
      './harnesses/factory-plugin/consent.js'
    )
    const { readContentConsent: readCanonConsent } = await import('../canon/droid-content.js')
    const dir = tempDir()
    mkdirSync(join(dir, '.kyberdash'), { recursive: true })
    const path = consentPathIn(dir)
    const text = paddedOnShape()
    // The fixture is the one the disagreement needs: UNDER the character bound, OVER the
    // byte bound, and still the valid on-shape.
    expect(text.length, 'under the 4096-character bound').toBeLessThan(4096)
    expect(Buffer.byteLength(text, 'utf8'), 'over the 4096-byte bound').toBeGreaterThan(4096)
    writeFileSync(path, text)

    // The two content readers bound the file by UTF-8 bytes, so they read it as OFF and
    // publish no prompt text.
    expect(readPluginConsent(path), 'the plugin reader reads the oversized file as off').toBe(false)
    expect(readCanonConsent(path), 'the canon gate reads the oversized file as off').toBe(false)

    // The state reader must agree with them. It must not report 'on' for a file the two
    // readers will not act on — the Settings row and `capture status` would then claim a
    // capture that is not happening — and the state it already uses for a file it will
    // not accept is 'invalid'.
    expect(
      readConsentState(path),
      'the state reader must agree with the two content readers',
    ).toBe('invalid')
    expect(readConsentState(path)).not.toBe('on')
  })
})

// ──────────────────────────────────────────────────────────────────────────────────────────────
// The reader and the writer, at their edges

/** The shared consent module, as the tests load it. */
type ConsentModule = typeof import('./consent-file.js')

/**
 * The shared consent module, loaded with the file system's reads recorded.
 *
 * The case is about WHAT the reader reads: a file that is already over the bound must
 * not be read at all, so the bound holds before the content is materialised. `node:fs` is
 * replaced for the load, so every read the module makes is recorded, and the real module
 * is put back in the `afterEach`.
 */
async function loadWithRecordedReads(reportedSize?: number): Promise<{
  module: ConsentModule
  reads: string[]
  bytesRead: () => number
}> {
  // Every content read is recorded, whichever call makes it and whatever it is handed (a
  // path or a descriptor), so an assertion on `reads` cannot pass just because the read
  // went through a descriptor.
  const reads: string[] = []
  let bytes = 0
  vi.doMock('node:fs', async (importOriginal) => {
    const actual = (await importOriginal()) as Record<string, unknown>
    const realReadFile = actual['readFileSync'] as (...args: unknown[]) => unknown
    const realRead = actual['readSync'] as (...args: unknown[]) => number
    const realFstat = actual['fstatSync'] as (...args: unknown[]) => { size: number }
    return {
      ...actual,
      readFileSync: (...args: unknown[]) => {
        reads.push(`readFileSync ${String(args[0])}`)
        return realReadFile(...args)
      },
      readSync: (...args: unknown[]) => {
        reads.push(`readSync ${String(args[0])}`)
        const got = realRead(...args)
        bytes += got
        return got
      },
      // A file that grows between the measure and the read is simulated by reporting a
      // smaller size than the file holds.
      fstatSync: (...args: unknown[]) => {
        const stat = realFstat(...args)
        if (reportedSize !== undefined) Object.defineProperty(stat, 'size', { value: reportedSize })
        return stat
      },
    }
  })
  vi.resetModules()
  const module = await import('./consent-file.js')
  return { module, reads, bytesRead: () => bytes }
}

/**
 * The shared consent module, loaded with every write failing with `failure`.
 *
 * A write that fails with a value that is not an object is what a patched or wrapped
 * `node:fs` can do, and what the catch in the writer must survive: the failure it
 * existed to report is the one that has to reach the caller.
 */
async function loadWithFailingWrite(failure: unknown): Promise<ConsentModule> {
  vi.doMock('node:fs', async (importOriginal) => {
    const actual = (await importOriginal()) as Record<string, unknown>
    return {
      ...actual,
      writeFileSync: () => {
        throw failure
      },
    }
  })
  vi.resetModules()
  return import('./consent-file.js')
}

describe('the consent file is bounded before it is read', () => {
  it('does not read a consent file that is already over the byte bound', async () => {
    const dir = tempDir()
    const path = consentPathIn(dir)
    mkdirSync(dirname(path), { recursive: true })
    // A file over the bound: 64 KiB, sixteen times the 4096-byte bound. It is well
    // formed JSON, so only the bound can reject it.
    writeFileSync(path, `{"version":1,"content":true,"note":"${'x'.repeat(64 * 1024)}"}`)

    const { module, reads } = await loadWithRecordedReads()
    const state = module.readConsentState(path)

    expect(reads, 'a consent file that is already over the bound is never read').toEqual([])
    expect(state, 'the over-bound file is invalid').toBe('invalid')
  })

  it('never reads past the bound when the file grows after it was measured', async () => {
    const dir = tempDir()
    const path = consentPathIn(dir)
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, `{"version":1,"content":true,"note":"${'x'.repeat(64 * 1024)}"}`)

    // The measure says the file is small; the file on disk is sixteen times the bound.
    const { module, reads, bytesRead } = await loadWithRecordedReads(100)
    const state = module.readConsentState(path)

    expect(reads.some((read) => read.startsWith('readFileSync')), 'the file is never read whole').toBe(false)
    expect(bytesRead(), 'the read stops one byte past the bound').toBeLessThanOrEqual(4096 + 1)
    expect(state, 'a file that grew past the bound is invalid').toBe('invalid')
  })
})

describe('the consent writer reports the failure it caught', () => {
  it('propagates the original failure when the write throws a value that is not an object', async () => {
    const path = consentPathIn(tempDir())
    const module = await loadWithFailingWrite(null)

    let raised: unknown = 'nothing was raised'
    try {
      module.createConsentIfAbsent(path)
    } catch (error) {
      raised = error
    }

    expect(
      raised,
      'the failure is the one the write threw, not a TypeError about reading .code of it',
    ).not.toBeInstanceOf(TypeError)
    expect(raised, 'the original failure is what propagates').toBeNull()
  })
})
