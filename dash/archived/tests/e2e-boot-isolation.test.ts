// Storm guard (T4a, RED): the e2e dashboard boots must stay in-process and isolated.
//
// Failure this prevents: dash/e2e/harness-collection-boot.ts and
// dash/e2e/refresh-filters-boot.ts called runWebDashboard with no program, jobSpawner or
// receiverSpawner. The JobHost's first tick then spawned `dash refresh` children that
// re-booted the dashboard, and the storm reached 536 node processes. The refresh boot also
// served the owner's real ~/.kyberdash state, and its env name was a dead one.
//
// The contract under test (GREEN is T4b, which adds dash/e2e/boot-dashboard.ts):
//   bootIsolatedDashboard(options) starts a dashboard in this process and
//   - takes the boot options the caller passes today (period, provider, project, exclude,
//     and maintenance when the caller passes one), plus `root` (the world's temp root) and
//     `canonDbPath` (the world's canon.db);
//   - always passes port 0, an inert job spawner and receiver spawner that record calls and
//     start nothing, an inert receiver prober that answers 'refused', and a stateDir created
//     under `root` (never the operator's ~/.kyberdash);
//   - resolves a handle: `port`, `stateDir`, `jobSpawner.calls`, `receiverSpawner.calls`,
//     `firstTick` (settles once the hosted JobHost has finished its first tick),
//     `closedJobHostCount()` (how many hosted JobHosts the server closed), and `close()`.
//
// The in-process tests import the helper dynamically, so the static source checks run and
// report their own failures while the helper module is still missing.
import { existsSync, readdirSync, readFileSync, rmSync } from 'node:fs'
import { connect as connectSocket } from 'node:net'
import { homedir } from 'node:os'
import { dirname, join, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, describe, expect, it } from 'vitest'

import {
  createTempRoot as createHarnessRoot,
  refreshTempCanon as refreshHarnessCanon,
} from '../e2e/harness-collection-world.js'
import {
  createTempRoot as createRefreshRoot,
  refreshTempCanon as refreshRefreshCanon,
} from '../e2e/refresh-filters-world.js'
import { spawnGuardHits } from './setup/spawn-guard.js'

type RecordedCall = { readonly program: string; readonly args: readonly string[] }

/** The handle the helper resolves, as far as this suite relies on it. */
type BootHandle = {
  readonly port: number
  readonly stateDir: string
  readonly jobSpawner: { readonly calls: readonly RecordedCall[] }
  readonly receiverSpawner: { readonly calls: readonly RecordedCall[] }
  readonly firstTick: Promise<void>
  closedJobHostCount(): number
  close(): Promise<void>
}

type BootOptions = {
  readonly root: string
  readonly canonDbPath: string
  readonly port: number
  readonly period: string
  readonly provider: string
  readonly project: readonly string[]
  readonly exclude: readonly string[]
  readonly maintenance?: () => Promise<void>
}

type BootModule = {
  bootIsolatedDashboard: (options: BootOptions) => Promise<BootHandle>
}

const testDir = dirname(fileURLToPath(import.meta.url))
const dashRoot = join(testDir, '..')
const e2eDir = join(dashRoot, 'e2e')
const harnessBootPath = join(e2eDir, 'harness-collection-boot.ts')
const refreshBootPath = join(e2eDir, 'refresh-filters-boot.ts')
const harnessSpecPath = join(e2eDir, 'harness-collection.spec.ts')
const refreshSpecPath = join(e2eDir, 'refresh-filters.spec.ts')

const createdRoots: string[] = []

afterEach(() => {
  for (const root of createdRoots.splice(0)) {
    rmSync(root, { recursive: true, force: true })
  }
})

