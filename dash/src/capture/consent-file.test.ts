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

import { mkdirSync, mkdtempSync, openSync, closeSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

const made: string[] = []

afterEach(() => {
  for (const dir of made.splice(0)) rmSync(dir, { recursive: true, force: true })
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

  it('replaces the file in one step, so a reader never sees half a file', async () => {
    const { writeContentConsent } = await sharedWriter()
    const path = consentPathIn(tempDir())
    writeContentConsent(path, false)
    const before = readFileSync(path, 'utf8')
    const beforeInode = statSync(path).ino
    // A reader that already opened the file keeps reading the whole old file: the writer
    // replaces the file instead of truncating and rewriting it in place.
    const handle = openSync(path, 'r')
    try {
      writeContentConsent(path, true)
      expect(readFileSync(handle, 'utf8')).toBe(before)
      expect(statSync(path).ino, 'the file is replaced, not rewritten in place').not.toBe(beforeInode)
      expect(JSON.parse(readFileSync(path, 'utf8'))).toEqual({ version: 1, content: true })
    } finally {
      closeSync(handle)
    }
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
