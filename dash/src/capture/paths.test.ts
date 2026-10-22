// D14 — capture path resolution is platform-aware and testable on any host.
// Every harness `resolvePath` takes an optional path environment, so Windows
// resolution is pinned from any OS through path.win32 joining (the shared
// `pjoin` helper in paths.ts) instead of a real Windows machine. The darwin
// and linux strings are characterization: D14 changes only the seam, not
// those paths, and XDG_* stays ignored on every OS. Every resolution happens
// under a synthetic HOME; nothing reads the real one, and win32 checks on a
// non-Windows host stay pure string comparisons — status is read-only.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { antigravityHarness } from './harnesses/antigravity.js'
import { claudeCodeHarness } from './harnesses/claude-code.js'
import { codexHarness } from './harnesses/codex.js'
import { copilotHarness } from './harnesses/copilot.js'
import { opencodeHarness } from './harnesses/opencode.js'
import { piHarness } from './harnesses/pi.js'
import type { CaptureConfigFile } from './harnesses/registry.js'
import { pjoin, type PathEnv } from './paths.js'

const temporaryHomes: string[] = []

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
})

/** A fresh synthetic HOME; every path resolves beneath it, never the real one. */
function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-paths-'))
  temporaryHomes.push(home)
  return home
}

/**
 * One VS Code edition file copilot declares, by the fileIds the declaration
 * already uses. Insiders is reachable only through `configFiles` — the same
 * seam `status` and `enable` resolve through — never a second writer.
 */
function vscodeEditionFile(fileId: 'vscode-stable' | 'vscode-insiders'): CaptureConfigFile {
  const file = copilotHarness.configFiles?.find((candidate) => candidate.fileId === fileId)
  if (file === undefined) throw new Error(`copilot must declare its ${fileId} config file`)
  return file
}

describe('pjoin (D14 win32/posix-aware joining)', () => {
  it('joins with win32 separators on win32, normalizing a trailing separator', () => {
    expect(pjoin('win32', 'C:\\Users\\synthetic', '.claude', 'settings.json')).toBe(
      'C:\\Users\\synthetic\\.claude\\settings.json',
    )
    expect(pjoin('win32', 'C:\\Users\\synthetic\\', '.claude', 'settings.json')).toBe(
      'C:\\Users\\synthetic\\.claude\\settings.json',
    )
  })

  it('joins with posix separators on darwin and linux', () => {
    expect(
      pjoin('darwin', '/Users/synthetic', 'Library', 'Application Support', 'Code', 'User', 'settings.json'),
    ).toBe('/Users/synthetic/Library/Application Support/Code/User/settings.json')
    expect(pjoin('linux', '/home/synthetic', '.config', 'opencode', 'opencode.jsonc')).toBe(
      '/home/synthetic/.config/opencode/opencode.jsonc',
    )
  })
})

describe('claude-code config path (D14)', () => {
  it('resolves the win32 settings path with win32 separators', () => {
    const home = makeHome()
    const env: PathEnv = { platform: 'win32' }
    expect(claudeCodeHarness.resolvePath(home, env)).toBe(`${home}\\.claude\\settings.json`)
  })

  it('keeps the darwin and linux strings unchanged', () => {
    const home = makeHome()
    expect(claudeCodeHarness.resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/.claude/settings.json`,
    )
    expect(claudeCodeHarness.resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.claude/settings.json`,
    )
  })
})

