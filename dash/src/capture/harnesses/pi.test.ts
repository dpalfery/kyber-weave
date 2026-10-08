// P2.5 — Pi capture writer (status-only; @dpalfery/pi-statusline).
// Reads ~/.pi/agent/settings.json packages read-only, reports which OTLP
// extension is loaded, and warns when both the collector and ObservMe are present.

import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import { ingestBatch } from '../../canon/ingest.js'
import { buildSessionRow } from '../../canon/sessions.js'
import { CanonStore } from '../../canon/store.js'
import { synthesizeCall } from '../../synth/synth.js'
import type { ParsedProviderCall } from '../../providers/types.js'
import type { OtlpSpan } from '../../otel/receiver.js'
import modelsStoreFixture from '../../synth/fixtures/pi-models-store.json' with { type: 'json' }
import { runCapture } from '../index.js'

const COLLECTOR_PACKAGE = '@dpalfery/pi-statusline'
const MIXED_SESSION_ID = 'synthetic-pi-mixed-session'
const DECLARED_WINDOW = 250_000
const QUALIFIED_MODEL = 'synthetic-provider/synthetic-pi-test-model'

const temporaryHomes: string[] = []

const KYBERDASH_LISTENER = async (): Promise<{ status: number; body: string }> => ({
  status: 200,
  body: JSON.stringify({ service: 'kyberdash-otlp', version: '0.0.0-synthetic' }),
})

afterEach(() => {
  for (const home of temporaryHomes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.unstubAllEnvs()
})

function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-pi-capture-'))
  temporaryHomes.push(home)
  return home
}

function piAgentDir(home: string): string {
  return join(home, '.pi', 'agent')
}

function piSettingsPath(home: string): string {
  return join(piAgentDir(home), 'settings.json')
}

function observMePath(home: string): string {
  return join(piAgentDir(home), 'observme.yaml')
}

function writePiSettings(home: string, packages: string[]): void {
  mkdirSync(piAgentDir(home), { recursive: true })
  writeFileSync(piSettingsPath(home), `${JSON.stringify({ packages }, null, 2)}\n`)
}

function writeObservMeConfig(home: string): void {
  mkdirSync(piAgentDir(home), { recursive: true })
  writeFileSync(
    observMePath(home),
    '# synthetic ObservMe fixture\nendpoint: http://127.0.0.1:4318/v1/traces\n',
  )
}

function installModelsStore(home: string): void {
  mkdirSync(piAgentDir(home), { recursive: true })
  writeFileSync(join(piAgentDir(home), 'models-store.json'), `${JSON.stringify(modelsStoreFixture)}\n`)
}

function createPiOtlpSpan(sessionId: string): OtlpSpan {
  const now = Date.now()
  const nanoNow = String(now * 1_000_000)
  return {
    traceId: 'trace-pi-mixed-001',
    spanId: 'span-pi-otlp-001',
    parentSpanId: null,
    name: 'pi.llm.request',
    kind: 'client',
    startTimeUnixNano: nanoNow,
    endTimeUnixNano: String(BigInt(nanoNow) + 100_000_000n),
    timestamp: new Date(now).toISOString(),
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'pi-synthetic-collector' },
    scope: {},
    attributes: {
      'gen_ai.session.id': sessionId,
      'pi.session.id': sessionId,
      'gen_ai.usage.input_tokens': 900,
      'gen_ai.usage.output_tokens': 120,
    },
  }
}

function fileSidePiRecord(sessionId: string): ReturnType<typeof synthesizeCall> {
  const call: ParsedProviderCall = {
    provider: 'pi',
    model: QUALIFIED_MODEL,
    inputTokens: 800,
    outputTokens: 100,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0,
    tools: [],
    bashCommands: [],
    timestamp: '2026-10-01T00:00:02.000Z',
    speed: 'standard',
    deduplicationKey: `pi:${sessionId}:turn-1`,
    userMessage: 'Synthetic pi request.',
    sessionId,
  }
  return synthesizeCall(call, undefined, { parts: [], declaredContextWindow: DECLARED_WINDOW })
}

describe('pi capture writer status (P2.5)', () => {
  it('reports the collector extension when @dpalfery/pi-statusline is in packages', async () => {
    const home = makeHome()
    writePiSettings(home, [COLLECTOR_PACKAGE])

    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['pi'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    const combined = `${result.stdout}\n${result.stderr}`.toLowerCase()
    expect(combined).not.toMatch(/not yet supported/)
    expect(combined).toMatch(/collector|@dpalfery\/pi-statusline/)
  })

  it('reports ObservMe when observme.yaml is present', async () => {
    const home = makeHome()
    writePiSettings(home, [])
    writeObservMeConfig(home)

    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['pi'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    const combined = `${result.stdout}\n${result.stderr}`.toLowerCase()
    expect(combined).not.toMatch(/not yet supported/)
    expect(combined).toMatch(/observme/)
  })

  it('warns when both the collector package and ObservMe are loaded', async () => {
    const home = makeHome()
    writePiSettings(home, [COLLECTOR_PACKAGE])
    writeObservMeConfig(home)

    const result = await runCapture('status', {
      homeDir: home,
      harnessIds: ['pi'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    const combined = `${result.stdout}\n${result.stderr}`.toLowerCase()
    expect(combined).toMatch(/warn/)
    expect(combined).toMatch(/observme/)
    expect(combined).toMatch(/collector|@dpalfery\/pi-statusline/)
  })

  it('enable is status-only and writes no Pi agent config', async () => {
    const home = makeHome()
    writePiSettings(home, [COLLECTOR_PACKAGE])
    const before = readFileSync(piSettingsPath(home), 'utf8')

    const result = await runCapture('enable', {
      homeDir: home,
      harnessIds: ['pi'],
      fetchHealth: KYBERDASH_LISTENER,
    })

    expect(result.exitCode).toBe(0)
    expect(readFileSync(piSettingsPath(home), 'utf8')).toBe(before)
    expect(existsSync(join(home, '.kyberdash', 'capture-receipt.json'))).toBe(false)
  })
})

describe('pi mixed OTLP and file session window (P2.5)', () => {
  it('resolves T7 declared window when OTLP and codeburn/pi rows share gen_ai.session.id', async () => {
    const home = makeHome()
    installModelsStore(home)
    process.env['HOME'] = home

    const store = new CanonStore(':memory:')
    const otlp = createPiOtlpSpan(MIXED_SESSION_ID)
    const ingest = ingestBatch([otlp], store)
    expect(ingest.accepted).toBe(1)

    const fileRecord = {
      ...fileSidePiRecord(MIXED_SESSION_ID),
      source: 'codeburn/pi',
      spanId: 'span-pi-file-001',
      sessionId: MIXED_SESSION_ID,
    }
    store.upsertMany([fileRecord])

    const records = store.recordsForSession(MIXED_SESSION_ID)
    expect(records.length).toBeGreaterThanOrEqual(2)

    const row = buildSessionRow(MIXED_SESSION_ID, records, (text) => text.length)
    const context = row.payload.context as {
      contextLimit?: number
      contextLimitSource?: string
    }
    expect(context.contextLimit).toBe(DECLARED_WINDOW)
    expect(context.contextLimitSource).toBe('declared')

    store.close()
  })
})
