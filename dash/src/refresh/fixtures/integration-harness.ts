// Sanitized fixture loaders for T7 refresh integration tests.
// JSONL and a virtual SQLite session store — no live user history.

import { copyFileSync, mkdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { fileURLToPath } from 'node:url'

import type { ParsedProviderCall, Provider, SessionSource } from '../../providers/types.js'

export const FIXTURE_DIR = dirname(fileURLToPath(import.meta.url))

export const COMMAND_STARTED_AT = new Date('2026-09-12T18:00:00.000Z')

export type FixtureTurn = {
  sessionId: string
  turnId: string
  timestamp: string
  model: string
  inputTokens?: number
  outputTokens?: number
  provider?: string
}

export function fixturePath(name: string): string {
  return join(FIXTURE_DIR, name)
}

export function copyFixture(name: string, destination: string): string {
  mkdirSync(dirname(destination), { recursive: true })
  copyFileSync(fixturePath(name), destination)
  return destination
}

export function parsedCall(turn: FixtureTurn, provider: string): ParsedProviderCall {
  const identity = turn.provider ?? provider
  return {
    provider: identity,
    model: turn.model,
    inputTokens: turn.inputTokens ?? 10,
    outputTokens: turn.outputTokens ?? 4,
    cacheCreationInputTokens: 0,
    cacheReadInputTokens: 0,
    cachedInputTokens: 0,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0,
    tools: [],
    bashCommands: [],
    timestamp: turn.timestamp,
    speed: 'standard',
    deduplicationKey: `${identity}:${turn.sessionId}:${turn.turnId}`,
    userMessage: `fixture ${turn.turnId}`,
    sessionId: turn.sessionId,
    turnId: turn.turnId,
  }
}

export function loadJsonlTurns(path: string): FixtureTurn[] {
  const turns: FixtureTurn[] = []
  for (const line of readFileSync(path, 'utf8').split('\n')) {
    const trimmed = line.trim()
    if (trimmed === '') continue
    const parsed: unknown = JSON.parse(trimmed)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) continue
    const record = parsed as Record<string, unknown>
    if (typeof record['timestamp'] !== 'string' || typeof record['sessionId'] !== 'string') continue
    if (typeof record['turnId'] !== 'string') continue
    turns.push({
      sessionId: record['sessionId'],
      turnId: record['turnId'],
      timestamp: record['timestamp'],
      model: typeof record['model'] === 'string' ? record['model'] : 'test-model',
      provider: typeof record['provider'] === 'string' ? record['provider'] : undefined,
    })
  }
  return turns
}

export function loadCursorVirtualTurns(): FixtureTurn[] {
  const parsed: unknown = JSON.parse(readFileSync(fixturePath('cursor-virtual-turns.json'), 'utf8'))
  if (!Array.isArray(parsed)) throw new Error('cursor-virtual-turns.json must be an array')
  return parsed as FixtureTurn[]
}

export function writeCursorVirtualDb(destination: string, turns: readonly FixtureTurn[] = loadCursorVirtualTurns()): string {
  mkdirSync(dirname(destination), { recursive: true })
  const db = new DatabaseSync(destination)
  try {
    db.exec(`
      CREATE TABLE turns (
        session_id TEXT NOT NULL,
        turn_id TEXT NOT NULL,
        timestamp TEXT NOT NULL,
        model TEXT NOT NULL,
        input_tokens INTEGER NOT NULL,
        output_tokens INTEGER NOT NULL
      );
    `)
    const insert = db.prepare(
      'INSERT INTO turns (session_id, turn_id, timestamp, model, input_tokens, output_tokens) VALUES (?, ?, ?, ?, ?, ?)',
    )
    for (const turn of turns) {
      insert.run(
        turn.sessionId,
        turn.turnId,
        turn.timestamp,
        turn.model,
        turn.inputTokens ?? 40,
        turn.outputTokens ?? 12,
      )
    }
  } finally {
    db.close()
  }
  return destination
}

export function loadSqliteTurns(path: string): FixtureTurn[] {
  const db = new DatabaseSync(path, { readOnly: true })
  try {
    const rows = db.prepare(
      'SELECT session_id, turn_id, timestamp, model, input_tokens, output_tokens FROM turns ORDER BY timestamp',
    ).all() as Array<{
      session_id: string
      turn_id: string
      timestamp: string
      model: string
      input_tokens: number
      output_tokens: number
    }>
    return rows.map((row) => ({
      sessionId: row.session_id,
      turnId: row.turn_id,
      timestamp: row.timestamp,
      model: row.model,
      inputTokens: row.input_tokens,
      outputTokens: row.output_tokens,
    }))
  } finally {
    db.close()
  }
}

export function fixtureProvider(
  name: string,
  sources: readonly SessionSource[],
  loader: (source: SessionSource) => readonly ParsedProviderCall[] | Error,
): Provider {
  return {
    name,
    displayName: name,
    modelDisplayName: (model) => model,
    toolDisplayName: (tool) => tool,
    discoverSessions: async () => [...sources],
    createSessionParser(source) {
      return {
        async *parse(): AsyncGenerator<ParsedProviderCall> {
          const parsed = loader(source)
          if (parsed instanceof Error) throw parsed
          yield* parsed
        },
      }
    },
  }
}

export function jsonlLoader(source: SessionSource): ParsedProviderCall[] {
  return loadJsonlTurns(source.path).map((turn) => parsedCall(turn, source.provider))
}

export function sqliteLoader(source: SessionSource): ParsedProviderCall[] {
  return loadSqliteTurns(source.path).map((turn) => parsedCall(turn, source.provider))
}
