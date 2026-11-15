import { tmpdir } from 'os'
import { join } from 'path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'
import { KyberBridge } from './bridge.js'

// The content route (GET /api/kyber/session/:id/content) is what the Tool drawer
// reads for a tool's definition. A part the turn offered without keeping
// resident (an MCP catalogue entry) is marked `resident: false` on the stored
// ContentPart. That mark must reach the route response, or the drawer has no way
// to label the definition as offered rather than resident.

const SESSION_ID = 'sess-resident-001'
const SPAN_RESIDENT = 'span-resident-tools'
const SPAN_NOT_RESIDENT = 'span-catalogue-tools'
const BUILT_IN_DEFINITION = '{"name":"read_file","description":"Read a file from the workspace"}'
const MCP_DEFINITION = '{"name":"github_search","description":"Search repositories"}'

function tokens() {
  return {
    freshInput: 10,
    cacheRead: 0,
    cacheCreation: 0,
    output: 1,
    reportedInput: 10,
    reportedOutput: 1,
  }
}

function turn(spanId: string, parts: ContentPart[], timestamp: string): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-resident-001',
    parentSpanId: null,
    source: 'antigravity',
    harness: 'antigravity',
    sessionId: SESSION_ID,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp,
    durationMs: 100,
    status: 'ok',
    tokens: tokens(),
    content: {},
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

describe('content route: residency of tool definitions', () => {
  let store: CanonStore | undefined

  afterEach(() => {
    store?.close()
    store = undefined
  })

  function bridgeWithTurns(): KyberBridge {
    store = new CanonStore(':memory:')
    store.upsertMany([
      turn(
        SPAN_RESIDENT,
        [{ part: 'tool_definitions', text: BUILT_IN_DEFINITION, tokens: 12, server: 'built-in' }],
        '2026-10-21T09:00:00.000Z',
      ),
      turn(
        SPAN_NOT_RESIDENT,
        [{ part: 'tool_definitions', text: MCP_DEFINITION, tokens: 9, server: 'github', resident: false }],
        '2026-10-21T09:01:00.000Z',
      ),
    ])
    return new KyberBridge({
      store,
      canonDb: new DatabaseSync(':memory:'),
      ratesPath: join(tmpdir(), 'nonexistent-rates.json'),
    })
  }

  it('keeps resident:false on a non-resident tool_definitions part in the content response', () => {
    // RED: getSessionContent rebuilds each entry from spanId, part, text, tokens
    // and server only, so the residency mark is dropped before the route answers.
    const bridge = bridgeWithTurns()

    const result = bridge.getSessionContent(SESSION_ID, { spanId: SPAN_NOT_RESIDENT, part: 'tool_definitions' })

    expect(result).not.toBeNull()
    expect(result!.parts).toHaveLength(1)
    expect(result!.parts[0].text).toBe(MCP_DEFINITION)
    // SessionContentPart has no `resident` member yet; the read is typed here so
    // the failure names the missing behaviour, not a compile error.
    expect((result!.parts[0] as { resident?: unknown }).resident).toBe(false)
  })

  it('leaves residency absent on a resident tool definition, so it is never read as offered', () => {
    // Guard: a normal definition carries no residency mark on the route.
    const bridge = bridgeWithTurns()

    const result = bridge.getSessionContent(SESSION_ID, { spanId: SPAN_RESIDENT, part: 'tool_definitions' })

    expect(result).not.toBeNull()
    expect(result!.parts).toHaveLength(1)
    expect(result!.parts[0]).not.toHaveProperty('resident')
  })
})
