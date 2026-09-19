import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import viewPathPatterns from '../../../src/server/view-paths.json' with { type: 'json' }

import {
  VIEW_PATH_PATTERNS,
  applySpineAction,
  locationFromPath,
  pathFromLocation,
  pushView,
  restoreFromPopState,
  resolveAncestry,
  type AncestryApi,
  type ViewLocation,
} from './router.js'

const jsonOnDisk = JSON.parse(
  readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../../src/server/view-paths.json'), 'utf8'),
) as readonly string[]

/**
 * One location per pattern, carrying only the fields the URL itself names.
 * Ancestry (harness on a run, run on a session) is reconstructed in 7.2 —
 * putting those fields here would make the round-trip lie about what the
 * path can restore without a fetch.
 */
const EXAMPLE_BY_PATTERN: Record<string, ViewLocation> = {
  '/': { level: 'context-doctor' },
  '/harness/:harnessId': { level: 'harness', harnessId: 'codex' },
  '/run/:runId': { level: 'run', runId: 'run-42' },
  '/session/:sessionId': { level: 'execution', executionId: 'sess-7' },
  '/session/:sessionId/turn/:turnIndex': { level: 'turn', executionId: 'sess-7', turnIndex: 3 },
  '/finding/:findingId': { level: 'finding', findingId: 'fid-9' },
  '/compare?a=:runId&b=:runId': { level: 'compare', compareA: 'run-a', compareB: 'run-b' },
  '/quarantine': { level: 'quarantine' },
  '/problems': { level: 'problems' },
}

describe('view-paths.json drives the web router (R5.1)', () => {
  it('imports the same list the CLI and tray match against, not a second copy', () => {
    expect(VIEW_PATH_PATTERNS).toEqual(viewPathPatterns)
    expect(VIEW_PATH_PATTERNS).toEqual(jsonOnDisk)
  })

  it('has an example location for every pattern, so a new route cannot silently skip the round-trip', () => {
    expect(Object.keys(EXAMPLE_BY_PATTERN).sort()).toEqual([...VIEW_PATH_PATTERNS].sort())
  })

  it('round-trips spine location → path → location for every pattern', () => {
    for (const pattern of VIEW_PATH_PATTERNS) {
      const location = EXAMPLE_BY_PATTERN[pattern]
      expect(location, `missing example for ${pattern}`).toBeDefined()
      const path = pathFromLocation(location!)
      expect(path, `${pattern} did not encode`).not.toBeNull()
      expect(locationFromPath(path!), pattern).toEqual(location)
    }
  })

  it('round-trips ids that need encoding, so a finding id with a slash still lands', () => {
    const location: ViewLocation = { level: 'finding', findingId: 'fid/with spaces' }
    const path = pathFromLocation(location)
    expect(path).toBe('/finding/fid%2Fwith%20spaces')
    expect(locationFromPath(path!)).toEqual(location)
  })

  it('rejects a path that is not a view, so an unknown URL does not become Context Doctor', () => {
    expect(locationFromPath('/usage')).toBeNull()
    expect(locationFromPath('/finding/')).toBeNull()
    expect(locationFromPath('/compare')).toBeNull()
    expect(locationFromPath('/compare?a=r1')).toBeNull()
    expect(locationFromPath('/session/s1/turn/')).toBeNull()
    expect(locationFromPath('/session/s1/turn/nope')).toBeNull()
  })
})

class MemoryHistory {
  entries: Array<{ state: unknown; url: string }> = [{ state: null, url: '/' }]
  index = 0

  get url(): string {
    return this.entries[this.index]!.url
  }

  get state(): unknown {
    return this.entries[this.index]!.state
  }

  pushState(state: unknown, _unused: string, url?: string): void {
    this.entries = this.entries.slice(0, this.index + 1)
    this.entries.push({ state, url: url ?? this.url })
    this.index = this.entries.length - 1
  }

  replaceState(state: unknown, _unused: string, url?: string): void {
    this.entries[this.index] = { state, url: url ?? this.url }
  }

  back(): void {
    if (this.index > 0) this.index -= 1
  }

  forward(): void {
    if (this.index < this.entries.length - 1) this.index += 1
  }
}

function notFound(id: string): Error {
  const error = new Error(`Request failed (404) for /api/kyber/${id}`)
  Object.assign(error, { status: 404, path: `/api/kyber/${id}` })
  return error
}

function seededApi(): AncestryApi {
  return {
    async fetchHarness(id) {
      if (id === 'missing') throw notFound(id)
      return { harness: id }
    },
    async fetchRun(id) {
      if (id === 'missing-run') throw notFound(id)
      return { runId: id, harness: 'codex' }
    },
    async fetchSession(id) {
      if (id === 'missing-session') throw notFound(id)
      return { id, harness: 'codex', runId: 'run-42', executionId: id }
    },
    async fetchFinding(id) {
      if (id === 'missing-finding') throw notFound(id)
      return { id, runId: 'run-42', sessionId: 'sess-7', harness: 'codex' }
    },
  }
}

