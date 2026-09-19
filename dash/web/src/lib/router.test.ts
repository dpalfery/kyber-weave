import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import viewPathPatterns from '../../../src/server/view-paths.json' with { type: 'json' }

import {
  VIEW_PATH_PATTERNS,
  locationFromPath,
  pathFromLocation,
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
