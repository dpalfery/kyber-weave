// Codex content-reader tests. The fixture is inline JSONL in the real
// rollout shape — key names and nesting taken off ~/.codex/sessions — with
// synthetic filler for every body. A real session file must never land in
// the repo: R12.3, git history is permanent, and a tracked-artifact check
// enforces it.

import { mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it } from 'vitest'

import { contentFromParts, type CanonicalContentKey } from '../../canon/types.js'
import { codexReader } from './codex.js'
import type { ReaderTurn } from './types.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

/** A complete synthetic system prompt; the tail is what a byte-cap would drop. */
const SYSTEM_PROMPT =
  'You are a synthetic coding agent used only in tests.\n' +
  'Follow the repository rules. Do not invent token counts.\n' +
  'END_OF_SYNTHETIC_SYSTEM_PROMPT'

const SESSION_ID = '00000000-0000-4000-8000-000000000001'
const CONTEXT_WINDOW = 258400

function line(entry: unknown): string {
  return typeof entry === 'string' ? entry : JSON.stringify(entry)
}

function writeRollout(entries: unknown[]): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-codex-reader-'))
  tempRoots.push(dir)
  const path = join(dir, 'rollout-test.jsonl')
  writeFileSync(path, entries.map(line).join('\n') + '\n')
  return path
}

async function readTurns(path: string): Promise<ReaderTurn[]> {
  const turns: ReaderTurn[] = []
  for await (const turn of codexReader.read(path)) turns.push(turn)
  return turns
}

function bucketsOf(turn: ReaderTurn): CanonicalContentKey[] {
  return [...new Set(turn.parts.map((part) => part.part))]
}

function sessionMeta(overrides: Record<string, unknown> = {}) {
  return {
    type: 'session_meta',
    payload: {
      session_id: SESSION_ID,
      base_instructions: { text: SYSTEM_PROMPT },
      ...overrides,
    },
  }
}

