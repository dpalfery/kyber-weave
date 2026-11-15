import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { snapshotFile } from './testing.js'

import { readJsonKeys } from './edit-json.js'
import { runCapture } from './index.js'
import type { CaptureHarnessWriter } from './harnesses/registry.js'

/** JSON/JSONC scalar values the capture writer seam must preserve (P2.W). */
type JsonScalar = string | boolean | number | null

type ManagedConfigFile = {
  fileId: string
  format: 'jsonc'
  resolvePath: (home: string) => string
  desiredKeys: (endpoint: string) => Record<string, JsonScalar>
}

type StatusWarningDecl = {
  fileId: string
  key: string
  whenValue: JsonScalar
  warnText: string
}

/**
 * Target harness declaration shape: multiple managed files, typed desired keys,
 * declarative status warnings, and an optional process-env status snippet.
 * Cast to `CaptureHarnessWriter` until the capture core understands it.
 */
type WriterSeamDeclaration = {
  kind: 'managed'
  id: string
  displayName: string
  configFiles: ManagedConfigFile[]
  statusWarnings?: StatusWarningDecl[]
  statusEnvSnippet?: { label: string; envKeys: readonly string[] }
}

const SEAM_HARNESS_ID = 'synthetic-seam'

const MANAGED_STRING_KEY = 'synthetic.otel.endpoint'
const MANAGED_BOOL_KEY = 'synthetic.otel.enabled'
const MANAGED_ZERO_KEY = 'synthetic.otel.retryBudget'

const WARNING_KEY = 'synthetic.legacy.exportMode'

const SNIPPET_ENV_KEYS = ['SYNTH_CAPTURE_SNIPPET_ALPHA', 'SYNTH_CAPTURE_SNIPPET_BETA'] as const

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-writer-seam-'))
  temporaryHomes.push(home)
  return home
}

function writeJsonc(path: string, content: string): void {
  mkdirSync(dirname(path), { recursive: true })
  writeFileSync(path, content)
}

/**
 * Registers the full seam declaration on a T8-shaped writer shell. The core
 * currently reads only `resolvePath` and string `desiredKeys`; extra fields
 * carry the contract the P2.W implementation must honor.
 */
function seamHarnessWriter(declaration: WriterSeamDeclaration): CaptureHarnessWriter {
  const primary = declaration.configFiles[0]
  if (primary === undefined) throw new Error('seam fixture requires at least one config file')

  const stringDesiredKeys = (endpoint: string): Record<string, string> => {
    const typed = primary.desiredKeys(endpoint)
    const out: Record<string, string> = {}
    for (const [key, value] of Object.entries(typed)) out[key] = String(value)
    return out
  }

  return {
    kind: 'managed',
    id: declaration.id,
    displayName: declaration.displayName,
    resolvePath: primary.resolvePath,
    format: primary.format,
    desiredKeys: stringDesiredKeys,
    configFiles: declaration.configFiles,
    statusWarnings: declaration.statusWarnings,
    statusEnvSnippet: declaration.statusEnvSnippet,
  } as CaptureHarnessWriter
}

function primaryPath(home: string): string {
  return join(home, '.synthetic-capture', 'primary.jsonc')
}

function secondaryPath(home: string): string {
  return join(home, '.synthetic-capture', 'secondary.jsonc')
}

function seamDeclaration(): WriterSeamDeclaration {
  return {
    kind: 'managed',
    id: SEAM_HARNESS_ID,
    displayName: 'Synthetic Seam Harness',
    configFiles: [
      {
        fileId: 'primary',
        format: 'jsonc',
        resolvePath: primaryPath,
        desiredKeys: (endpoint) => ({
          [MANAGED_STRING_KEY]: endpoint,
          [MANAGED_BOOL_KEY]: true,
          [MANAGED_ZERO_KEY]: 0,
        }),
      },
      {
        fileId: 'secondary',
        format: 'jsonc',
        resolvePath: secondaryPath,
        desiredKeys: (endpoint) => ({
          [MANAGED_STRING_KEY]: endpoint,
          [MANAGED_BOOL_KEY]: false,
          [MANAGED_ZERO_KEY]: 0,
        }),
      },
    ],
    statusWarnings: [
      {
        fileId: 'primary',
        key: WARNING_KEY,
        whenValue: 'parallel',
        warnText: 'synthetic legacy export mode is parallel',
      },
    ],
    statusEnvSnippet: {
      label: 'Synthetic status snippet',
      envKeys: SNIPPET_ENV_KEYS,
    },
  }
}

