// pi can supply file-derived transcript parts while its configured collector
// supplies OTel counters. D7 selects a source per field; it never adds both.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it, expectTypeOf } from 'vitest'

import type { ParsedProviderCall } from '../../../src/providers/types.js'
import { contentFromParts, type CanonicalRecord } from '../../canon/types.js'
import { joinOtelAndFileTurn } from '../dedup.js'
import { synthesizeCall } from '../synth.js'
import { piReader } from './pi.js'
import type { ContentReader, ReaderTurn } from './types.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

function writePiSession(entries: unknown[]): string {
  const root = mkdtempSync(join(tmpdir(), 'kyber-pi-reader-'))
  tempRoots.push(root)
  const path = join(root, 'synthetic-pi-session.jsonl')
  writeFileSync(path, entries.map((entry) => JSON.stringify(entry)).join('\n') + '\n')
  return path
}

async function readTurns(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of piReader.read(path)) turns.push(turn)
  return turns
}

function record(source: string, spanId: string): CanonicalRecord {
  return {
    ...synthesizeCall({
      provider: 'pi',
      model: 'synthetic/pi',
      inputTokens: 99,
      outputTokens: 17,
      cacheCreationInputTokens: 3,
      cacheReadInputTokens: 20,
      cachedInputTokens: 20,
      reasoningTokens: 0,
      webSearchRequests: 0,
      costUSD: 0,
      tools: [],
      bashCommands: [],
      timestamp: '2026-09-04T00:00:00.000Z',
      speed: 'standard',
      deduplicationKey: 'pi:synthetic:turn-1',
      userMessage: '',
      sessionId: 'synthetic-pi-session',
    } satisfies ParsedProviderCall),
    source,
    spanId,
  }
}

describe('piReader', () => {
  it('is exposed through the public ContentReader contract', () => {
    expectTypeOf(piReader).toMatchTypeOf<ContentReader>()
  })

  it('uses file parts only when the OTel turn has none, retaining OTel counters', async () => {
    const [fileTurn] = await readTurns(writePiSession([
      { type: 'session', id: 'synthetic-pi-session', timestamp: '2026-09-04T00:00:00.000Z' },
      {
        type: 'message',
        timestamp: '2026-09-04T00:00:01.000Z',
        message: { role: 'user', content: [{ type: 'text', text: 'Synthetic pi request.' }] },
      },
    ]))
    expect(fileTurn?.parts).toEqual(expect.arrayContaining([
      expect.objectContaining({ part: 'conversation_history', text: 'Synthetic pi request.' }),
    ]))

    const file = {
      ...record('codeburn/pi', 'synth-pi-turn'),
      parts: fileTurn!.parts,
      content: contentFromParts(fileTurn!.parts),
    }
    const otel = record('pi-otel', 'otel-pi-turn')

    const joined = joinOtelAndFileTurn(otel, file)

    expect(joined.tokens).toEqual(otel.tokens)
    expect(joined.parts).toEqual(file.parts)
    expect(joined.content).toEqual(file.content)
  })

  it('keeps OTel parts when both sources describe the same pi turn', async () => {
    const [fileTurn] = await readTurns(writePiSession([
      { type: 'session', id: 'synthetic-pi-session', timestamp: '2026-09-04T00:00:00.000Z' },
      {
        type: 'message',
        timestamp: '2026-09-04T00:00:01.000Z',
        message: { role: 'user', content: [{ type: 'text', text: 'File-only synthetic content.' }] },
      },
    ]))
    const file = {
      ...record('codeburn/pi', 'synth-pi-turn'),
      parts: fileTurn!.parts,
      content: contentFromParts(fileTurn!.parts),
    }
    const otel = {
      ...record('pi-otel', 'otel-pi-turn'),
      parts: [{ part: 'conversation_history' as const, text: 'OTel synthetic content.', order: 0 }],
      content: { conversation_history: 'OTel synthetic content.' },
    }

    const joined = joinOtelAndFileTurn(otel, file)

    expect(joined.tokens).toEqual(otel.tokens)
    expect(joined.parts).toEqual(otel.parts)
    expect(joined.content).toEqual(otel.content)
  })
})
