// Install detection for the capture settings surface (D9).
//
// WHY evidence rather than a PATH lookup: the dashboard server often runs
// under launchd or a service manager with a reduced PATH, so `which claude`
// would report false negatives. Each harness is judged from files and
// directories its installer leaves under the home the capture engine already
// resolves from, in-process, with no child process. The probe is injectable
// so the three states — including the one a real filesystem rarely produces,
// a permission failure — can be pinned without chmod tricks.
//
// States: `detected` when any probe finds the harness; `not-detected` when
// every probe is definitively absent; `unknown` when a probe failed (a
// permission-class error) and nothing was detected. "Unknown" is reported
// as unknown, never rounded to "not installed" (honest unobservability).

import { statSync } from 'node:fs'
import { posix, win32 } from 'node:path'

import type { CaptureHarnessWriter } from './harnesses/registry.js'
import { pjoin, type PathEnv } from './paths.js'

export type InstallState = 'detected' | 'not-detected' | 'unknown'

export type InstallDetection = {
  installed: InstallState
  /** One human-readable line per probe. Machine paths appear here, never host error text. */
  evidence: string[]
}

/** A probe failure is reduced to its errno code. */
export type ExistenceResult = 'present' | 'absent' | { failed: string }
export type ExistenceProbe = (path: string) => ExistenceResult

/** ENOENT and ENOTDIR are "definitely not there"; anything else is a failed probe. */
export function defaultExistenceProbe(path: string): ExistenceResult {
  try {
    statSync(path)
    return 'present'
  } catch (error) {
    const code =
      error !== null && typeof error === 'object' && typeof (error as { code?: unknown }).code === 'string'
        ? (error as { code: string }).code
        : 'UNKNOWN'
    return code === 'ENOENT' || code === 'ENOTDIR' ? 'absent' : { failed: code }
  }
}

/**
 * One question the install check asks, answered by any of its alternatives:
 * "is Claude Code here?" is yes for the `.claude` directory OR the native
 * binary. Each group reports a single evidence line, so a detected harness is
 * not padded with the alternatives it did not need.
 */
type Probe = { label: string; path: string }
type ProbeGroup = { probes: Probe[] }

function dirnameFor(platform: NodeJS.Platform, path: string): string {
  return platform === 'win32' ? win32.dirname(path) : posix.dirname(path)
}

function groupsFor(writer: CaptureHarnessWriter, home: string, env: PathEnv): ProbeGroup[] {
  const { platform } = env
  const exe = platform === 'win32' ? '.exe' : ''
  switch (writer.id) {
    case 'claude-code':
      return [
        {
          probes: [
            { label: '.claude directory', path: pjoin(platform, home, '.claude') },
            { label: 'native binary', path: pjoin(platform, home, '.local', 'bin', `claude${exe}`) },
          ],
        },
      ]
    case 'opencode':
      return [
        {
          probes: [
            { label: 'config directory', path: pjoin(platform, home, '.config', 'opencode') },
            {
              label: 'install-script binary',
              path: pjoin(platform, home, '.opencode', 'bin', `opencode${exe}`),
            },
          ],
        },
      ]
    case 'copilot':
      // Per edition: the user directory is what VS Code creates on first run.
      return [
        {
          probes: [
            {
              label: 'VS Code Stable user directory',
              path: dirnameFor(platform, copilotSettingsPath(writer, 'vscode-stable', home, env)),
            },
          ],
        },
        {
          probes: [
            {
              label: 'VS Code Insiders user directory',
              path: dirnameFor(platform, copilotSettingsPath(writer, 'vscode-insiders', home, env)),
            },
          ],
        },
      ]
    default:
      // Status-only and injected writers: their one config file is the evidence.
      return [{ probes: [{ label: 'config', path: writer.resolvePath(home, env) }] }]
  }
}

function copilotSettingsPath(
  writer: CaptureHarnessWriter,
  fileId: string,
  home: string,
  env: PathEnv,
): string {
  const file = writer.kind === 'managed' ? writer.configFiles?.find((f) => f.fileId === fileId) : undefined
  return (file?.resolvePath ?? writer.resolvePath)(home, env)
}

/** Judge one harness from filesystem evidence under `home`. */
export function detectInstall(
  writer: CaptureHarnessWriter,
  home: string,
  env: PathEnv,
  exists: ExistenceProbe = defaultExistenceProbe,
): InstallDetection {
  let found = false
  let failed = false
  const evidence: string[] = []
  for (const group of groupsFor(writer, home, env)) {
    const results = group.probes.map((probe) => ({ probe, result: exists(probe.path) }))
    const present = results.find((entry) => entry.result === 'present')
    if (present !== undefined) {
      found = true
      evidence.push(`${present.probe.label} found at ${present.probe.path}`)
      continue
    }
    const broken = results.find((entry) => typeof entry.result === 'object')
    if (broken !== undefined) {
      failed = true
      evidence.push(
        `could not inspect ${broken.probe.path} (${(broken.result as { failed: string }).failed})`,
      )
      continue
    }
    const first = group.probes[0]!
    evidence.push(
      group.probes.length === 1
        ? `${first.label} not found at ${first.path}`
        : `${first.label} not found at ${group.probes.map((probe) => probe.path).join(' or ')}`,
    )
  }
  const installed: InstallState = found ? 'detected' : failed ? 'unknown' : 'not-detected'
  return { installed, evidence }
}
