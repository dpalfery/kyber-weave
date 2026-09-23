// Regenerate the `ContextReport` fixtures the renderers are built against.
//
//   npm run report:fixtures
//
// Stream C's text and Markdown renderers, and the tray's popover, are written against
// `src/analysis/report/fixtures/*.json` rather than against a live store. That is what
// lets a renderer be tested for what it renders — including the cases that are hard to
// produce on demand, like a store whose last refresh failed two days ago.
//
// The scenarios are built from deterministic in-memory bridges rather than by refreshing a
// real store: a fixture regenerated from live data would change every time anyone ran it,
// and a renderer test diffing against a moving file proves nothing. Everything here is
// fixed — the clock, the ids, the token counts — so re-running this script on a clean tree
// produces no diff. That property is worth more than the fixtures having been through
// SQLite, and it is checked by `report-fixtures.test.ts`.

import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import type { HarnessRollupRow } from '../canon/types.js'
import type { Finding } from '../analysis/findings.js'
import { buildContextReport } from '../analysis/report/build.js'
import type { ContextReport, ReportSection } from '../analysis/report/types.js'
import type { KyberBridge, SessionSummary } from '../server/bridge.js'

const FIXTURE_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'analysis', 'report', 'fixtures')

/** Fixed so a regeneration is a no-op diff. */
const NOW = new Date('2026-09-19T12:00:00.000Z')
const VERSION = '1.0.0'
const hoursAgo = (n: number): string => new Date(NOW.getTime() - n * 3600_000).toISOString()

function session(overrides: Partial<SessionSummary> = {}): SessionSummary {
  return {
    session_id: 'sess-alpha',
    harness: 'claude-code',
    label: null,
    is_subagent: false,
    parent_session: null,
    agent_name: null,
    repo: 'kyber-weave',
    branch: 'main',
    started: hoursAgo(3),
    ended: hoursAgo(2),
    turn_count: 14,
    request_count: 41,
    total_input: 182_000,
    total_output: 9_400,
    cost_usd: null,
    models: ['claude-sonnet-4-5'],
    problems: 0,
    ...overrides,
  }
}

function finding(overrides: Partial<Finding> = {}): Finding {
  return {
    id: 'finding-tool-schema-residency',
    detectorId: 'tool-schema-residency' as Finding['detectorId'],
    title: 'Tool definitions hold 9,000 tokens across every turn',
    mechanism:
      'Four MCP servers publish their full schemas into every request, whether or not a tool from them is called.',
    evidenceLinks: [
      { spanId: 'span-0d41', turnIndex: 3, description: 'schema block resident, no call' },
      { spanId: 'span-0d52', turnIndex: 9, description: 'same block resident, no call' },
    ],
    confidence: 'high' as Finding['confidence'],
    estimatedWasteTokens: 6_400,
    recommendation:
      'Move the github and filesystem server schemas behind an on-demand skill so they load only when a tool from them is called.',
    errorBar: { lower: 5_200, upper: 7_600 },
    outcomeRiskCaveat:
      'Relocating schemas adds a load step the first time a tool is used in a session.',
    sessionId: 'sess-alpha',
    runId: 'run-2026-09-19',
    rankScore: 87.5,
    measurementClass: 'deterministic',
    confidenceBasis: 'resident in 14 of 14 turns, called in 0',
    ...overrides,
  }
}

const rollup = (overrides: Partial<HarnessRollupRow> = {}): HarnessRollupRow => ({
  harness: 'claude-code',
  sampleCount: 14,
  contextPressureMedian: 0.63,
  cacheHitRate: 0.81,
  toolYield: 2.4,
  delegationOverhead: 0.07,
  measurability: {},
  ...overrides,
})

const MEASURABLE_CONTEXT = {
  context: {
    measurable: true,
    contextLimit: 200_000,
    flaggedTurns: [9],
    turns: [
      {
        index: 14,
        pressure: 0.63,
        buckets: {
          system_prompt: 1_200,
          tool_definitions: 9_000,
          instruction_context: 3_400,
          conversation_history: 81_000,
          tool_result_content: 31_600,
        },
        residual: { tokens: 512 },
        toolDefinitionsByServer: { 'mcp-github': 5_100, 'mcp-filesystem': 2_600, 'mcp-fetch': 1_300 },
      },
    ],
  },
}

type Parts = {
  sessions?: SessionSummary[]
  findings?: Finding[]
  rollups?: HarnessRollupRow[]
  content?: unknown
  costs?: Array<{ sessionId: string; basis: string; status: string; value?: number }>
  refresh?: Partial<Record<'success' | 'failure' | 'running', {
    startedAt: string; completedAt: string | null; pid: number; summary: string | null
  }>>
}

function bridgeOf(parts: Parts): KyberBridge {
  return {
    listSessions: () => parts.sessions ?? [],
    listFindings: () => parts.findings ?? [],
    listHarnessRollups: () => parts.rollups ?? [],
    getQuarantine: () => [],
    getProblems: () => [],
    // The derived context a latest-turn measurement needs lives in the
    // persisted payload behind `getSessionPayload()` (R8.2); the content read
    // is the unclipped `{ sessionId, parts }` view and never carries context.
    getSessionContent: () => ({ sessionId: '', parts: [] }),
    getSessionPayload: () => parts.content ?? null,
    store: {
      listAll: () =>
        (parts.costs ?? []).map((cost) => ({
          sessionId: cost.sessionId,
          cost: { basis: cost.basis, status: cost.status, ...(cost.value === undefined ? {} : { value: cost.value }) },
        })),
      latestRefreshRun: (status: 'success' | 'failure' | 'running') => parts.refresh?.[status],
    },
  } as unknown as KyberBridge
}