describe('codexReader', () => {
  it('extracts the system prompt whole, without truncating the tail', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 12 } } },
      },
    ])

    const turns = await readTurns(path)
    const system = turns[0]?.parts.find((part) => part.part === 'system_prompt')

    expect(system?.text).toBe(SYSTEM_PROMPT)
    expect(system?.text.endsWith('END_OF_SYNTHETIC_SYSTEM_PROMPT')).toBe(true)
  })

  it('separates tool results from conversation on item type, not role', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'tool',
          content: [{ type: 'input_text', text: 'this is a message even though the role says tool' }],
        },
      },
      {
        type: 'response_item',
        payload: {
          type: 'function_call_output',
          role: 'assistant',
          output: 'file contents from a function_call_output',
        },
      },
      {
        type: 'response_item',
        payload: {
          type: 'custom_tool_call_output',
          output: 'file contents from a custom_tool_call_output',
        },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 40 } } },
      },
    ])

    const turns = await readTurns(path)
    const byText = new Map(turns[0]!.parts.map((part) => [part.text, part.part]))

    expect(byText.get('this is a message even though the role says tool')).toBe(
      'conversation_history',
    )
    expect(byText.get('file contents from a function_call_output')).toBe('tool_result_content')
    expect(byText.get('file contents from a custom_tool_call_output')).toBe('tool_result_content')
  })

  it('never emits tool_definitions, even when the file records tool names', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'response_item',
        payload: { type: 'function_call', name: 'exec_command', arguments: '{"cmd":"ls"}' },
      },
      {
        type: 'response_item',
        payload: { type: 'custom_tool_call', name: 'read_file', input: '{}' },
      },
      {
        type: 'response_item',
        payload: { type: 'function_call_output', output: 'ok' },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 8 } } },
      },
    ])

    const turns = await readTurns(path)
    expect(turns.flatMap((turn) => bucketsOf(turn))).not.toContain('tool_definitions')
    expect(contentFromParts(turns[0]!.parts).tool_definitions).toBeUndefined()
  })

  it('reads the session id and the real context window when present', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'event_msg',
        payload: { type: 'task_started', model_context_window: CONTEXT_WINDOW },
      },
      {
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'user',
          content: [{ type: 'input_text', text: 'hello' }],
        },
      },
      {
        type: 'event_msg',
        payload: {
          type: 'token_count',
          info: {
            model_context_window: CONTEXT_WINDOW,
            last_token_usage: {
              input_tokens: 100,
              output_tokens: 20,
              cached_input_tokens: 10,
              reasoning_output_tokens: 5,
            },
          },
        },
      },
    ])

    const turns = await readTurns(path)
    expect(turns).toHaveLength(1)
    expect(turns[0]?.sessionId).toBe(SESSION_ID)
    expect(turns[0]?.contextWindow).toBe(CONTEXT_WINDOW)
  })

  it('does not treat a missing context window as zero', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 4 } } },
      },
    ])

    const turns = await readTurns(path)
    expect(turns[0]?.contextWindow).toBeUndefined()
    expect('contextWindow' in (turns[0] ?? {})).toBe(false)
  })

  it('skips a malformed or truncated line without throwing', async () => {
    const path = writeRollout([
      sessionMeta(),
      '{"type":"response_item","payload":{"type":"message"',
      'this is not json',
      '',
      {
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'user',
          content: [{ type: 'input_text', text: 'survived the broken lines' }],
        },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 6 } } },
      },
    ])

    await expect(readTurns(path)).resolves.toEqual([
      expect.objectContaining({
        sessionId: SESSION_ID,
        parts: expect.arrayContaining([
          expect.objectContaining({
            part: 'conversation_history',
            text: 'survived the broken lines',
          }),
        ]),
      }),
    ])
  })

  it('leaves instruction_context absent when the file carries no workspace instructions', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'developer',
          content: [{ type: 'input_text', text: '<permissions instructions> sandbox note' }],
        },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 9 } } },
      },
    ])

    const turns = await readTurns(path)
    expect(bucketsOf(turns[0]!)).not.toContain('instruction_context')
    expect(contentFromParts(turns[0]!.parts).instruction_context).toBeUndefined()
    expect(turns[0]!.parts.some((part) => part.part === 'conversation_history')).toBe(true)
  })

  it('emits instruction_context only from stored AGENTS.md text, never from a boolean flag', async () => {
    const agentsMd = '# Synthetic workspace\nUse the test fixture rules.'
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'world_state',
        payload: {
          state: {
            agents_md: { directory: '/tmp/synthetic', text: agentsMd },
            apps_instructions: true,
            environments_instructions: false,
          },
        },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 15 } } },
      },
    ])

    const turns = await readTurns(path)
    const instruction = turns[0]!.parts.filter((part) => part.part === 'instruction_context')

    expect(instruction).toEqual([expect.objectContaining({ text: agentsMd })])
    expect(instruction.some((part) => part.text === 'true' || part.text === 'false')).toBe(false)
  })

  it('keeps prior conversation resident on later turns rather than emitting only the delta', async () => {
    const path = writeRollout([
      sessionMeta(),
      {
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'user',
          content: [{ type: 'input_text', text: 'first question' }],
        },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 20 } } },
      },
      {
        type: 'response_item',
        payload: {
          type: 'message',
          role: 'user',
          content: [{ type: 'input_text', text: 'second question' }],
        },
      },
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 30 } } },
      },
    ])

    const turns = await readTurns(path)
    expect(turns).toHaveLength(2)

    const firstHistory = turns[0]!.parts.filter((part) => part.part === 'conversation_history')
    const secondHistory = turns[1]!.parts.filter((part) => part.part === 'conversation_history')

    expect(firstHistory.map((part) => part.text)).toEqual(['first question'])
    expect(secondHistory.map((part) => part.text)).toEqual(['first question', 'second question'])
    expect(turns[1]!.parts.find((part) => part.part === 'system_prompt')?.text).toBe(SYSTEM_PROMPT)
  })

  it('does not read session_meta.context_window as a token limit', async () => {
    const path = writeRollout([
      sessionMeta({ context_window: { window_id: 'ui-window-not-a-token-limit' } }),
      {
        type: 'event_msg',
        payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 3 } } },
      },
    ])

    const turns = await readTurns(path)
    expect(turns[0]?.contextWindow).toBeUndefined()
  })
})
