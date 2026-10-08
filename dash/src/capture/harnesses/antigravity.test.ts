import {
  closeSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { runCapture } from '../index.js'
import { antigravityHarness } from './antigravity.js'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-antigravity-capture-'))
  temporaryHomes.push(home)
  return home
}

/** Path read via a descriptor so it is not a stat-then-read on the same path. */
function readUtf8(path: string): string {
  const fd = openSync(path, 'r')
  try {
    return readFileSync(fd, 'utf8')
  } finally {
    closeSync(fd)
  }
}

function settingsPath(home: string): string {
  return join(home, '.gemini', 'antigravity-cli', 'settings.json')
}

function bridgeScriptPath(home: string): string {
  return join(home, '.gemini', 'antigravity-cli', 'statusline.py')
}

function installBridgeLayout(home: string): { settings: string; script: string } {
  const script = bridgeScriptPath(home)
  mkdirSync(dirname(script), { recursive: true })
  writeFileSync(
    script,
    '# synthetic user bridge\nOTEL_EXPORTER_OTLP_TRACES_ENDPOINT = "http://127.0.0.1:4318/v1/traces"\n',
  )
  const settings = settingsPath(home)
  writeFileSync(
    settings,
    JSON.stringify(
      {
        statusLine: {
          command: script,
        },
      },
      null,
      2,
    ),
  )
  return { settings, script }
}

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

describe('antigravity capture writer (P2.6, status-only bridge route)', () => {
  it.skipIf(process.platform === 'win32')('status reports the existing bridge route to port 4318', async () => {
    const home = makeHome()
    const { settings, script } = installBridgeLayout(home)

    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['antigravity'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    const combined = `${result.stdout}\n${result.stderr}`
    expect(combined).not.toMatch(/not yet supported/i)
    expect(combined).toContain(settings)
    expect(combined).toContain(script)
    expect(combined).toMatch(/4318/)
    expect(combined.toLowerCase()).toMatch(/bridge|statusline/)
  })

  it.skipIf(process.platform === 'win32')(
    'status prints the declaredContextWindow attribute snippet for the user to add',
    async () => {
      const home = makeHome()
      installBridgeLayout(home)

      const result = await runCapture('status', {
        homeDir: home,
        harnessIds: ['antigravity'],
        fetchHealth: KYBERDASH_LISTENER,
      })

      expect(result.exitCode).toBe(0)
      const combined = `${result.stdout}\n${result.stderr}`
      expect(combined).toContain('declaredContextWindow')
    },
  )

  it.skipIf(process.platform === 'win32')('enable and disable write nothing under a temporary HOME', async () => {
    const home = makeHome()
    const settings = installBridgeLayout(home).settings
    const before = readFileSync(settings, 'utf8')
    const mtimeBefore = statSync(settings).mtimeMs

    const enabled = await runCapture('enable', {
      homeDir: home,
      harnessIds: ['antigravity'],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(enabled.exitCode).toBe(0)
    expect(enabled.stdout).not.toMatch(/not yet supported/i)
    expect(readUtf8(settings)).toBe(before)
    expect(statSync(settings).mtimeMs).toBe(mtimeBefore)

    const disabled = await runCapture('disable', {
      homeDir: home,
      harnessIds: ['antigravity'],
      fetchHealth: KYBERDASH_LISTENER,
    })
    expect(disabled.exitCode).toBe(0)
    expect(disabled.stdout).not.toMatch(/not yet supported/i)
    expect(readUtf8(settings)).toBe(before)

    const receiptPath = join(home, '.kyberdash', 'capture-receipt.json')
    expect(existsSync(receiptPath)).toBe(false)
  })

  it('registers antigravity against the antigravity-cli settings path (T8 layout)', () => {
    expect(antigravityHarness.id).toBe('antigravity')
    expect(antigravityHarness.displayName).toBe('Antigravity')
    expect(antigravityHarness.resolvePath('/tmp/synthetic-home')).toBe(
      join('/tmp/synthetic-home', '.gemini', 'antigravity-cli', 'settings.json'),
    )
  })
})