function seedBothJsoncFixtures(home: string): {
  primaryInitial: string
  secondaryInitial: string
} {
  const primaryInitial = `{
  // synthetic primary JSONC fixture
  "${WARNING_KEY}": "parallel",
  "editor.tabSize": 2
}
`
  const secondaryInitial = `{
  // synthetic secondary JSONC fixture
  "theme.kind": "dark"
}
`
  writeJsonc(primaryPath(home), primaryInitial)
  writeJsonc(secondaryPath(home), secondaryInitial)
  return { primaryInitial, secondaryInitial }
}

function expectTypedManagedValues(content: string, endpoint: string, enabled: boolean): void {
  const found = readJsonKeys(content, [MANAGED_STRING_KEY, MANAGED_BOOL_KEY, MANAGED_ZERO_KEY])
  expect(found[MANAGED_STRING_KEY].present).toBe(true)
  expect(found[MANAGED_STRING_KEY].value).toBe(endpoint)
  expect(typeof found[MANAGED_STRING_KEY].value).toBe('string')

  expect(found[MANAGED_BOOL_KEY].present).toBe(true)
  expect(found[MANAGED_BOOL_KEY].value).toBe(enabled)
  expect(typeof found[MANAGED_BOOL_KEY].value).toBe('boolean')

  expect(found[MANAGED_ZERO_KEY].present).toBe(true)
  expect(found[MANAGED_ZERO_KEY].value).toBe(0)
  expect(typeof found[MANAGED_ZERO_KEY].value).toBe('number')
}

