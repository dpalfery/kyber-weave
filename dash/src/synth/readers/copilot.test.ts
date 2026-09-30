// Copilot CLI's data.db records ASAD's context taxonomy directly. These
// synthetic rows contain only neutral labels and counts, never CLI history.
import { describe, expect, it, expectTypeOf } from 'vitest'
import { DatabaseSync } from 'node:sqlite'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import {
  copilotCliReader,
  loadCopilotCliCalls,
  parseCopilotCliContextRow,
} from './copilot.js'
import type { ContentReader } from './types.js'

const CLI_CONTEXT_ROW = {
  id: 'synthetic-copilot-cli-session',
  context_system_tokens: 120,
  context_conversation_tokens: 340,
  context_tool_definitions_tokens: 560,
  context_mcp_tools_tokens: 780,
  context_buffer_tokens: 90,
  context_tier: 'standard',
}

describe('copilotCliReader', () => {
  it('is exposed through the public ContentReader contract', () => {
    expectTypeOf(copilotCliReader).toMatchTypeOf<ContentReader>()
  })

  it('preserves Copilot CLI’s reported ASAD taxonomy without collapsing buckets', () => {
    expect(parseCopilotCliContextRow(CLI_CONTEXT_ROW)).toEqual({
      context_system_tokens: 120,
      context_conversation_tokens: 340,
      context_tool_definitions_tokens: 560,
      context_mcp_tools_tokens: 780,
      context_buffer_tokens: 90,
      context_tier: 'standard',
    })
  })

  it('does not turn an omitted reported bucket into zero', () => {
    const { context_buffer_tokens: _omitted, ...withoutBuffer } = CLI_CONTEXT_ROW

    expect(parseCopilotCliContextRow(withoutBuffer)).not.toHaveProperty('context_buffer_tokens')
  })
})

describe('loadCopilotCliCalls', () => {
  it('loads a synthetic SQLite session row without adding omitted taxonomy buckets', () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-cli-'))
    const filePath = join(root, 'data.db')
    const db = new DatabaseSync(filePath)
    try {
      db.exec(`
        CREATE TABLE sessions (
          id TEXT,
          session_id TEXT,
          model TEXT,
          created_at TEXT,
          context_system_tokens INTEGER,
          context_conversation_tokens INTEGER,
          context_tier TEXT
        );
        INSERT INTO sessions VALUES (
          'synthetic-row', 'synthetic-session', 'gpt-5',
          '2026-09-04T12:00:00.000Z', 120, 340, 'standard'
        );
      `)

      expect(loadCopilotCliCalls(filePath)).toMatchObject([{
        provider: 'copilot',
        sessionId: 'synthetic-session',
        context_system_tokens: 120,
        context_conversation_tokens: 340,
        context_tier: 'standard',
      }])
      expect(loadCopilotCliCalls(filePath)[0]).not.toHaveProperty('context_buffer_tokens')
    } finally {
      db.close()
      rmSync(root, { recursive: true, force: true })
    }
  })
})

describe('loadCopilotCliCalls: reader-supplied cost_usd stays harness-reported (issue #186, R5.2)', () => {
  it('a genuine reader figure is carried verbatim on the harness basis', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-copilot-cli-cost-'))
    const filePath = join(root, 'data.db')
    const db = new DatabaseSync(filePath)
    try {
      db.exec(`
        CREATE TABLE sessions (id TEXT, session_id TEXT, model TEXT, created_at TEXT, cost_usd REAL,
          input_tokens INTEGER, output_tokens INTEGER);
        INSERT INTO sessions VALUES ('cost-row', 'cost-session', 'claude-sonnet-5-5',
          '2026-09-04T12:00:00.000Z', 0.42, 1000, 200);
      `)
      const [call] = loadCopilotCliCalls(filePath)
      expect(call!.costUSD).toBe(0.42)
      const { synthesizeCall } = await import('../synth.js')
      const cost = synthesizeCall(call!).cost
      expect(cost.basis).toBe('harness')
      expect(cost.value).toBe(0.42)
    } finally {
      db.close()
      rmSync(root, { recursive: true, force: true })
    }
  })
})
