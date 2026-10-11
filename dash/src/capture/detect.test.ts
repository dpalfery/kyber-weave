import { describe, expect, it } from 'vitest'

import { detectInstall, type ExistenceProbe, type ExistenceResult } from './detect.js'
import { CAPTURE_HARNESS_REGISTRY } from './harnesses/registry.js'
import type { CaptureHarnessWriter } from './harnesses/registry.js'

function writerFor(id: string): CaptureHarnessWriter {
  const writer = CAPTURE_HARNESS_REGISTRY.find((candidate) => candidate.id === id)
  if (writer === undefined) throw new Error(`no writer ${id}`)
  return writer
}

/** An injected probe: exactly the listed paths are present, one may fail, the rest are absent. */
function probeOf(present: readonly string[], failing: Record<string, string> = {}): ExistenceProbe {
  return (path): ExistenceResult => {
    const code = failing[path]
    if (code !== undefined) return { failed: code }
    return present.includes(path) ? 'present' : 'absent'
  }
}

const POSIX = { platform: 'linux' as const }
const WIN = { platform: 'win32' as const, appData: 'C:\\Users\\me\\AppData\\Roaming' }

describe('detectInstall: claude-code', () => {
  const claude = writerFor('claude-code')

  it('is detected by the ~/.claude directory', () => {
    const result = detectInstall(claude, '/home/me', POSIX, probeOf(['/home/me/.claude']))
    expect(result.installed).toBe('detected')
    // A detected harness is not padded with the alternative that was not needed.
    expect(result.evidence).toEqual(['.claude directory found at /home/me/.claude'])
  })

  it('is detected by the native binary alone', () => {
    const result = detectInstall(claude, '/home/me', POSIX, probeOf(['/home/me/.local/bin/claude']))
    expect(result.installed).toBe('detected')
  })

  it('is not-detected when every probe is definitively absent', () => {
    const result = detectInstall(claude, '/home/me', POSIX, probeOf([]))
    expect(result.installed).toBe('not-detected')
    // One line per question asked, naming every place that was looked.
    expect(result.evidence).toHaveLength(1)
    expect(result.evidence[0]).toBe(
      '.claude directory not found at /home/me/.claude or /home/me/.local/bin/claude',
    )
  })

  it('is unknown when a probe failed and nothing was detected', () => {
    const result = detectInstall(
      claude,
      '/home/me',
      POSIX,
      probeOf([], { '/home/me/.claude': 'EACCES' }),
    )
    expect(result.installed).toBe('unknown')
    expect(result.evidence.join('\n')).toContain('could not inspect /home/me/.claude (EACCES)')
  })

  it('stays detected when another probe found it, even if one failed', () => {
    const result = detectInstall(
      claude,
      '/home/me',
      POSIX,
      probeOf(['/home/me/.local/bin/claude'], { '/home/me/.claude': 'EPERM' }),
    )
    expect(result.installed).toBe('detected')
  })

  it('uses claude.exe and backslashes on win32', () => {
    const seen: string[] = []
    detectInstall(claude, 'C:\\Users\\me', WIN, (path) => {
      seen.push(path)
      return 'absent'
    })
    expect(seen).toEqual(['C:\\Users\\me\\.claude', 'C:\\Users\\me\\.local\\bin\\claude.exe'])
  })
})

describe('detectInstall: opencode', () => {
  const opencode = writerFor('opencode')

  it('is detected by the config directory or the install-script binary', () => {
    expect(
      detectInstall(opencode, '/h', POSIX, probeOf(['/h/.config/opencode'])).installed,
    ).toBe('detected')
    expect(
      detectInstall(opencode, '/h', POSIX, probeOf(['/h/.opencode/bin/opencode'])).installed,
    ).toBe('detected')
  })

  it('is not-detected when both are absent', () => {
    expect(detectInstall(opencode, '/h', POSIX, probeOf([])).installed).toBe('not-detected')
  })

  it('uses opencode.exe on win32', () => {
    const seen: string[] = []
    detectInstall(opencode, 'C:\\Users\\me', WIN, (path) => {
      seen.push(path)
      return 'absent'
    })
    expect(seen).toContain('C:\\Users\\me\\.opencode\\bin\\opencode.exe')
  })
})

describe('detectInstall: copilot', () => {
  const copilot = writerFor('copilot')

  it('evaluates each VS Code edition separately and is detected by either', () => {
    const stableOnly = detectInstall(copilot, '/h', POSIX, probeOf(['/h/.config/Code/User']))
    expect(stableOnly.installed).toBe('detected')
    expect(stableOnly.evidence.some((line) => line.includes('Stable') && line.includes('found at'))).toBe(true)
    expect(stableOnly.evidence.some((line) => line.includes('Insiders') && line.includes('not found'))).toBe(true)

    const insidersOnly = detectInstall(copilot, '/h', POSIX, probeOf(['/h/.config/Code - Insiders/User']))
    expect(insidersOnly.installed).toBe('detected')
  })

  it('is not-detected when neither user directory exists', () => {
    expect(detectInstall(copilot, '/h', POSIX, probeOf([])).installed).toBe('not-detected')
  })

  it('resolves the win32 user directories through the app-data root', () => {
    const seen: string[] = []
    detectInstall(copilot, 'C:\\Users\\me', WIN, (path) => {
      seen.push(path)
      return 'absent'
    })
    expect(seen).toEqual([
      'C:\\Users\\me\\AppData\\Roaming\\Code\\User',
      'C:\\Users\\me\\AppData\\Roaming\\Code - Insiders\\User',
    ])
  })
})

describe('detectInstall: status-only harnesses', () => {
  it('reads the harness config file as the evidence', () => {
    const codex = writerFor('codex')
    expect(detectInstall(codex, '/h', POSIX, probeOf(['/h/.codex/config.toml'])).installed).toBe('detected')
    const absent = detectInstall(codex, '/h', POSIX, probeOf([]))
    expect(absent.installed).toBe('not-detected')
    expect(absent.evidence).toEqual(['config not found at /h/.codex/config.toml'])
  })

  it('treats an injected probe that throws or returns a malformed result as a failed probe', () => {
    const writer = CAPTURE_HARNESS_REGISTRY.find((candidate) => candidate.id === 'claude-code')!
    const env = { platform: 'linux' } as const
    const thrower = (): never => {
      throw Object.assign(new Error('boom'), { code: 'EACCES' })
    }
    const thrown = detectInstall(writer, '/home/x', env, thrower)
    expect(thrown.installed).toBe('unknown')
    expect(thrown.evidence.join('\n')).toContain('EACCES')
    const malformed = detectInstall(writer, '/home/x', env, (() => null) as never)
    expect(malformed.installed).toBe('unknown')
  })
})