describe('openSpine history and popstate (R5.2, R5.3)', () => {
  it('pushes a history entry carrying the stack so Back can restore it without a refetch', () => {
    const history = new MemoryHistory()
    const stack: ViewLocation[] = [
      { level: 'context-doctor' },
      { level: 'harness', harnessId: 'codex' },
    ]
    pushView('/harness/codex', stack, history)
    expect(history.url).toBe('/harness/codex')
    expect(history.entries).toHaveLength(2)
    expect(restoreFromPopState(history.state, history.url)).toEqual(stack)
  })

  it('restores the previous stack on popstate after Back, then the later stack on Forward', () => {
    const history = new MemoryHistory()
    const doctor: ViewLocation[] = [{ level: 'context-doctor' }]
    const harness: ViewLocation[] = [...doctor, { level: 'harness', harnessId: 'codex' }]
    const run: ViewLocation[] = [...harness, { level: 'run', runId: 'run-42', harnessId: 'codex' }]

    pushView('/', doctor, history)
    pushView('/harness/codex', harness, history)
    pushView('/run/run-42', run, history)

    history.back()
    expect(restoreFromPopState(history.state, history.url)).toEqual(harness)
    history.back()
    expect(restoreFromPopState(history.state, history.url)).toEqual(doctor)
    history.forward()
    expect(restoreFromPopState(history.state, history.url)).toEqual(harness)
    history.forward()
    expect(restoreFromPopState(history.state, history.url)).toEqual(run)
  })
})

describe('resolveAncestry rebuilds the stack a click path would have built (R5.2)', () => {
  const api = seededApi()

  it('rebuilds doctor → harness → run for /run/:id', async () => {
    const navigated = applySpineAction(
      applySpineAction([{ level: 'context-doctor' }], {
        type: 'push',
        location: { level: 'harness', harnessId: 'codex' },
      }),
      { type: 'push', location: { level: 'run', runId: 'run-42', harnessId: 'codex' } },
    )
    const direct = await resolveAncestry({ level: 'run', runId: 'run-42' }, api)
    expect(direct).toEqual({ status: 'ok', stack: navigated })
  })

  it('rebuilds doctor → harness → run → execution for /session/:id', async () => {
    const navigated = applySpineAction(
      applySpineAction(
        applySpineAction([{ level: 'context-doctor' }], {
          type: 'push',
          location: { level: 'harness', harnessId: 'codex' },
        }),
        { type: 'push', location: { level: 'run', runId: 'run-42', harnessId: 'codex' } },
      ),
      {
        type: 'push',
        location: { level: 'execution', executionId: 'sess-7', runId: 'run-42', harnessId: 'codex' },
      },
    )
    const direct = await resolveAncestry({ level: 'execution', executionId: 'sess-7' }, api)
    expect(direct).toEqual({ status: 'ok', stack: navigated })
  })

  it('rebuilds the session ancestry plus the turn for /session/:id/turn/:n', async () => {
    const navigated = applySpineAction(
      applySpineAction(
        applySpineAction(
          applySpineAction([{ level: 'context-doctor' }], {
            type: 'push',
            location: { level: 'harness', harnessId: 'codex' },
          }),
          { type: 'push', location: { level: 'run', runId: 'run-42', harnessId: 'codex' } },
        ),
        {
          type: 'push',
          location: { level: 'execution', executionId: 'sess-7', runId: 'run-42', harnessId: 'codex' },
        },
      ),
      {
        type: 'push',
        location: {
          level: 'turn',
          executionId: 'sess-7',
          runId: 'run-42',
          harnessId: 'codex',
          turnIndex: 3,
        },
      },
    )
    const direct = await resolveAncestry(
      { level: 'turn', executionId: 'sess-7', turnIndex: 3 },
      api,
    )
    expect(direct).toEqual({ status: 'ok', stack: navigated })
  })

  it('rebuilds doctor → harness → run → finding for /finding/:id', async () => {
    const navigated = applySpineAction(
      applySpineAction(
        applySpineAction([{ level: 'context-doctor' }], {
          type: 'push',
          location: { level: 'harness', harnessId: 'codex' },
        }),
        { type: 'push', location: { level: 'run', runId: 'run-42', harnessId: 'codex' } },
      ),
      {
        type: 'push',
        location: { level: 'finding', findingId: 'fid-9', runId: 'run-42', harnessId: 'codex' },
      },
    )
    const direct = await resolveAncestry({ level: 'finding', findingId: 'fid-9' }, api)
    expect(direct).toEqual({ status: 'ok', stack: navigated })
  })

  it('names the missing id when the API 404s, so the shell can show not-found instead of crashing', async () => {
    expect(await resolveAncestry({ level: 'run', runId: 'missing-run' }, api)).toEqual({
      status: 'not-found',
      id: 'missing-run',
    })
    expect(await resolveAncestry({ level: 'execution', executionId: 'missing-session' }, api)).toEqual({
      status: 'not-found',
      id: 'missing-session',
    })
    expect(await resolveAncestry({ level: 'finding', findingId: 'missing-finding' }, api)).toEqual({
      status: 'not-found',
      id: 'missing-finding',
    })
  })
})
