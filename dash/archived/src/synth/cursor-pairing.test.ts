// T5 — Cursor turn pairing: the token-bearing bubble of one request must be
// the single carrier of that request's reader parts and context window.
//
// One request has two bubbles: a prompt bubble (identity-bearing `turnId`)
// and a token-bearing reply bubble (pairing-only `pairingId`) whose bubble
// row carries the `contextWindow`. The reader yields one turn for the
// request. Exactly one synthesized `llm.invoke` — the token-bearing one —
// carries the reader parts and the window; the other carries no duplicate
// parts, and span identities stay distinct. `positionalPairingUnsafe` stays
// true for Cursor so id-less calls never steal another turn positionally.

import { describe, expect, it } from 'vitest'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { DatabaseSync } from 'node:sqlite'

import type { ParsedProviderCall } from '../providers/types.js'
import { createCursorProvider } from '../providers/cursor.js'
import { ingestProviders } from './provider.js'
import { cursorReader } from './readers/cursor.js'

const SESSION = 'cursor-pairing-session'
const REQUEST = 'cursor-pairing-req-1'
const WINDOW = 128000

function fixturePath(): string {
  return fileURLToPath(new URL('./fixtures/cursor-pairing.json', import.meta.url))
}

/** A complete Cursor `ParsedProviderCall` for this fixture's request. */
function cursorCall(spec: Partial<ParsedProviderCall> = {}): ParsedProviderCall {
  return {
    provider: 'cursor',
    model: 'gpt-5',
    inputTokens: 0,
    outputTokens: 0,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.001,
    costIsEstimated: true,
    tools: [],
    bashCommands: [],
    timestamp: '2026-09-22T10:03:01.000Z',
    speed: 'standard',
    deduplicationKey: `cursor:${SESSION}:reply`,
    userMessage: 'synthetic pairing reply carrying token usage',
    sessionId: SESSION,
    ...spec,
  }
}

describe('T5 Cursor turn pairing', () => {
  it('pairs the token-bearing bubble as the single carrier of parts and window', async () => {
    // Prompt bubble keeps the identity-bearing turnId; the token-bearing
    // reply bubble carries only the pairing key (no turnId), so span
    // identity still derives from each bubble's own deduplication key.
    const promptCall = cursorCall({
      inputTokens: 120,
      outputTokens: 0,
      timestamp: '2026-09-22T10:03:00.000Z',
      deduplicationKey: `cursor:${SESSION}:prompt`,
      userMessage: 'synthetic pairing prompt about workspace wiring',
      turnId: REQUEST,
    })
    const replyCall = cursorCall({
      inputTokens: 240,
      outputTokens: 60,
      deduplicationKey: `cursor:${SESSION}:reply`,
      pairingId: REQUEST,
    })

    const result = await ingestProviders(['cursor'], () => ({
      calls: [promptCall, replyCall],
      filePath: fixturePath(),
      harnessId: 'cursor',
      sourceKey: `cursor:${SESSION}`,
    }))

    expect(result.problems).toEqual([])
    const invokes = result.records.filter((record) => record.op === 'llm.invoke')
    expect(invokes).toHaveLength(2)

    // Span identities stay distinct: one span per bubble, never collapsed
    // onto the shared request id.
    expect(new Set(invokes.map((record) => record.spanId))).toHaveLength(2)

    // Exactly one invoke carries reader parts.
    const carriers = invokes.filter((record) => (record.parts ?? []).length > 0)
    expect(carriers).toHaveLength(1)

    // The carrier is the token-bearing bubble, with its window.
    const carrier = invokes.find((record) => record.spanId.endsWith(':reply'))!
    expect((carrier.parts ?? []).length).toBeGreaterThan(0)
    expect(carrier.content.instruction_context).toContain('synthetic pairing instruction alpha')
    expect(carrier.content.conversation_history).toContain('synthetic pairing prompt about workspace wiring')
    expect(carrier.content.tool_result_content).toContain('synthetic pairing tool context beta')
    expect((carrier.raw as Record<string, unknown>)['contextWindow']).toBe(WINDOW)

    // The prompt bubble carries no duplicate parts and no window.
    const prompt = invokes.find((record) => record.spanId.endsWith(':prompt'))!
    expect(prompt.parts ?? []).toEqual([])
    expect(prompt.content).toEqual({})
    expect(prompt.raw).not.toHaveProperty('contextWindow')
  })

  it('keeps positional pairing unsafe for Cursor', () => {
    expect(cursorReader.positionalPairingUnsafe).toBe(true)
  })

  it('emits the pairing key on token-bearing Cursor bubbles', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-cursor-pairing-'))
    const dbPath = join(root, 'state.vscdb')
    try {
      const fixture = JSON.parse(readFileSync(fixturePath(), 'utf8')) as {
        rows: Array<{ key: string; value: Record<string, unknown> }>
      }
      const now = new Date().toISOString()
      const db = new DatabaseSync(dbPath)
      try {
        db.exec('CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT NOT NULL)')
        const insert = db.prepare('INSERT INTO cursorDiskKV (key, value) VALUES (?, ?)')
        for (const row of fixture.rows) {
          const value = { ...row.value }
          // Keep bubble rows inside the provider's 6-month coverage window
          // whatever day this runs, so the parse yields both bubbles.
          if (row.key.startsWith('bubbleId:') && typeof value['createdAt'] === 'string') {
            value['createdAt'] = now
          }
          insert.run(row.key, JSON.stringify(value))
        }
      } finally {
        db.close()
      }

      const cursor = createCursorProvider(dbPath)
      const calls: ParsedProviderCall[] = []
      for await (const entry of cursor
        .createSessionParser({ path: dbPath, project: 'fixture-project', provider: 'cursor' }, new Set())
        .parse()) {
        calls.push(entry)
      }

      const prompt = calls.find((entry) => entry.deduplicationKey.endsWith(':prompt'))
      const reply = calls.find((entry) => entry.deduplicationKey.endsWith(':reply'))
      expect(prompt?.turnId).toBe(REQUEST)
      expect(reply?.turnId).toBeUndefined()
      expect(reply?.pairingId).toBe(REQUEST)
      expect(prompt?.pairingId).toBeUndefined()
    } finally {
      rmSync(root, { recursive: true, force: true })
    }
  })
})
