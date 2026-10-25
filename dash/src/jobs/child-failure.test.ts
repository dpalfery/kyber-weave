// RED contract for Task B: a crashing child must produce a diagnosable failure.
//
// The engine runs the scheduled refresh as a child process (`dash refresh
// --trigger scheduled`) and records its outcome in the job footer. Two defects
// combine into an undiagnosable failure:
//
// 1. `summarize()` (`dash/src/jobs/host.ts`) takes the first non-empty line of the
//    child's stderr, sliced to 200 characters. The child's stderr tail contains
//    bundled commander.js source, so the stored fragment names commander
//    internals rather than the error - and the scheduled refresh has been
//    failing with nobody able to tell why.
// 2. The full stderr is discarded by design (`STDERR_CAP`, in-memory only), so
//    there is nothing to go back to after the fact.
//
// The pins below seed child failures through the `JobSpawner` seam - the same
// FakeChild/FakeSpawner harness `host.test.ts` and `receiver-host-health.test.ts`
// use - so they exercise the real `record()`/`summarize()` path. The on-disk
// artifact is located by scanning the host's `stateDir`: the tests do not assume
// a file name, only that the failure leaves bounded evidence where the host owns
// the filesystem.

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { JobHost } from './host.js'
import { JOBS_LOCK_FILE } from './lease.js'

type ChildResult = { code: number | null; stderr: string }

class FakeChild {
  killed = false
  readonly exited: Promise<ChildResult>
  private resolveExit!: (result: ChildResult) => void

  constructor(readonly program: string, readonly args: readonly string[]) {
    this.exited = new Promise((resolve) => { this.resolveExit = resolve })
  }

  exit(code: number | null, stderr = ''): void { this.resolveExit({ code, stderr }) }

  kill(): void {
    this.killed = true
    this.resolveExit({ code: null, stderr: '' })
  }
}

class FakeSpawner {
  readonly children: FakeChild[] = []

  spawn(program: string, args: readonly string[]): FakeChild {
    const child = new FakeChild(program, args)
    this.children.push(child)
    return child
  }

  get last(): FakeChild { return this.children[this.children.length - 1] }
}

let stateDir: string
let store: CanonStore
let spawner: FakeSpawner
const hosts: JobHost[] = []

function makeHost(): JobHost {
  const host = new JobHost({
    store,
    stateDir,
    program: 'kyberdash',
    spawner,
    // Never the failing seam: the failure under test comes from the child.
    maintenance: () => {},
  })
  hosts.push(host)
  return host
}

beforeEach(async () => {
  stateDir = await mkdtemp(join(tmpdir(), 'kyberdash-child-failure-'))
  store = new CanonStore(':memory:')
  spawner = new FakeSpawner()
})

afterEach(async () => {
  for (const host of hosts.splice(0)) await host.close()
  store.close()
  await rm(stateDir, { recursive: true, force: true })
})

/** A spawned child exists; `runNow` spawns only after the async lease acquisition. */
async function waitForChild(): Promise<FakeChild> {
  await vi.waitFor(() => expect(spawner.children).toHaveLength(1))
  return spawner.last
}

/**
 * Files a run left behind, minus the host's own lease file: the jobs lock is
 * acquired on every run and is not failure evidence.
 */
function addedFiles(before: Set<string>): Array<{ path: string; size: number }> {
  return snapshotFiles(stateDir).filter(
    (file) => !before.has(file.path) && !file.path.endsWith(JOBS_LOCK_FILE),
  )
}

/** Every regular file under `dir`, with sizes, so a test sees what a run left behind. */
function snapshotFiles(dir: string): Array<{ path: string; size: number }> {
  const found: Array<{ path: string; size: number }> = []
  const walk = (current: string): void => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const full = join(current, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.isFile()) found.push({ path: full, size: statSync(full).size })
    }
  }
  walk(dir)
  return found
}

