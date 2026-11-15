import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { readJsonKeys, type JsonScalar } from './edit-json.js'
import type { CaptureHarnessWriter } from './harnesses/registry.js'
import { runCapture } from './index.js'
import { loadCaptureReceipt } from './receipt.js'

/**
 * Target declaration: always-written keys and ensure-if-absent keys on one
 * JSON file. Cast until the capture core reads `ensureIfAbsentKeys`. On the
 * P2.W tip the core only has `desiredKeys`, and `enableFile` applies and
 * receipts that whole map.
 */
type EnsureIfAbsentFile = {
  fileId: string
  format: 'json'
  resolvePath: (home: string) => string
  desiredKeys: (endpoint: string) => Record<string, JsonScalar>
  ensureIfAbsentKeys: (endpoint: string) => Record<string, JsonScalar>
}

const HARNESS_ID = 'synthetic-ensure'

const ALWAYS_KEY = 'syntheticAlwaysWritten'
const PRESENT_ENSURE_KEY = 'syntheticEnsurePresent'
const ABSENT_ENSURE_KEY = 'syntheticEnsureAbsent'

const ALWAYS_PRIOR = 'owner-prior'
const ALWAYS_WRITTEN = 'kyber-written'
const PRESENT_ORIGINAL = 'owner-kept'
const PRESENT_CANDIDATE = 'must-not-replace'
const ABSENT_WRITTEN = 'inserted-when-missing'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-writer-ensure-'))
  temporaryHomes.push(home)
  return home
}

function configPath(home: string): string {
  return join(home, '.synthetic-capture', 'ensure.json')
}

function writeJson(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

function ensureFile(): EnsureIfAbsentFile {
  return {
    fileId: 'primary',
    format: 'json',
    resolvePath: configPath,
    desiredKeys: (_endpoint) => ({
      [ALWAYS_KEY]: ALWAYS_WRITTEN,
    }),
    ensureIfAbsentKeys: (_endpoint) => ({
      [PRESENT_ENSURE_KEY]: PRESENT_CANDIDATE,
      [ABSENT_ENSURE_KEY]: ABSENT_WRITTEN,
    }),
  }
}

function ensureHarness(file: EnsureIfAbsentFile): CaptureHarnessWriter {
  return {
    kind: 'managed',
    id: HARNESS_ID,
    displayName: 'Synthetic Ensure Harness',
    resolvePath: file.resolvePath,
    format: file.format,
    desiredKeys: file.desiredKeys,
    ensureIfAbsentKeys: file.ensureIfAbsentKeys,
    configFiles: [file],
  } as CaptureHarnessWriter
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

describe('capture writer ensure-if-absent seam (P2.W2)', () => {
  it.skipIf(process.platform === 'win32')(
    'a present ensure key stays unreceipted, an absent one is written, and disable restores only written keys',
    async () => {
      const home = makeHome()
      const path = configPath(home)
      const initial = `{
  "${ALWAYS_KEY}": "${ALWAYS_PRIOR}",
  "${PRESENT_ENSURE_KEY}": "${PRESENT_ORIGINAL}"
}
`
      writeJson(path, initial)
      const file = ensureFile()
      const writer = ensureHarness(file)

      const enabled = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)

      const written = readFileSync(path, 'utf8')
      const afterEnable = readJsonKeys(written, [ALWAYS_KEY, PRESENT_ENSURE_KEY, ABSENT_ENSURE_KEY])
      expect(afterEnable[ALWAYS_KEY]).toEqual({ present: true, value: ALWAYS_WRITTEN })
      expect(afterEnable[PRESENT_ENSURE_KEY]).toEqual({ present: true, value: PRESENT_ORIGINAL })
      expect(afterEnable[ABSENT_ENSURE_KEY]).toEqual({ present: true, value: ABSENT_WRITTEN })

      const receipt = loadCaptureReceipt(home)
      expect(receipt).not.toBeNull()
      const receiptFile = receipt?.files.find((entry) => entry.path === path)
      expect(receiptFile).toBeDefined()
      expect(receiptFile?.keys[ALWAYS_KEY]?.written).toBe(ALWAYS_WRITTEN)
      expect(receiptFile?.keys[ALWAYS_KEY]?.prior).toEqual({ present: true, value: ALWAYS_PRIOR })
      expect(receiptFile?.keys[ABSENT_ENSURE_KEY]?.written).toBe(ABSENT_WRITTEN)
      expect(receiptFile?.keys[ABSENT_ENSURE_KEY]?.prior).toEqual({ present: false })
      expect(receiptFile?.keys).not.toHaveProperty(PRESENT_ENSURE_KEY)

      const disabled = await runCapture('disable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)

      const afterDisable = readJsonKeys(readFileSync(path, 'utf8'), [
        ALWAYS_KEY,
        PRESENT_ENSURE_KEY,
        ABSENT_ENSURE_KEY,
      ])
      expect(afterDisable[ALWAYS_KEY]).toEqual({ present: true, value: ALWAYS_PRIOR })
      expect(afterDisable[PRESENT_ENSURE_KEY]).toEqual({ present: true, value: PRESENT_ORIGINAL })
      expect(afterDisable[ABSENT_ENSURE_KEY]).toEqual({ present: false })
    },
  )
})
