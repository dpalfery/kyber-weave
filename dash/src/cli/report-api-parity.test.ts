// R11.14: `kyberdash report --format json` and `GET /api/kyber/report` are the
// same document for one store and scope. They agree because they share
// `buildContextReport`, not because two implementations are kept in step.
//
// `generatedAt` is excluded — it is "when this process ran". `detection` and
// `coverage.storePath` are also excluded: detection walks the filesystem and
// is CLI-only (design C1), and the REST handler does not pass `storePath`.
// Findings, dimensions, session figures and cost are what R11.14 names.

import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'
import type { AddressInfo } from 'node:net'
import type { Server } from 'node:http'

import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord } from '../canon/types.js'
import { runWebDashboard } from './web.js'
import { KyberBridge } from '../server/bridge.js'

const homes: string[] = []
const servers: Server[] = []
const bridges: KyberBridge[] = []

afterEach(async () => {
  while (servers.length > 0) {
    const server = servers.pop()
    if (server) await new Promise<void>((resolve) => server.close(() => resolve()))
  }
  while (bridges.length > 0) bridges.pop()?.close()
  while (homes.length > 0) {
    const home = homes.pop()
    if (home) await rm(home, { recursive: true, force: true })
  }
})

function comparable(report: Record<string, unknown>): unknown {
  const { generatedAt: _generatedAt, detection: _detection, coverage, ...rest } = report
  if (coverage !== undefined && coverage !== null && typeof coverage === 'object') {
    const { storePath: _storePath, ...coverageRest } = coverage as Record<string, unknown>
    return { ...rest, coverage: coverageRest }
  }
  return rest
}