describe('copilot VS Code user settings paths (D14)', () => {
  it('roots both editions at the injected application-data directory on win32', () => {
    const home = makeHome()
    const env: PathEnv = { platform: 'win32', appData: 'C:\\Injected\\AppData' }
    expect(copilotHarness.resolvePath(home, env)).toBe(
      'C:\\Injected\\AppData\\Code\\User\\settings.json',
    )
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, env)).toBe(
      'C:\\Injected\\AppData\\Code\\User\\settings.json',
    )
    expect(vscodeEditionFile('vscode-insiders').resolvePath(home, env)).toBe(
      'C:\\Injected\\AppData\\Code - Insiders\\User\\settings.json',
    )
  })

  it('prefers the injected appData over a set APPDATA environment variable', () => {
    const home = makeHome()
    process.env.APPDATA = 'C:\\From\\AppDataEnv'
    const env: PathEnv = { platform: 'win32', appData: 'C:\\Injected\\AppData' }
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, env)).toBe(
      'C:\\Injected\\AppData\\Code\\User\\settings.json',
    )
    expect(vscodeEditionFile('vscode-insiders').resolvePath(home, env)).toBe(
      'C:\\Injected\\AppData\\Code - Insiders\\User\\settings.json',
    )
  })

  it('falls back to the APPDATA environment variable when appData is not injected', () => {
    const home = makeHome()
    process.env.APPDATA = 'C:\\From\\AppDataEnv'
    const env: PathEnv = { platform: 'win32' }
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, env)).toBe(
      'C:\\From\\AppDataEnv\\Code\\User\\settings.json',
    )
    expect(vscodeEditionFile('vscode-insiders').resolvePath(home, env)).toBe(
      'C:\\From\\AppDataEnv\\Code - Insiders\\User\\settings.json',
    )
  })

  it('falls back to <home>\\AppData\\Roaming when APPDATA is unset', () => {
    const home = makeHome()
    delete process.env.APPDATA
    const env: PathEnv = { platform: 'win32' }
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, env)).toBe(
      `${home}\\AppData\\Roaming\\Code\\User\\settings.json`,
    )
    expect(vscodeEditionFile('vscode-insiders').resolvePath(home, env)).toBe(
      `${home}\\AppData\\Roaming\\Code - Insiders\\User\\settings.json`,
    )
  })

  it('keeps the darwin and linux strings unchanged (Library on darwin, .config on linux)', () => {
    const home = makeHome()
    expect(copilotHarness.resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/Library/Application Support/Code/User/settings.json`,
    )
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/Library/Application Support/Code/User/settings.json`,
    )
    expect(vscodeEditionFile('vscode-insiders').resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/Library/Application Support/Code - Insiders/User/settings.json`,
    )
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.config/Code/User/settings.json`,
    )
    expect(vscodeEditionFile('vscode-insiders').resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.config/Code - Insiders/User/settings.json`,
    )
  })

  it('ignores an injected appData on darwin and linux: those strings stay unchanged', () => {
    const home = makeHome()
    expect(
      copilotHarness.resolvePath(home, { platform: 'darwin', appData: 'C:\\Injected\\AppData' }),
    ).toBe(`${home}/Library/Application Support/Code/User/settings.json`)
    expect(
      vscodeEditionFile('vscode-insiders').resolvePath(home, {
        platform: 'linux',
        appData: 'C:\\Injected\\AppData',
      }),
    ).toBe(`${home}/.config/Code - Insiders/User/settings.json`)
  })
})

describe('opencode config path (D14)', () => {
  it('resolves the win32 opencode.jsonc path with win32 separators', () => {
    const home = makeHome()
    expect(opencodeHarness.resolvePath(home, { platform: 'win32' })).toBe(
      `${home}\\.config\\opencode\\opencode.jsonc`,
    )
  })

  it('keeps the darwin and linux strings unchanged', () => {
    const home = makeHome()
    expect(opencodeHarness.resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/.config/opencode/opencode.jsonc`,
    )
    expect(opencodeHarness.resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.config/opencode/opencode.jsonc`,
    )
  })

  it('ignores XDG_CONFIG_HOME on win32, darwin and linux', () => {
    const home = makeHome()
    process.env.XDG_CONFIG_HOME = '/synthetic/xdg-junk'
    expect(opencodeHarness.resolvePath(home, { platform: 'win32' })).toBe(
      `${home}\\.config\\opencode\\opencode.jsonc`,
    )
    expect(opencodeHarness.resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/.config/opencode/opencode.jsonc`,
    )
    expect(opencodeHarness.resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.config/opencode/opencode.jsonc`,
    )
    expect(vscodeEditionFile('vscode-stable').resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.config/Code/User/settings.json`,
    )
  })
})

describe('pending harness config paths (D14 characterization)', () => {
  it('keeps the codex darwin and linux strings unchanged', () => {
    const home = makeHome()
    expect(codexHarness.resolvePath(home, { platform: 'darwin' })).toBe(`${home}/.codex/config.toml`)
    expect(codexHarness.resolvePath(home, { platform: 'linux' })).toBe(`${home}/.codex/config.toml`)
  })

  it('keeps the pi darwin and linux strings unchanged', () => {
    const home = makeHome()
    expect(piHarness.resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/.pi/agent/settings.json`,
    )
    expect(piHarness.resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.pi/agent/settings.json`,
    )
  })

  it('keeps the antigravity darwin and linux strings unchanged', () => {
    const home = makeHome()
    expect(antigravityHarness.resolvePath(home, { platform: 'darwin' })).toBe(
      `${home}/.gemini/antigravity-cli/settings.json`,
    )
    expect(antigravityHarness.resolvePath(home, { platform: 'linux' })).toBe(
      `${home}/.gemini/antigravity-cli/settings.json`,
    )
  })
})
