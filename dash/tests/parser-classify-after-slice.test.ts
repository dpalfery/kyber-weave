import { afterEach, beforeEach, expect, it } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { parseAllSessions, filterProjectsByDateRange, clearSessionCache } from '../src/parser.js'
import { loadPricing } from '../src/models.js'
import type { ClassifiedTurn, DateRange } from '../src/types.js'

// scanProjectDirs decides the date slice on the RAW cached turn and classifies
// only survivors. The classification itself must still see each surviving
// turn's COMPLETE call list, and the branch/PR carries must still run over the
// full ordered turn list — so this fixture puts the branch anchor and the PR
// reference before the range, and straddles the range start with a turn whose
// only Edit lands on the out-of-range side.

const SESSION = '22222222-2222-4222-8222-222222222222'
const CWD = '/tmp/slice-proj'
const BRANCH = 'feat/carry'
const PR = 'https://github.com/o/r/pull/42'
const RANGE: DateRange = {
  start: new Date('2026-07-20T00:00:00.000Z'),
  end: new Date('2026-07-20T23:59:59.999Z'),
}

let tmpDir: string

beforeEach(async () => {
  clearSessionCache()
  tmpDir = await mkdtemp(join(tmpdir(), 'slice-'))
  process.env['CLAUDE_CONFIG_DIR'] = join(tmpDir, 'claude')
  process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache')
})

afterEach(async () => {
  clearSessionCache()
  delete process.env['CLAUDE_CONFIG_DIR']
  delete process.env['CODEBURN_CACHE_DIR']
  await rm(tmpDir, { recursive: true, force: true })
})

function user(ts: string, content: string): string {
  return JSON.stringify({ type: 'user', sessionId: SESSION, timestamp: ts, cwd: CWD, gitBranch: BRANCH, message: { role: 'user', content } })
}

function assistant(ts: string, id: string, tools: string[]): string {
  return JSON.stringify({
    type: 'assistant', sessionId: SESSION, timestamp: ts, cwd: CWD, gitBranch: BRANCH,
    message: {
      id, type: 'message', role: 'assistant', model: 'claude-sonnet-4-5',
      content: tools.map((name, i) => ({ type: 'tool_use', id: `${id}_${i}`, name, input: {} })),
      usage: { input_tokens: 100, output_tokens: 50 },
    },
  })
}

async function writeTranscript(): Promise<void> {
  const projDir = join(tmpDir, 'claude', 'projects', 'slice-proj')
  await mkdir(projDir, { recursive: true })
  await writeFile(join(projDir, `${SESSION}.jsonl`), [
    // Before the range: the only turn carrying the branch (the cache elides an
    // unchanged branch on later turns) and the only PR reference.
    user('2026-07-19T09:00:00.000Z', `please finish ${PR}`),
    assistant('2026-07-19T09:00:05.000Z', 'm1', ['Read']),
    // Straddles the range start: the Edit is on the out-of-range call.
    user('2026-07-19T23:50:00.000Z', 'keep going overnight'),
    assistant('2026-07-19T23:50:10.000Z', 'm2', ['Edit']),
    assistant('2026-07-20T00:10:00.000Z', 'm3', ['Read']),
    // Fully inside the range.
    user('2026-07-20T10:00:00.000Z', 'what changed?'),
    assistant('2026-07-20T10:00:05.000Z', 'm4', ['Read']),
  ].join('\n') + '\n', 'utf-8')
}

function shape(turn: ClassifiedTurn): unknown {
  return {
    timestamp: turn.timestamp,
    category: turn.category,
    subCategory: turn.subCategory,
    retries: turn.retries,
    hasEdits: turn.hasEdits,
    gitBranch: turn.gitBranch,
    prRefs: turn.prRefs,
    calls: turn.assistantCalls.map(c => c.timestamp),
  }
}

it('slices before classifying without changing carried branch, PR, or turn classification', async () => {
  await loadPricing()
  await writeTranscript()

  const sliced = await parseAllSessions(RANGE, 'claude')
  // Reference: the old order — classify every turn from the full history, then
  // apply the same range slice afterwards.
  clearSessionCache()
  const reference = filterProjectsByDateRange(await parseAllSessions(undefined, 'claude'), RANGE)

  const session = sliced[0]!.sessions[0]!
  expect(session.turns.map(shape)).toEqual(reference[0]!.sessions[0]!.turns.map(shape))

  // The branch anchor and the PR reference both live before the range.
  expect(session.everHadBranch).toBe(true)
  expect(session.turns.every(t => t.gitBranch === BRANCH)).toBe(true)
  expect(session.prRefsAtRangeStart).toEqual([PR])

  // The straddling turn kept only its in-range call, but was classified from
  // the complete call list — the Edit it dropped still counts.
  const straddled = session.turns[0]!
  expect(straddled.assistantCalls.map(c => c.timestamp)).toEqual(['2026-07-20T00:10:00.000Z'])
  expect(straddled.hasEdits).toBe(true)
})