async function loadBootIsolatedDashboard(): Promise<BootModule['bootIsolatedDashboard']> {
  // Dynamic on purpose: while dash/e2e/boot-dashboard.ts is missing, only the in-process
  // tests fail on it, and the static source checks still run against today's files. The path
  // is held in a variable so the typecheck gate does not fail the whole project on a module
  // the GREEN task (T4b) creates; the runtime import still fails on the missing file.
  const helperPath = '../e2e/boot-dashboard.js'
  const module = (await import(helperPath)) as BootModule
  return module.bootIsolatedDashboard
}

/** True when something accepts a TCP connection on 127.0.0.1:port. */
function isListening(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = connectSocket({ host: '127.0.0.1', port })
    socket.once('connect', () => {
      socket.destroy()
      resolve(true)
    })
    socket.once('error', () => resolve(false))
  })
}

function readSource(path: string): string {
  return readFileSync(path, 'utf8')
}

/**
 * Boots the helper against one world, checks the in-process contract, and closes the handle.
 * The assertions name what they pin so a failure reads as the boot-isolation rule it broke.
 */
async function assertIsolatedBoot(
  label: string,
  root: string,
  canonDbPath: string,
  bootOptions: Pick<BootOptions, 'period' | 'provider' | 'project' | 'exclude' | 'maintenance'>,
): Promise<void> {
  const bootIsolatedDashboard = await loadBootIsolatedDashboard()
  const handle = await bootIsolatedDashboard({ root, canonDbPath, port: 0, ...bootOptions })
  try {
    await handle.firstTick

    expect(
      spawnGuardHits(),
      `${label} boot reached a real spawn; the spawn guard refused: ${spawnGuardHits().join('; ')}`,
    ).toEqual([])

    const scheduled = handle.jobSpawner.calls.map((call) => call.args.join(' '))
    expect(
      scheduled,
      `${label} boot must record its scheduled refresh on the inert job spawner (got: ${scheduled.join(' | ') || 'nothing'})`,
    ).toContain('dash refresh --trigger scheduled')

    expect(handle.port, `${label} boot must bind an ephemeral port`).toBeGreaterThan(0)
    const version = await fetch(`http://127.0.0.1:${handle.port}/api/kyber/version`)
    expect(version.status, `${label} boot must answer GET /api/kyber/version`).toBe(200)

    expect(
      handle.stateDir.startsWith(`${root}${sep}`),
      `${label} boot wrote state to ${handle.stateDir}, which is outside its own temp root ${root}`,
    ).toBe(true)
    expect(
      handle.stateDir.startsWith(join(homedir(), '.kyberdash')),
      `${label} boot must never use the owner's ~/.kyberdash state (got ${handle.stateDir})`,
    ).toBe(false)
  } finally {
    await handle.close()
  }

  expect(
    handle.closedJobHostCount(),
    `${label} boot close must release its hosted pair exactly once (the JobHost stays open otherwise)`,
  ).toBe(1)
  expect(
    await isListening(handle.port),
    `${label} boot close must stop listening on port ${handle.port}`,
  ).toBe(false)
}

describe('e2e boots: in-process, inert, and isolated (storm guard T4)', () => {
  it('the harness-collection world boots in-process with inert spawners and its own state dir', async () => {
    const root = createHarnessRoot()
    createdRoots.push(root)
    const world = await refreshHarnessCanon(root)
    world.store.close()

    await assertIsolatedBoot('harness-collection', world.root, world.dbPath, {
      period: 'all',
      provider: 'all',
      project: [],
      exclude: [],
      maintenance: async () => {},
    })
  })

  it('the refresh-filters world boots in-process with inert spawners and its own state dir', async () => {
    const root = createRefreshRoot()
    createdRoots.push(root)
    const world = await refreshRefreshCanon(root)
    world.store.close()

    await assertIsolatedBoot('refresh-filters', world.root, world.dbPath, {
      period: 'all',
      provider: 'all',
      project: [],
      exclude: [],
    })
  })
})

