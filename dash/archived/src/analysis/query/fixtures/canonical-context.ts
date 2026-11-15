import { deflateSync } from 'node:zlib'
import { CanonStore } from '../../../canon/store.js'
import { contentFromParts, type CanonicalRecord, type ContentPart } from '../../../canon/types.js'
import { KyberBridge } from '../../../server/bridge.js'
import { createCanonAnalyticsSource } from '../canon-source.js'
import { createAnalyticsService } from '../service.js'

export type ContextFixture = {
  store: CanonStore
  bridge: KyberBridge
  service: ReturnType<typeof createAnalyticsService>
  setGeneration(generation: string): void
  close(): void
}

export function largeContextText(repetitions: number): string {
  return `LARGE_CONTEXT_BEGIN${'雪🙂é'.repeat(repetitions)}LARGE_CONTEXT_END`
}

export function createContextFixture(options: { largeText?: boolean } = {}): ContextFixture {
  const store = new CanonStore(':memory:')
  const bridge = new KyberBridge({ store })
  let generation = 'projection-1'
  const source = createCanonAnalyticsSource(bridge, { projectionGeneration: () => generation })
  const service = createAnalyticsService({
    source,
    clock: () => new Date('2026-10-25T12:00:00.000Z'),
    identity: { instanceId: 'context-test-instance', projectionGeneration: () => generation },
  })

  const largeText = largeContextText(options.largeText === true ? 260_000 : 30_000)
  const parts: ContentPart[] = [
    { part: 'system_prompt', text: 'SESSION_SHARED_CONTEXT', role: 'system', order: 0 },
    { part: 'conversation_history', text: 'HISTORY_USER_MESSAGE', role: 'user', order: 1 },
    { part: 'conversation_history', text: 'HISTORY_ASSISTANT_REPLY', role: 'assistant', order: 2 },
    { part: 'tool_result_content', text: largeText, role: 'assistant', order: 3 },
    { part: 'conversation_history', text: 'HISTORY_USER_MESSAGE', role: 'user', order: 4 },
  ]
  const spanId = 'context-turn-0'
  const record: CanonicalRecord = {
    spanId,
    traceId: 'context-trace',
    parentSpanId: null,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId: 'context-session',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-24T10:00:00.000Z',
    durationMs: 20,
    status: 'ok',
    tokens: { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 5, reportedInput: 10, reportedOutput: 5 },
    content: contentFromParts(parts),
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
  }
  store.upsertMany([record])
  if (options.largeText === true) {
    let state = 0x1a2b3c4d
    const noise = Buffer.allocUnsafe(3_500_000)
    for (let index = 0; index < noise.length; index += 1) {
      state = (Math.imul(state, 1_664_525) + 1_013_904_223) >>> 0
      noise[index] = 32 + (state % 95)
    }
    const encodedParts = deflateSync(Buffer.from(JSON.stringify([
      parts[0],
      parts[1],
      parts[2],
      {
        part: 'tool_result_content',
        unrelatedPayloadBeforeText: noise.toString('ascii'),
        text: largeText,
        role: 'assistant',
        order: 3,
      },
      parts[4],
    ]), 'utf8'))
    store.getDatabase().prepare('UPDATE records SET parts_json = ? WHERE span_id = ?').run(encodedParts, spanId)
  }
  store.upsertMany([{
    ...record,
    spanId: 'context-shared-prompt',
    name: 'session.start',
    op: 'session.start',
    kind: 'internal',
    parts: [{ part: 'system_prompt', text: 'SHARED_SESSION_START_PROMPT', role: 'system', order: 0 }],
    content: { system_prompt: 'SHARED_SESSION_START_PROMPT' },
  }])
  store.upsertMany([{
    ...record,
    spanId: 'subagent-context-turn',
    sessionId: 'subagent-session',
    parts: [{ part: 'conversation_history', text: 'SUBAGENT_CONTEXT_TEXT', role: 'assistant', order: 0 }],
    content: { conversation_history: 'SUBAGENT_CONTEXT_TEXT' },
  }])
  store.upsertSession({
    sessionId: 'context-session',
    harness: 'claude-code',
    label: 'Context fixture',
    isSubagent: false,
    parentSession: null,
    agentName: 'test-agent',
    repo: 'fixture-repo',
    branch: 'fixture-branch',
    started: '2026-10-24T10:00:00.000Z',
    ended: '2026-10-24T10:01:00.000Z',
    payload: {
      id: 'context-session',
      session_id: 'context-session',
      harness: 'claude-code',
      label: 'Context fixture',
      turns: [
        { index: 0, spanId },
        { index: 3, spanId: 'missing-sparse-turn' },
      ],
    },
  })
  store.upsertSession({
    sessionId: 'subagent-session',
    harness: 'claude-code',
    label: 'Subagent fixture',
    isSubagent: true,
    parentSession: 'context-session',
    agentName: 'worker',
    repo: 'fixture-repo',
    branch: 'fixture-branch',
    started: '2026-10-24T10:00:30.000Z',
    ended: '2026-10-24T10:00:45.000Z',
    payload: {
      id: 'subagent-session',
      session_id: 'subagent-session',
      harness: 'claude-code',
      label: 'Subagent fixture',
      turns: [{ index: 0, spanId: 'subagent-context-turn' }],
    },
  })
  store.upsertRun({ runId: 'context-run', harness: 'claude-code', groupingBasis: 'explicit', groupingRule: 'explicit_run_id' })
  store.upsertExecution({ executionId: 'context-root-execution', runId: 'context-run', sessionId: 'context-session', harness: 'claude-code', agentName: 'test-agent', isRoot: true, parentLinkage: 'measured' })
  store.upsertExecution({ executionId: 'context-child-execution', runId: 'context-run', sessionId: 'subagent-session', parentExecutionId: 'context-root-execution', harness: 'claude-code', agentName: 'worker', isRoot: false, parentLinkage: 'measured' })

  return {
    store,
    bridge,
    service,
    setGeneration(value) { generation = value },
    close() { store.close() },
  }
}