const ALL_SECTIONS: ReportSection[] = [
  'coverage', 'detection', 'findings', 'harnesses', 'latestSession', 'cost',
]

const FRESH_REFRESH = {
  success: { startedAt: hoursAgo(1), completedAt: hoursAgo(1), pid: 4242, summary: '6 source job(s), 14 session(s)' },
}

/** The six documents a renderer has to handle. Each names the case it exists to pin. */
const SCENARIOS: Record<string, () => ContextReport> = {
  // Everything present: the shape a renderer is written against first.
  full: () =>
    buildContextReport(
      bridgeOf({
        sessions: [session()],
        findings: [finding(), finding({ id: 'finding-duplicate-grep', title: 'The same search ran four times', rankScore: 41.2, estimatedWasteTokens: 900, errorBar: { lower: 700, upper: 1_200 } })],
        rollups: [rollup(), rollup({ harness: 'codex', sampleCount: 3, contextPressureMedian: 0.21, cacheHitRate: null, toolYield: null, delegationOverhead: null })],
        content: MEASURABLE_CONTEXT,
        costs: [{ sessionId: 'sess-alpha', basis: 'harness', status: 'priced', value: 4.12 }],
        refresh: FRESH_REFRESH,
      }),
      { days: 7 },
      { now: NOW, kyberdashVersion: VERSION, sections: ALL_SECTIONS, detection: () => [
        { harness: 'claude-code', detected: true, probedPaths: ['~/.claude/projects'] },
        { harness: 'codex', detected: false, probedPaths: ['~/.codex/sessions'] },
      ] },
    ),

  // A store that has never been refreshed: every section must degrade to a remedy, not a zero.
  empty: () =>
    buildContextReport(bridgeOf({}), { days: 7 }, { now: NOW, kyberdashVersion: VERSION }),

  // Refreshed two days ago and failing since: the last success must still be visible (R10.5).
  stale: () =>
    buildContextReport(
      bridgeOf({
        sessions: [session({ ended: hoursAgo(50) })],
        rollups: [rollup()],
        refresh: {
          success: { startedAt: hoursAgo(50), completedAt: hoursAgo(50), pid: 100, summary: '6 source job(s), 14 session(s)' },
          failure: { startedAt: hoursAgo(2), completedAt: hoursAgo(2), pid: 220, summary: '2 of 6 source job(s) failed' },
        },
      }),
      { days: 7 },
      { now: NOW, kyberdashVersion: VERSION },
    ),

  // The harness exports no message structure: pressure and buckets are reasons, never 0 (R8.4).
  'unmeasurable-pressure': () =>
    buildContextReport(
      bridgeOf({
        sessions: [session({ harness: 'cursor' })],
        rollups: [rollup({ harness: 'cursor', contextPressureMedian: null })],
        content: { context: { measurable: false, reason: 'no_message_structure' } },
        refresh: FRESH_REFRESH,
      }),
      { days: 7 },
      { now: NOW, kyberdashVersion: VERSION },
    ),

  // Sessions and dimensions, but nothing to act on — the empty findings list is not an error.
  'no-findings': () =>
    buildContextReport(
      bridgeOf({ sessions: [session()], rollups: [rollup()], content: MEASURABLE_CONTEXT, refresh: FRESH_REFRESH }),
      { days: 7 },
      { now: NOW, kyberdashVersion: VERSION },
    ),

  // Two bases and an unpriced third: the renderer must show three rows, never one sum (R14.2).
  'mixed-basis-cost': () =>
    buildContextReport(
      bridgeOf({
        sessions: [session()],
        rollups: [rollup()],
        refresh: FRESH_REFRESH,
        costs: [
          { sessionId: 'sess-alpha', basis: 'harness', status: 'priced', value: 4.12 },
          { sessionId: 'sess-alpha', basis: 'published', status: 'priced', value: 3.87 },
          { sessionId: 'sess-alpha', basis: 'unknown', status: 'no_rate' },
        ],
      }),
      { days: 7 },
      { now: NOW, kyberdashVersion: VERSION },
    ),
}

export function generateFixtures(): Record<string, ContextReport> {
  return Object.fromEntries(Object.entries(SCENARIOS).map(([name, build]) => [name, build()]))
}

export function writeFixtures(dir = FIXTURE_DIR): string[] {
  mkdirSync(dir, { recursive: true })
  const written: string[] = []
  for (const [name, report] of Object.entries(generateFixtures())) {
    const path = join(dir, `${name}.json`)
    writeFileSync(path, `${JSON.stringify(report, null, 2)}\n`)
    written.push(path)
  }
  return written
}

const isMain = process.argv[1] !== undefined
  && fileURLToPath(import.meta.url) === process.argv[1]
if (isMain) {
  for (const path of writeFixtures()) process.stdout.write(`${path}\n`)
}
