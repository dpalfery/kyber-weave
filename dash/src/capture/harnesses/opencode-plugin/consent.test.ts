// T2 (R1, Q1/Q2, plan §3.4): read side of the consent file
// `~/.kyberdash/opencode-plugin.json` = { "version": 1, "content": boolean }.
// Failure of any kind means off. Tests inject the path and the reader; they
// never touch the developer's home directory.

import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import {
  OPENCODE_CONSENT_FILE_NAME,
  readContentConsent,
  resolveConsentPath,
} from './consent.js'

const dirs: string[] = []
const temp = (): string => {
  const dir = mkdtempSync(join(tmpdir(), 'kd-consent-'))
  dirs.push(dir)
  return dir
}
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

describe('resolveConsentPath', () => {
  it('is <home>/.kyberdash/opencode-plugin.json', () => {
    expect(OPENCODE_CONSENT_FILE_NAME).toBe('opencode-plugin.json')
    expect(resolveConsentPath('/h/u')).toBe(join('/h/u', '.kyberdash', 'opencode-plugin.json'))
  })
})

describe('readContentConsent', () => {
  const fileWith = (body: string): string => {
    const home = temp()
    mkdirSync(join(home, '.kyberdash'), { recursive: true })
    const path = resolveConsentPath(home)
    writeFileSync(path, body)
    return path
  }

  it('is on only for version 1 with content === true', () => {
    expect(readContentConsent(fileWith('{"version":1,"content":true}'))).toBe(true)
    expect(readContentConsent(fileWith('{"version":1,"content":false}'))).toBe(false)
  })

  it.each([
    ['missing content', '{"version":1}'],
    ['string content', '{"version":1,"content":"true"}'],
    ['numeric content', '{"version":1,"content":1}'],
    ['unknown version', '{"version":2,"content":true}'],
    ['missing version', '{"content":true}'],
    ['not JSON', 'nope'],
    ['JSON null', 'null'],
    ['JSON array', '[true]'],
    ['empty file', ''],
  ])('is off for %s', (_label, body) => {
    expect(readContentConsent(fileWith(body))).toBe(false)
  })

  it('is off when the file does not exist', () => {
    expect(readContentConsent(resolveConsentPath(temp()))).toBe(false)
  })

  it('is off when the path is a directory (unreadable)', () => {
    expect(readContentConsent(temp())).toBe(false)
  })

  it('is off when the reader throws', () => {
    expect(
      readContentConsent('/anywhere', () => {
        throw new Error('EACCES')
      }),
    ).toBe(false)
  })

  it('is off for an oversized file without parsing it', () => {
    const huge = `{"version":1,"content":true,"pad":"${'x'.repeat(100_000)}"}`
    expect(readContentConsent('/anywhere', () => huge)).toBe(false)
  })
})
