// Storm guard (T3): a job child is re-run from the real CLI entry and from nothing else.
//
// Failure this prevents: resolveJobSpawnTarget handed back node + execArgv + process.argv[1]
// for whatever script happened to be running. Under the e2e boot script, or inside a vitest
// worker, that re-ran the script, the script booted another host, and the storm reached 536
// node processes. The resolver now answers `{ refused }` for any entry that is not the CLI,
// and the reason names the path so the failed refresh says what it refused.
//
// The SEA, the tsx checkout and the plain `dist/cli.js` shapes are pinned by
// web-spawn-target.test.ts; this file pins the accept/refuse decision on argv[1] only.
// Every case here is pure: the SpawnRuntime is injected. The one filesystem touch is a
// symlink in a temp directory, which the resolver must follow to the real file.
import { mkdtempSync, realpathSync, rmSync, symlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, describe, expect, it } from 'vitest'

import { resolveJobSpawnTarget, type SpawnRuntime, type SpawnTarget } from './spawn-target.js'

/** The refusal the resolver answers with for an entry it will not re-run. */
type Refused = { readonly refused: string }

const DASH_ROOT = fileURLToPath(new URL('../../', import.meta.url))
const LAUNCHER = join(DASH_ROOT, 'src', 'launcher.ts')
const BOOT_SCRIPT = join(DASH_ROOT, 'e2e', 'harness-collection-boot.ts')
const VITEST_WORKER = join(DASH_ROOT, 'node_modules', 'vitest', 'dist', 'workers', 'forks.js')
const TSX_LOADER_FLAGS = ['--import', `file://${join(DASH_ROOT, 'node_modules/tsx/dist/loader.mjs')}`]
const NODE = '/usr/local/bin/node'

const tempDirs: string[] = []

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

function runtimeFor(entry: string, execArgv: readonly string[] = []): SpawnRuntime {
  return { execPath: NODE, execArgv, argv: [NODE, entry, 'web'], isSea: false }
}

describe('resolveJobSpawnTarget: accepts the real CLI entry', () => {
  it('accepts this package src/launcher.ts as the entry', () => {
    const target: SpawnTarget | Refused = resolveJobSpawnTarget(runtimeFor(LAUNCHER))

    expect(target).not.toHaveProperty('refused')
    expect(target).toMatchObject({ program: NODE })
  })

  it('accepts the launcher reached through a symlink in a temp dir, and runs the real file', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-spawn-link-'))
    tempDirs.push(dir)
    const link = join(dir, 'kyberdash-entry.ts')
    symlinkSync(LAUNCHER, link)

    const target: SpawnTarget | Refused = resolveJobSpawnTarget(runtimeFor(link))

    expect(target, 'a symlink to the launcher is the CLI').not.toHaveProperty('refused')
    if ('refused' in target) return
    expect(realpathSync(target.programArgs[target.programArgs.length - 1]!)).toBe(realpathSync(LAUNCHER))
  })

  it('accepts a published package entry, dist/cli.js, from a global install', () => {
    const entry = '/opt/homebrew/lib/node_modules/kyberdash/dist/cli.js'

    const target: SpawnTarget | Refused = resolveJobSpawnTarget(runtimeFor(entry))

    expect(target).not.toHaveProperty('refused')
    expect(target).toMatchObject({ program: NODE, programArgs: [entry] })
  })

  it('never refuses a SEA on argv[1]: the binary is the CLI and argv[1] is an unexpanded argv0', () => {
    const target: SpawnTarget | Refused = resolveJobSpawnTarget({
      execPath: '/opt/kyberdash/bin/kyberdash',
      execArgv: [],
      argv: ['kyberdash', BOOT_SCRIPT, 'web'],
      isSea: true,
    })

    expect(target).toEqual({ program: '/opt/kyberdash/bin/kyberdash', programArgs: [] })
  })
})

describe('resolveJobSpawnTarget: refuses an entry that is not the CLI', () => {
  it.each([
    ['the e2e boot script', BOOT_SCRIPT],
    ['a vitest worker', VITEST_WORKER],
    ['an arbitrary script', '/tmp/x.ts'],
  ])('refuses %s and names its path in the reason', (_label, entry) => {
    const target: SpawnTarget | Refused = resolveJobSpawnTarget(runtimeFor(entry))

    expect(target, `re-running ${entry} must be refused`).toEqual({ refused: expect.stringContaining(entry) })
    expect(target).not.toHaveProperty('program')
  })

  it('refuses the e2e boot script even when the process runs under the tsx loader', () => {
    // The storm ran under tsx: the loader flags are real, so the refusal cannot depend on them.
    const target: SpawnTarget | Refused = resolveJobSpawnTarget(runtimeFor(BOOT_SCRIPT, TSX_LOADER_FLAGS))

    expect(target).toEqual({ refused: expect.stringContaining(BOOT_SCRIPT) })
  })

  it('refuses a symlink named cli.js whose target is the boot script: the decision follows the real file', () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyber-spawn-disguise-'))
    tempDirs.push(dir)
    const disguised = join(dir, 'cli.js')
    symlinkSync(BOOT_SCRIPT, disguised)

    const target: SpawnTarget | Refused = resolveJobSpawnTarget(runtimeFor(disguised))

    expect(target).toEqual({ refused: expect.stringContaining(disguised) })
  })
})
