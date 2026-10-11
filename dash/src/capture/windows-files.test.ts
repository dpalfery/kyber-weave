// Windows-authored harness configs (T2): files saved on Windows commonly
// carry a UTF-8 BOM and CRLF line endings. Both are part of the owner's
// bytes: `enable` must keep the BOM, never mix line endings, and `disable`
// must restore the exact prior bytes of the keys KyberDash wrote. A BOM is
// an encoding marker, not a parse error. Every fixture lives under a
// temporary HOME; a real harness config is never touched.

import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { applyJsonEdits, readJsonKeys, revertJsonEdits } from './edit-json.js'
import { runCapture } from './index.js'
import type { CaptureHarnessWriter } from './harnesses/registry.js'

/** EF BB BF as it survives a utf8 round-trip: one U+FEFF character. */
const UTF8_BOM = '\uFEFF'
const UTF8_BOM_BYTES = Buffer.from([0xef, 0xbb, 0xbf])

const ENDPOINT = 'http://127.0.0.1:4318'

const MANAGED_ENDPOINT_KEY = 'synthetic.win.endpoint'
const MANAGED_ENABLED_KEY = 'synthetic.win.enabled'

/** VS Code–style literal dotted property the owner already had. */
const OWNER_KEY = 'editor.tabSize'

/** Synthetic JSONC fixture: BOM, CRLF, a comment, and one owner key. */
const JSONC_INITIAL =
  UTF8_BOM +
  '{\r\n' +
  '  // synthetic windows JSONC fixture\r\n' +
  `  "${OWNER_KEY}": 2\r\n` +
  '}\r\n'

/** Synthetic plain-JSON fixture (claude-code settings.json shape): BOM + CRLF. */
const JSON_INITIAL = UTF8_BOM + '{\r\n  "theme": "dark"\r\n}\r\n'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-windows-files-'))
  temporaryHomes.push(home)
  return home
}

/** Every line break is CRLF: an inserted LF would corrupt the owner's file. */
function expectCrlfOnly(text: string): void {
  expect(text).toContain('\r\n')
  expect(text.replace(/\r\n/g, '')).not.toMatch(/[\r\n]/)
}

function seedWindowsFile(path: string, content: string): Buffer {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
  return readFileSync(path)
}

function windowsHarnessWriter(
  id: string,
  format: 'json' | 'jsonc',
  resolvePath: (home: string) => string,
): CaptureHarnessWriter {
  return {
    kind: 'managed',
    id,
    displayName: 'Synthetic Windows-authored harness',
    resolvePath,
    format,
    desiredKeys: (endpoint) => ({
      [MANAGED_ENDPOINT_KEY]: endpoint,
      [MANAGED_ENABLED_KEY]: true,
    }),
  }
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

describe('edit-json with Windows-authored documents (BOM + CRLF)', () => {
  it('reads keys from a BOM document instead of rejecting it as unparseable', () => {
    const found = readJsonKeys(JSONC_INITIAL, [OWNER_KEY, MANAGED_ENDPOINT_KEY])

    expect(found[OWNER_KEY]).toEqual({ present: true, value: 2 })
    expect(found[MANAGED_ENDPOINT_KEY]).toEqual({ present: false })
  })

  it('enable edits preserve the BOM, stay CRLF-only, and are not refused', () => {
    const applied = applyJsonEdits(JSONC_INITIAL, {
      [MANAGED_ENDPOINT_KEY]: ENDPOINT,
      [MANAGED_ENABLED_KEY]: true,
    })

    expect(applied.refused).toBeUndefined()
    expect(applied.changed).toBe(true)
    expect(applied.content.startsWith(UTF8_BOM)).toBe(true)
    expectCrlfOnly(applied.content)
    expect(applied.content).toContain('// synthetic windows JSONC fixture')
    expect(applied.content).toContain(`"${OWNER_KEY}": 2`)
  })

  it('disable restores the exact prior bytes of a BOM + CRLF document', () => {
    const applied = applyJsonEdits(JSONC_INITIAL, {
      [MANAGED_ENDPOINT_KEY]: ENDPOINT,
      [MANAGED_ENABLED_KEY]: true,
    })

    const reverted = revertJsonEdits(applied.content, {
      [MANAGED_ENDPOINT_KEY]: { written: ENDPOINT, prior: { present: false } },
      [MANAGED_ENABLED_KEY]: { written: true, prior: { present: false } },
    })

    expect(reverted.refused).toBeUndefined()
    expect(reverted.drift).toEqual([])
    expect(reverted.changed).toBe(true)
    expect(reverted.content).toBe(JSONC_INITIAL)
  })
})

describe('capture enable/disable round-trips Windows-authored files', () => {
  // runCapture refuses the win32 platform outright; these tests exercise
  // Windows-authored *files*, not the Windows platform.
  it.skipIf(process.platform === 'win32')(
    'JSONC with BOM and CRLF: enable keeps the BOM and CRLF, disable restores the prior bytes',
    async () => {
      const home = makeHome()
      const path = join(home, '.synthetic-capture', 'windows-settings.jsonc')
      const original = seedWindowsFile(path, JSONC_INITIAL)
      const writer = windowsHarnessWriter(
        'synthetic-windows-jsonc',
        'jsonc',
        (h) => join(h, '.synthetic-capture', 'windows-settings.jsonc'),
      )

      const enabled = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [writer.id],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)

      const afterEnable = readFileSync(path)
      expect(afterEnable.subarray(0, 3).equals(UTF8_BOM_BYTES)).toBe(true)
      const afterEnableText = afterEnable.toString('utf8')
      expectCrlfOnly(afterEnableText)
      expect(afterEnableText).toContain('// synthetic windows JSONC fixture')

      const found = readJsonKeys(afterEnableText, [MANAGED_ENDPOINT_KEY, MANAGED_ENABLED_KEY, OWNER_KEY])
      expect(found[MANAGED_ENDPOINT_KEY]).toEqual({ present: true, value: ENDPOINT })
      expect(found[MANAGED_ENABLED_KEY]).toEqual({ present: true, value: true })
      expect(found[OWNER_KEY]).toEqual({ present: true, value: 2 })

      const disabled = await runCapture('disable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [writer.id],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)
      expect(readFileSync(path).equals(original)).toBe(true)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'plain JSON with BOM and CRLF round-trips to its original bytes',
    async () => {
      const home = makeHome()
      const path = join(home, '.synthetic-capture', 'windows-settings.json')
      const original = seedWindowsFile(path, JSON_INITIAL)
      const writer = windowsHarnessWriter(
        'synthetic-windows-json',
        'json',
        (h) => join(h, '.synthetic-capture', 'windows-settings.json'),
      )

      const enabled = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [writer.id],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)

      const afterEnable = readFileSync(path)
      expect(afterEnable.subarray(0, 3).equals(UTF8_BOM_BYTES)).toBe(true)
      expectCrlfOnly(afterEnable.toString('utf8'))

      const found = readJsonKeys(afterEnable.toString('utf8'), [MANAGED_ENDPOINT_KEY, MANAGED_ENABLED_KEY, 'theme'])
      expect(found[MANAGED_ENDPOINT_KEY]).toEqual({ present: true, value: ENDPOINT })
      expect(found[MANAGED_ENABLED_KEY]).toEqual({ present: true, value: true })
      expect(found['theme']).toEqual({ present: true, value: 'dark' })

      const disabled = await runCapture('disable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [writer.id],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)
      expect(readFileSync(path).equals(original)).toBe(true)
    },
  )
})
