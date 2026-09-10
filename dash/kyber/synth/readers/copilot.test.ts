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