function expectWarningKeyUntouched(content: string, expected: string): void {
  const found = readJsonKeys(content, [WARNING_KEY])
  expect(found[WARNING_KEY].present).toBe(true)
  expect(found[WARNING_KEY].value).toBe(expected)
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

describe('capture writer declaration seam (P2.W)', () => {
  it('legacy single-file string declaration still satisfies D11 dry-run, receipt and restore', async () => {
    const home = makeHome()
    const initial = '{\n  "theme": "dark"\n}\n'
    const writer: CaptureHarnessWriter = {
      kind: 'managed',
      id: 'legacy-string-file',
      displayName: 'Legacy single-file harness',
      resolvePath: (h) => join(h, '.synthetic-capture', 'legacy.json'),
      format: 'json',
      desiredKeys: (endpoint) => ({ syntheticLegacyOtelEndpoint: endpoint }),
    }
    const configPath = writer.kind === 'managed' ? writer.resolvePath(home) : ''
    writeJsonc(configPath, initial)

    const dry = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      harnessIds: [writer.id],
      dryRun: true,
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(dry.exitCode).toBe(0)
    expect(readFileSync(configPath, 'utf8')).toBe(initial)

    const enabled = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      harnessIds: [writer.id],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(enabled.exitCode).toBe(0)
    const written = readFileSync(configPath, 'utf8')
    expect(written).toContain('http://127.0.0.1:4318')

    const noop = await runCapture('enable', {
      homeDir: home,
      writers: [writer],
      harnessIds: [writer.id],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(noop.exitCode).toBe(0)
    expect(noop.stdout).toMatch(/no-op/i)

    const disabled = await runCapture('disable', {
      homeDir: home,
      writers: [writer],
      harnessIds: [writer.id],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(disabled.exitCode).toBe(0)
    expect(readFileSync(configPath, 'utf8')).toBe(initial)
  })

  it.skipIf(process.platform === 'win32')(
    'multi-file declaration writes both JSONC files, preserves comments, and keeps JSON types',
    async () => {
      const home = makeHome()
      const { primaryInitial, secondaryInitial } = seedBothJsoncFixtures(home)
      const writer = seamHarnessWriter(seamDeclaration())

      const result = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(result.exitCode).toBe(0)

      const primaryWritten = readFileSync(primaryPath(home), 'utf8')
      const secondaryWritten = readFileSync(secondaryPath(home), 'utf8')

      expect(primaryWritten).toContain('// synthetic primary JSONC fixture')
      expect(secondaryWritten).toContain('// synthetic secondary JSONC fixture')
      expect(primaryWritten).not.toBe(primaryInitial)

      expectTypedManagedValues(primaryWritten, 'http://127.0.0.1:4318', true)

      expect(secondaryWritten).not.toBe(secondaryInitial)
      expectTypedManagedValues(secondaryWritten, 'http://127.0.0.1:4318', false)
      expectWarningKeyUntouched(primaryWritten, 'parallel')
      expect(readJsonKeys(secondaryWritten, [WARNING_KEY])[WARNING_KEY].present).toBe(false)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'multi-file D11 records each file, dry-run writes nothing, and disable restores byte-for-byte',
    async () => {
      const home = makeHome()
      const { primaryInitial, secondaryInitial } = seedBothJsoncFixtures(home)
      const writer = seamHarnessWriter(seamDeclaration())
      const endpoint = 'http://127.0.0.1:4318'

      const dry = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        dryRun: true,
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(dry.exitCode).toBe(0)
      expect(dry.stdout).toMatch(/dry-run/i)
      expect(readFileSync(primaryPath(home), 'utf8')).toBe(primaryInitial)
      expect(readFileSync(secondaryPath(home), 'utf8')).toBe(secondaryInitial)
      expect(existsSync(join(home, '.kyberdash', 'capture-receipt.json'))).toBe(false)

      const enabled = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)

      const receiptPath = join(home, '.kyberdash', 'capture-receipt.json')
      const receipt = JSON.parse(readFileSync(receiptPath, 'utf8')) as {
        files: Array<{ path: string; keys: Record<string, { written: unknown }> }>
      }
      const receiptPaths = receipt.files.map((file) => file.path).sort()
      expect(receiptPaths).toEqual([primaryPath(home), secondaryPath(home)].sort())
      for (const file of receipt.files) {
        expect(file.keys[MANAGED_STRING_KEY]?.written).toBe(endpoint)
        expect(typeof file.keys[MANAGED_BOOL_KEY]?.written).toBe('boolean')
        expect(file.keys[MANAGED_ZERO_KEY]?.written).toBe(0)
      }

      const secondEnable = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(secondEnable.exitCode).toBe(0)
      expect(secondEnable.stdout).toMatch(/no-op/i)

      const disabled = await runCapture('disable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).toBe(0)
      expect(readFileSync(primaryPath(home), 'utf8')).toBe(primaryInitial)
      expect(readFileSync(secondaryPath(home), 'utf8')).toBe(secondaryInitial)
    },
  )

  it.skipIf(process.platform === 'win32')(
    'multi-file D11 drift on one file refuses disable for that file only',
    async () => {
      const home = makeHome()
      const { secondaryInitial } = seedBothJsoncFixtures(home)
      const writer = seamHarnessWriter(seamDeclaration())

      const enabled = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)

      const primaryAfterEnable = readFileSync(primaryPath(home), 'utf8')
      const secondaryAfterEnable = readFileSync(secondaryPath(home), 'utf8')
      expect(secondaryAfterEnable).not.toBe(secondaryInitial)

      const driftedPrimary = primaryAfterEnable.replace(
        'http://127.0.0.1:4318',
        'http://192.0.2.44:4318',
      )
      writeFileSync(primaryPath(home), driftedPrimary)

      const disabled = await runCapture('disable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(disabled.exitCode).not.toBe(0)
      expect(readFileSync(primaryPath(home), 'utf8')).toBe(driftedPrimary)
      expect(readFileSync(secondaryPath(home), 'utf8')).toBe(secondaryAfterEnable)

      const combined = `${disabled.stdout}\n${disabled.stderr}`
      expect(combined).toContain(MANAGED_STRING_KEY)
      expect(combined).toContain('http://127.0.0.1:4318')
      expect(combined).toContain('http://192.0.2.44:4318')
    },
  )

  it.skipIf(process.platform === 'win32')(
    'status emits declared warnings for present config values without writing them on enable',
    async () => {
      const home = makeHome()
      const { primaryInitial } = seedBothJsoncFixtures(home)
      const writer = seamHarnessWriter(seamDeclaration())

      const status = await runCapture('status', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(status.exitCode).toBe(0)
      const statusCombined = `${status.stdout}\n${status.stderr}`.toLowerCase()
      expect(statusCombined).toMatch(/warn/)
      expect(statusCombined).toContain('parallel')

      const enabled = await runCapture('enable', {
        homeDir: home,
        writers: [writer],
        harnessIds: [SEAM_HARNESS_ID],
        fetchHealth: KYBERDASH_LISTENER,
      })
      expect(enabled.exitCode).toBe(0)
      expectWarningKeyUntouched(readFileSync(primaryPath(home), 'utf8'), 'parallel')
      expect(readFileSync(primaryPath(home), 'utf8')).not.toBe(primaryInitial)
    },
  )

  describe('optional process-env status snippet (no shell rc)', () => {
    const snippetValues: Record<(typeof SNIPPET_ENV_KEYS)[number], string> = {
      SYNTH_CAPTURE_SNIPPET_ALPHA: 'alpha-on',
      SYNTH_CAPTURE_SNIPPET_BETA: '42',
    }

    beforeEach(() => {
      for (const [key, value] of Object.entries(snippetValues)) vi.stubEnv(key, value)
    })

    it.skipIf(process.platform === 'win32')(
      'status prints injected env values and enable or disable never touch shell rc files',
      async () => {
        const home = makeHome()
        seedBothJsoncFixtures(home)
        const writer = seamHarnessWriter(seamDeclaration())
        const zshrc = join(home, '.zshrc')
        const bashrc = join(home, '.bashrc')
        const rcInitial = '# synthetic shell rc — must stay untouched\n'
        writeFileSync(zshrc, rcInitial)
        writeFileSync(bashrc, rcInitial)
        const zshrcBefore = snapshotFile(zshrc)
        const bashrcBefore = snapshotFile(bashrc)

        const status = await runCapture('status', {
          homeDir: home,
          writers: [writer],
          harnessIds: [SEAM_HARNESS_ID],
          fetchHealth: KYBERDASH_LISTENER,
        })
        expect(status.exitCode).toBe(0)
        const combined = `${status.stdout}\n${status.stderr}`
        expect(combined).toMatch(/synthetic status snippet/i)
        for (const key of SNIPPET_ENV_KEYS) {
          expect(combined).toContain(key)
          expect(combined).toContain(snippetValues[key])
        }

        const enabled = await runCapture('enable', {
          homeDir: home,
          writers: [writer],
          harnessIds: [SEAM_HARNESS_ID],
          fetchHealth: KYBERDASH_LISTENER,
        })
        expect(enabled.exitCode).toBe(0)

        const disabled = await runCapture('disable', {
          homeDir: home,
          writers: [writer],
          harnessIds: [SEAM_HARNESS_ID],
          fetchHealth: KYBERDASH_LISTENER,
        })
        expect(disabled.exitCode).toBe(0)

        const zshrcAfter = snapshotFile(zshrc)
        const bashrcAfter = snapshotFile(bashrc)
        expect(zshrcAfter.text).toBe(rcInitial)
        expect(bashrcAfter.text).toBe(rcInitial)
        expect(zshrcAfter.mtimeMs).toBe(zshrcBefore.mtimeMs)
        expect(bashrcAfter.mtimeMs).toBe(bashrcBefore.mtimeMs)
      },
    )
  })
})