describe('report JSON and GET /api/kyber/report (R11.14)', () => {
  it('are deeply equal for the same seeded store and scope, ignoring generatedAt', async () => {
    const home = await mkdtemp(join(tmpdir(), 'kyberdash-report-parity-'))
    homes.push(home)
    const db = join(home, 'canon.db')
    const now = new Date()
    const store = new CanonStore(db)
    store.upsertSession({
      sessionId: 'sess-alpha',
      harness: 'claude-cli',
      repo: 'kyber-weave',
      started: new Date(now.getTime() - 3600_000).toISOString(),
      ended: now.toISOString(),
      payload: {
        summary: { turn_count: 4, total_input: 1200, total_output: 80 },
      },
    })
    store.upsertFinding({
      id: 'finding-tool-schema-residency',
      detectorId: 'dormant-tool-schema',
      title: 'Tool definitions hold 9,000 tokens across every turn',
      mechanism: 'Schemas are resident in every turn.',
      evidenceLinks: [
        { spanId: 'span-0d41', turnIndex: 3, description: 'resident' },
        { spanId: 'span-0d52', turnIndex: 9, description: 'resident' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 6400,
      recommendation: 'Move the github server schema behind an on-demand skill.',
      errorBar: { lower: 5200, upper: 7600 },
      outcomeRiskCaveat: 'Relocating schemas adds a load step.',
      sessionId: 'sess-alpha',
      rankScore: 87.5,
      measurementClass: 'deterministic',
    })
    store.close()

    const cli = spawnSync(
      process.execPath,
      ['--import', 'tsx', 'src/launcher.ts', 'report', '--format', 'json', '--db', db, '--days', '7'],
      {
        cwd: new URL('../..', import.meta.url),
        env: { ...process.env, HOME: home, TZ: 'UTC' },
        encoding: 'utf-8',
        timeout: 30_000,
      },
    )
    expect(cli.status, cli.stderr).toBe(0)
    const cliReport = JSON.parse(cli.stdout) as Record<string, unknown>

    const bridge = new KyberBridge({ canonPath: db })
    bridges.push(bridge)
    const server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    servers.push(server)
    const port = (server.address() as AddressInfo).port
    const response = await fetch(`http://127.0.0.1:${port}/api/kyber/report?days=7`)
    expect(response.status).toBe(200)
    const apiReport = (await response.json()) as Record<string, unknown>

    expect(comparable(cliReport)).toEqual(comparable(apiReport))
  })

  // The same parity question over a two-harness store read through a harness
  // scope: the scoped sections stay scoped while the surfaces stay identical.
  // The Claude session carries a measurable persisted context so the parity
  // comparison covers measured latest-turn figures, not only absent ones; the
  // rollup-only `cursor` row and the outside-window session exercise the
  // coverage inventory edges on both surfaces at once.
  it('are deeply equal for a two-harness scoped store, ignoring generatedAt', async () => {
    const home = await mkdtemp(join(tmpdir(), 'kyberdash-report-parity-scoped-'))
    homes.push(home)
    const db = join(home, 'canon.db')
    const now = new Date()
    const store = new CanonStore(db)
    store.upsertSession({
      sessionId: 'sess-claude',
      harness: 'claude-cli',
      repo: 'kyber-weave',
      started: new Date(now.getTime() - 7200_000).toISOString(),
      ended: new Date(now.getTime() - 3600_000).toISOString(),
      payload: {
        summary: { turn_count: 2, total_input: 127_512, total_output: 96 },
        context: {
          measurable: true,
          contextLimit: 200_000,
          flaggedTurns: [2],
          turns: [
            {
              index: 1,
              pressure: 0.18,
              buckets: {
                system_prompt: 4100,
                tool_definitions: 9000,
                instruction_context: 900,
                conversation_history: 31_000,
                tool_result_content: 12_000,
              },
              residual: { tokens: 190 },
              toolDefinitionsByServer: { 'mcp-github': 6000, 'mcp-fs': 3000 },
            },
            {
              index: 2,
              pressure: 0.61,
              buckets: {
                system_prompt: 4100,
                tool_definitions: 9000,
                instruction_context: 900,
                conversation_history: 96_000,
                tool_result_content: 15_000,
              },
              residual: { tokens: 480 },
              toolDefinitionsByServer: { 'mcp-github': 6000, 'mcp-fs': 3000 },
            },
          ],
        },
      },
    })
    store.upsertSession({
      sessionId: 'sess-codex',
      harness: 'codex-cli',
      repo: 'kyber-weave',
      started: new Date(now.getTime() - 5400_000).toISOString(),
      ended: now.toISOString(),
      payload: { summary: { turn_count: 6, total_input: 4100, total_output: 300 } },
    })
    store.upsertSession({
      sessionId: 'sess-opencode-stale',
      harness: 'opencode',
      repo: 'kyber-weave',
      started: new Date(now.getTime() - 9 * 24 * 3600_000 - 3600_000).toISOString(),
      ended: new Date(now.getTime() - 9 * 24 * 3600_000).toISOString(),
      payload: { summary: { turn_count: 1, total_input: 100, total_output: 10 } },
    })
    store.upsertHarnessRollup({
      harness: 'claude-cli',
      sampleCount: 3,
      measurability: { contextPressure: 'measured', toolYield: 'measured' },
    })
    store.upsertHarnessRollup({ harness: 'cursor', sampleCount: 5, measurability: {} })
    store.upsertFinding({
      id: 'finding-claude-schema-residency',
      detectorId: 'dormant-tool-schema',
      title: 'Tool definitions hold 9,000 tokens across every turn',
      mechanism: 'Schemas are resident in every turn.',
      evidenceLinks: [
        { spanId: 'span-claude-1', turnIndex: 1, description: 'resident' },
        { spanId: 'span-claude-2', turnIndex: 2, description: 'resident' },
      ],
      confidence: 'deterministic',
      estimatedWasteTokens: 6400,
      recommendation: 'Move the github server schema behind an on-demand skill.',
      errorBar: { lower: 5200, upper: 7600 },
      outcomeRiskCaveat: 'Relocating schemas adds a load step.',
      sessionId: 'sess-claude',
      rankScore: 87.5,
      measurementClass: 'deterministic',
    })
    store.upsertFinding({
      id: 'finding-codex-duplicate-call',
      detectorId: 'duplicate-tool-call',
      title: 'The same search ran twice with equivalent arguments',
      mechanism: 'Two equivalent calls in one turn.',
      evidenceLinks: [
        { spanId: 'span-codex-1', turnIndex: 2, description: 'first call' },
        { spanId: 'span-codex-2', turnIndex: 3, description: 'second call' },
      ],
      confidence: 'heuristic',
      estimatedWasteTokens: 900,
      recommendation: 'Reuse the earlier result.',
      errorBar: { lower: 700, upper: 1100 },
      outcomeRiskCaveat: 'None observed.',
      sessionId: 'sess-codex',
      rankScore: 41,
      measurementClass: 'deterministic',
    })
    store.close()

    const cli = spawnSync(
      process.execPath,
      [
        '--import', 'tsx', 'src/launcher.ts',
        'report', '--format', 'json', '--db', db, '--days', '7', '--harness', 'claude-cli',
      ],
      {
        cwd: new URL('../..', import.meta.url),
        env: { ...process.env, HOME: home, TZ: 'UTC' },
        encoding: 'utf-8',
        timeout: 30_000,
      },
    )
    expect(cli.status, cli.stderr).toBe(0)
    const cliReport = JSON.parse(cli.stdout) as Record<string, unknown>

    const bridge = new KyberBridge({ canonPath: db })
    bridges.push(bridge)
    const server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    servers.push(server)
    const port = (server.address() as AddressInfo).port
    const response = await fetch(`http://127.0.0.1:${port}/api/kyber/report?days=7&harness=claude-cli`)
    expect(response.status).toBe(200)
    const apiReport = (await response.json()) as Record<string, unknown>

    expect(comparable(cliReport)).toEqual(comparable(apiReport))
  })

  it('keeps DB-backed diagnostics, refresh state, and scoped cost complete on both surfaces', async () => {
    const home = await mkdtemp(join(tmpdir(), 'kyberdash-report-parity-health-'))
    homes.push(home)
    const db = join(home, 'canon.db')
    const sessionId = 'sess-health'
    // Anchored a day back on an hour boundary, so the fixture stays inside the `--days 7`
    // window whenever the suite runs. Fixed dates aged out of it a week after they were written.
    const anchor = Math.floor((Date.now() - 24 * 3600_000) / 3600_000) * 3600_000
    const at = (minutes: number): string => new Date(anchor + minutes * 60_000).toISOString()
    const store = new CanonStore(db)
    store.upsertSession({
      sessionId,
      harness: 'cursor',
      repo: 'kyber-weave',
      started: at(0),
      ended: at(60),
      payload: {
        summary: { turn_count: 1, total_input: 1_000, total_output: 20 },
        context: {
          measurable: true,
          contextLimit: 200_000,
          turns: [{
            index: 1,
            pressure: 0.005,
            buckets: {
              system_prompt: 500,
              tool_definitions: 200,
              instruction_context: 100,
              conversation_history: 150,
              tool_result_content: 50,
            },
            residual: { tokens: 0 },
            toolDefinitionsByServer: {},
          }],
        },
      },
    })

    const records: CanonicalRecord[] = Array.from({ length: 205 }, (_, index) => ({
      spanId: `health-priced-${index}`,
      traceId: `health-trace-${index}`,
      parentSpanId: null,
      source: 'synthetic',
      harness: 'cursor',
      sessionId,
      name: `health record ${index}`,
      op: 'llm.invoke',
      kind: 'client',
      timestamp: at(index % 60),
      durationMs: 10,
      status: 'ok',
      tokens: {
        freshInput: 10,
        cacheRead: 0,
        cacheCreation: 0,
        output: 2,
        reportedInput: 10,
        reportedOutput: 2,
      },
      content: {},
      cost: { basis: 'harness', status: 'priced', value: 0.25, currency: 'USD' },
    }))
    records.push({
      ...records[0]!,
      spanId: 'health-published-1',
      traceId: 'health-published-trace-1',
      cost: { basis: 'published', status: 'priced', value: 0.25, currency: 'USD' },
    })
    records.push({
      ...records[0]!,
      spanId: 'health-published-2',
      traceId: 'health-published-trace-2',
      cost: { basis: 'published', status: 'priced', value: 0.75, currency: 'USD' },
    })
    store.upsertMany(records)

    for (let index = 0; index < 251; index += 1) {
      store.quarantine(`health-quarantine-${index}`, ['synthetic'], 'fixture quarantine')
    }
    for (let index = 0; index < 307; index += 1) {
      store.recordProblem({
        spanId: `health-problem-${index}`,
        severity: 'warning',
        code: 'FIXTURE_PROBLEM',
        message: 'fixture problem',
        location: `health-fixture-${index}`,
      })
    }
    store.startRefreshRun({
      id: 'health-refresh-success',
      startedAt: at(30),
      pid: process.pid,
      trigger: 'cli',
    })
    store.completeRefreshRun(
      'health-refresh-success',
      'success',
      at(45),
      'fixture refresh succeeded',
    )
    store.startRefreshRun({
      id: 'health-refresh-failure',
      startedAt: at(90),
      pid: process.pid,
      trigger: 'scheduled',
    })
    store.completeRefreshRun(
      'health-refresh-failure',
      'failure',
      at(105),
      'fixture refresh failed',
    )
    store.close()

    const cli = spawnSync(
      process.execPath,
      ['--import', 'tsx', 'src/launcher.ts', 'report', '--format', 'json', '--db', db, '--days', '7'],
      {
        cwd: new URL('../..', import.meta.url),
        env: { ...process.env, HOME: home, TZ: 'UTC' },
        encoding: 'utf-8',
        timeout: 30_000,
      },
    )
    expect(cli.status, cli.stderr).toBe(0)
    const cliReport = JSON.parse(cli.stdout) as Record<string, unknown>

    const bridge = new KyberBridge({ canonPath: db })
    bridges.push(bridge)
    const server = await runWebDashboard({ port: 0, open: false, kyberBridge: bridge, writeStdout: () => {} })
    servers.push(server)
    const port = (server.address() as AddressInfo).port
    const response = await fetch(`http://127.0.0.1:${port}/api/kyber/report?days=7`)
    expect(response.status).toBe(200)
    const apiReport = (await response.json()) as Record<string, unknown>

    const expectedCoverage = {
      quarantineCount: 251,
      problemCount: 307,
      refresh: {
        lastSuccessAt: at(45),
        lastFailure: {
          at: at(105),
          summary: 'fixture refresh failed',
        },
        inProgress: null,
      },
    }
    const expectedCost = [
      { basis: 'harness', amountUsd: { value: 51.25, unit: 'USD' } },
      { basis: 'published', amountUsd: { value: 1, unit: 'USD' } },
    ]
    expect(cliReport.coverage).toMatchObject(expectedCoverage)
    expect(apiReport.coverage).toMatchObject(expectedCoverage)
    expect(cliReport.cost).toEqual(expectedCost)
    expect(apiReport.cost).toEqual(expectedCost)
    expect(comparable(cliReport)).toEqual(comparable(apiReport))
  })
})
