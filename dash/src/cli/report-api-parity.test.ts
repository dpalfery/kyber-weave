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
})
