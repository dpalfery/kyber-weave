import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { runCaptureAction } from './actions.js'
import { copilotHarness } from './harnesses/copilot.js'
import { opencodeHarness } from './harnesses/opencode.js'
import type { PathEnv } from './paths.js'
import { receiptPathForHome } from './receipt.js'

const homes: string[] = []
const locked: string[] = []

afterEach(() => {
  for (const dir of locked.splice(0)) chmodSync(dir, 0o755)
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-receipt-safety-'))
  homes.push(home)
  return home
}

const HOST = process.platform
const env = (home: string): PathEnv => ({ platform: HOST, appData: join(home, 'AppData', 'Roaming') })
const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED (synthetic)')
}
const req = (home: string, harnessId: string, action: 'enable' | 'disable') => ({
  action,
  harnessId,
  dryRun: false,
  homeDir: home,
  platform: HOST,
  appDataDir: env(home).appData,
  fetchHealth: NO_LISTENER,
})
const copilotFile = (home: string, id: string): string =>
  copilotHarness.configFiles!.find((file) => file.fileId === id)!.resolvePath(home, env(home))

describe('receipt safety', () => {
  it.skipIf(process.platform === 'win32' || process.getuid?.() === 0)(
    'records the receipt for files already written when a later file fails to write',
    async () => {
      const home = makeHome()
      const stable = copilotFile(home, 'vscode-stable')
      const insiders = copilotFile(home, 'vscode-insiders')
      mkdirSync(dirname(stable), { recursive: true })
      writeFileSync(stable, '{}\n')
      mkdirSync(dirname(insiders), { recursive: true })
      chmodSync(dirname(insiders), 0o555)
      locked.push(dirname(insiders))

      const result = await runCaptureAction(req(home, 'copilot', 'enable'))
      expect(result.outcome).toBe('refused')
      expect(readFileSync(stable, 'utf8')).not.toBe('{}\n')

      const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as {
        files: Array<{ path: string }>
      }
      expect(receipt.files.map((file) => file.path)).toContain(stable)

      chmodSync(dirname(insiders), 0o755)
      const disabled = await runCaptureAction(req(home, 'copilot', 'disable'))
      expect(disabled.outcome).toBe('wrote')
      expect(JSON.parse(readFileSync(stable, 'utf8'))).toEqual({})
    },
  )

  it('refuses to enable over a corrupt receipt and leaves it and the config untouched', async () => {
    const home = makeHome()
    const path = opencodeHarness.resolvePath(home, env(home))
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, '{}\n')
    mkdirSync(dirname(receiptPathForHome(home)), { recursive: true })
    writeFileSync(receiptPathForHome(home), '{ not json')

    const result = await runCaptureAction(req(home, 'opencode', 'enable'))
    expect(result.outcome).toBe('refused')
    expect(result.reason).toBe('unparseable')
    expect(readFileSync(receiptPathForHome(home), 'utf8')).toBe('{ not json')
    expect(readFileSync(path, 'utf8')).toBe('{}\n')
  })

  it('writes the receipt atomically, leaving no temp files behind', async () => {
    const home = makeHome()
    const path = opencodeHarness.resolvePath(home, env(home))
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, '{}\n')
    await runCaptureAction(req(home, 'opencode', 'enable'))
    expect(existsSync(receiptPathForHome(home))).toBe(true)
    expect(readdirSync(dirname(receiptPathForHome(home)))).toEqual(['capture-receipt.json'])
  })
})