describe('e2e boot scripts: static source contract', () => {
  it.each([
    ['harness-collection-boot.ts', harnessBootPath],
    ['refresh-filters-boot.ts', refreshBootPath],
  ])('%s calls bootIsolatedDashboard and never calls runWebDashboard directly', (_name, path) => {
    const source = readSource(path)
    expect(source, `${path} must boot through bootIsolatedDashboard`).toMatch(/\bbootIsolatedDashboard\b/)
    expect(
      source,
      `${path} must not call runWebDashboard: with no inert spawners its JobHost spawns a storm`,
    ).not.toMatch(/\brunWebDashboard\b/)
  })

  it('the refresh-filters boot uses the live KYBERDASH_DASH_DIR env name, not the dead CODEBURN_DASH_DIR', () => {
    const source = readSource(refreshBootPath)
    expect(source, 'refresh-filters-boot.ts must read KYBERDASH_DASH_DIR').toContain('KYBERDASH_DASH_DIR')
    expect(
      source,
      'refresh-filters-boot.ts must not set the dead CODEBURN_DASH_DIR name, which the server never reads',
    ).not.toContain('CODEBURN_DASH_DIR')
  })

  it.each([
    ['harness-collection.spec.ts', harnessSpecPath],
    ['refresh-filters.spec.ts', refreshSpecPath],
  ])('%s spawns its boot detached and kills the whole process group', (_name, path) => {
    const source = readSource(path)
    expect(
      source,
      `${path} must spawn the boot with detached: true so its group can be killed`,
    ).toMatch(/detached:\s*true/)
    expect(
      source,
      `${path} must kill the boot's process group with process.kill(-pid, ...), not only the leader`,
    ).toContain('process.kill(-')
  })
})

// A missing source file must fail under its own name, not as an opaque read error above.
describe('e2e boot files exist', () => {
  it.each([harnessBootPath, refreshBootPath, harnessSpecPath, refreshSpecPath])(
    '%s is present',
    (path) => {
      expect(existsSync(path), `${path} is missing`).toBe(true)
    },
  )
})

// Storm guard (R3a): every Playwright config in dash/ must be one of the known configs, and each
// must deny real spawns and wire the process ledger. A config outside that set is how an e2e run
// escaped the guard: it would boot a dashboard whose JobHost re-runs the CLI.
const KNOWN_PLAYWRIGHT_CONFIGS = ['playwright.config.ts', 'playwright.isolated.config.ts'] as const
const playwrightConfigs = readdirSync(dashRoot)
  .filter((name) => /^playwright.*\.config\.ts$/.test(name))
  .sort()

describe('playwright configs: every config denies spawns and runs the process ledger', () => {
  it('the only dash/playwright*.config.ts files are the two known configs', () => {
    const unknown = playwrightConfigs.filter((name) => !(KNOWN_PLAYWRIGHT_CONFIGS as readonly string[]).includes(name))
    expect(
      unknown,
      `unknown Playwright config(s) in dash/ with no spawn deny or ledger hooks: ${unknown.join(', ') || 'none'}. ` +
        'Only playwright.config.ts and playwright.isolated.config.ts may exist; remove the rest.',
    ).toEqual([])
  })

  it.each(playwrightConfigs)('%s sets KYBERDASH_SPAWN_GUARD to deny', (name) => {
    const source = readSource(join(dashRoot, name))
    expect(
      source,
      `${name} must set KYBERDASH_SPAWN_GUARD to 'deny', config-level or in a webServer env, so its boots cannot spawn`,
    ).toMatch(/KYBERDASH_SPAWN_GUARD\W+deny/)
  })

  it.each(playwrightConfigs)('%s wires the process ledger (beginLedgerRun and globalTeardown)', (name) => {
    const source = readSource(join(dashRoot, name))
    expect(
      source,
      `${name} must call beginLedgerRun() so its worker processes are recorded in the ledger`,
    ).toMatch(/\bbeginLedgerRun\(\)/)
    expect(
      source,
      `${name} must set globalTeardown so the ledger is checked for survivors after the run`,
    ).toMatch(/\bglobalTeardown\s*:/)
  })
})