// The shape of the real failure: the child's stderr tail is bundled-source noise
// (stack frames pointing into the bundled CLI/commander code), and the actual
// error is the last thing the child wrote.
const BUNDLED_NOISE_LINES = [
  'at Command._parseCommand (/app/dist/cli.js:1847:21)',
  'at Command.parseAsync (/app/dist/cli.js:1790:10)',
  'at process.processTicksAndRejections (node:internal/process/task_queues:95:5)',
]
const SEEDED_ERROR = 'Error: refresh run failed: harness source unreadable (EACCES: permission denied)'

function bundledStderr(): string {
  return [...BUNDLED_NOISE_LINES, SEEDED_ERROR].join('\n')
}

describe('a crashing child names the actual error', () => {
  it('records the real error, not the bundled-source fragment, as the failure summary', async () => {
    // RED today: `summarize()` keeps the FIRST non-empty line sliced to 200
    // characters, which here is a bundled `at Command...` frame - the recorded
    // summary decodes to commander internals rather than the error.
    const host = makeHost()

    const outcome = host.runNow('web')
    const child = await waitForChild()
    child.exit(1, bundledStderr())
    const result = await outcome

    expect(result).toMatchObject({ outcome: 'failed' })
    if (result.outcome !== 'failed') throw new Error('expected a failed outcome')
    expect(result.reason).toContain(SEEDED_ERROR)
    expect(result.reason).not.toContain('Command._parseCommand')
    expect(result.reason).not.toContain('dist/cli.js')

    const status = host.getStatus()
    expect(status.state).toBe('failed')
    expect(status.lastFailure).toContain(SEEDED_ERROR)
    expect(status.lastFailure).not.toContain('Command._parseCommand')
  })
})

describe('a crashing child leaves bounded on-disk evidence', () => {
  it('captures the full stderr to a bounded artifact under the state dir', async () => {
    // RED today: nothing is written - the child's stderr past `STDERR_CAP` is
    // dropped by design, so a failure cannot be diagnosed after the fact.
    // The scan assumes no file name, only the directory the host owns; if the
    // implementer puts the artifact elsewhere this test says so by failing on
    // the file list, not on a hard-coded path.
    const host = makeHost()

    const outcome = host.runNow('web')
    const child = await waitForChild()
    // Well past any sane bound, so the bound itself - not the input - decides
    // the artifact size. The error rides at the END, where stderr readers look.
    const huge = `${'x'.repeat(5_000_000)}\n${SEEDED_ERROR}\n`
    const before = new Set(snapshotFiles(stateDir).map((file) => file.path))
    child.exit(1, huge)
    const result = await outcome
    expect(result).toMatchObject({ outcome: 'failed' })

    const added = addedFiles(before)
    expect(added.length).toBeGreaterThanOrEqual(1)
    for (const file of added) {
      // Bounded: an absolute sanity ceiling no implementation should exceed,
      // and strictly smaller than the input that overflowed it.
      expect(file.size).toBeLessThanOrEqual(1_048_576)
      expect(file.size).toBeLessThan(huge.length)
    }
    // Diagnosable: the artifact keeps the error, not just the head of the noise.
    const bodies = added.map((file) => readFileSync(file.path, 'utf-8'))
    expect(bodies.some((body) => body.includes(SEEDED_ERROR))).toBe(true)
  })
})

describe('a successful child records no failure', () => {
  it('leaves no failure artifact and no failure summary', async () => {
    // Characterization: the happy path is unchanged by the failure-evidence
    // work - it passes before and after, and fails if success ever writes
    // failure evidence.
    const host = makeHost()

    const before = new Set(snapshotFiles(stateDir).map((file) => file.path))
    const outcome = host.runNow('web')
    const child = await waitForChild()
    child.exit(0)
    const result = await outcome

    expect(result).toMatchObject({ outcome: 'succeeded' })
    expect(host.getStatus().lastFailure).toBeNull()
    expect(addedFiles(before)).toEqual([])
  })
})
