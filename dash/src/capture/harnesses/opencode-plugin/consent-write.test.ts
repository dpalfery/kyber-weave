// Write side of the consent file: atomic, unpredictable temp names, no clobber.

import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { createConsentIfAbsent, readConsentState, writeContentConsent } from './consent-write.js'

const dirs: string[] = []
const temp = (): string => {
  const dir = mkdtempSync(join(tmpdir(), 'kd-consent-write-'))
  dirs.push(dir)
  return dir
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

describe('writeContentConsent', () => {
  it('writes the file and leaves no temp file behind', () => {
    const dir = temp()
    const path = join(dir, 'sub', 'opencode-plugin.json')
    writeContentConsent(path, true)
    expect(readConsentState(path)).toBe('on')
    writeContentConsent(path, false)
    expect(readConsentState(path)).toBe('off')
    expect(readdirSync(join(dir, 'sub'))).toEqual(['opencode-plugin.json'])
  })

  it('does not use the predictable <path>.<pid>.tmp name, so a pre-planted file there is not written through', () => {
    const dir = temp()
    const path = join(dir, 'opencode-plugin.json')
    const decoy = `${path}.${process.pid}.tmp`
    writeFileSync(decoy, 'DECOY')
    writeContentConsent(path, true)
    expect(readFileSync(decoy, 'utf8')).toBe('DECOY')
    expect(readConsentState(path)).toBe('on')
  })

  it('cleans up its temp file when the rename fails (target is a directory)', () => {
    const dir = temp()
    const path = join(dir, 'opencode-plugin.json')
    mkdirSync(path)
    expect(() => writeContentConsent(path, true)).toThrow()
    expect(readdirSync(dir)).toEqual(['opencode-plugin.json'])
  })
})

describe('createConsentIfAbsent', () => {
  it('creates with content off when nothing is there, and leaves an existing file alone', () => {
    const dir = temp()
    const path = join(dir, 'opencode-plugin.json')
    expect(createConsentIfAbsent(path)).toBe('created')
    expect(readConsentState(path)).toBe('off')
    writeContentConsent(path, true)
    expect(createConsentIfAbsent(path)).toBe('present')
    expect(readConsentState(path)).toBe('on')
  })

  it('answers present, not a throw, when a directory sits at the path', () => {
    const dir = temp()
    const path = join(dir, 'opencode-plugin.json')
    mkdirSync(path)
    expect(createConsentIfAbsent(path)).toBe('present')
  })
})
