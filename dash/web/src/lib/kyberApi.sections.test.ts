// T9 (RED first): the session view's section routes. Each fetcher requests its
// own route with the page, validates the answered payload, and rejects a body
// that is not the documented shape — never a crash, never a default. T3's paged
// envelope validator is the base for the list routes; the documents below are
// built to the validator's shape from the server's serializers.

import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  KYBER_SECTION_PAGE_LIMIT,
  type KyberSectionPageLimit,
  type KyberSectionPageOrder,
  type KyberSectionWindow,
  type KyberSessionChartsSection,
  fetchKyberSessionCharts,
  fetchKyberSessionProblems,
  fetchKyberSessionReconciliation,
  fetchKyberSessionRequests,
  fetchKyberSessionServers,
  fetchKyberSessionKwSkills,
  fetchKyberSessionSkills,
  fetchKyberSessionStoredFields,
  fetchStoredTableRowsPage,
  fetchKyberSessionSummary,
  fetchKyberSessionTimeline,
  fetchKyberSessionTools,
  fetchKyberSessionTurn,
  kyberSessionSectionQueryKey,
} from './kyberApi.js'

type FetchCall = { url: string; init: RequestInit | undefined }

function stubFetch(status: number, body: unknown): FetchCall[] {
  const calls: FetchCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, init })
      return new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      })
    }),
  )
  return calls
}

afterEach(() => {
  vi.unstubAllGlobals()
})

const SUMMARY_BODY = {
  session_id: 'sess-1',
  harness: 'claude-code',
  label: 'Fix the parser',
  is_subagent: false,
  parent_session: null,
  agent_name: 'claude',
  repo: 'kyber-weave',
  branch: 'kw-task/T9',
  started: '2026-11-19T10:00:00.000Z',
  ended: '2026-11-19T11:00:00.000Z',
  span_count: 128,
  problems: 0,
  problems_count: 0,
  request_count: 42,
  context_limit_source: 'session',
  costBasisMismatch: null,
  summary: { turn_count: 128, models: ['claude-sonnet'] },
  notes: ['Skill listings were not exported; only invoked skills are shown.'],
  reconciliation: { total: 128, mismatched: 1 },
  tools_offered_count: 2,
  tools_invoked_count: 1,
  unused_tools_count: 1,
  skills_count: 1,
}

const CHARTS_BODY = {
  sessionId: 'sess-1',
  total: 2,
  page: 1,
  limit: 25,
  offset: 0,
  turns: [
    { index: 0, spanId: 'span-1', input: 900, output: 130, fresh: 50, cache_read: 860 },
    { index: 1, spanId: 'span-2' },
  ],
  context: {
    measurable: true,
    contextLimit: 200_000,
    turns: [{ index: 1, buckets: { system_prompt: 10 } }],
    first: { turn: 1, buckets: {}, reported_input: 900 },
    last: { turn: 2, buckets: {}, reported_input: 1_800 },
    unmeasuredTurns: 0,
  },
}

const TOOL_ROW = {
  name: 'read_file',
  server: null,
  invocations: 4,
  schema_tokens: 120,
  turns_resident: 3,
  total_schema_cost: 500,
  invoked: true,
  in_definitions: true,
  is_mcp: false,
}

const TOOLS_BODY = {
  tools: [TOOL_ROW],
  data: [TOOL_ROW],
  total: 30,
  limit: 25,
  offset: 25,
  page: 2,
  aggregate: {
    maxCost: 500,
    residentCostTotal: 900,
    invocationCount: 12,
    unusedCount: 28,
    unusedRange: { tokenResidencies: 4, floor: 10, ceiling: 40, currency: 'USD' },
    byServer: { 'mcp-a': 300 },
    definitionsSpanId: 'span-defs',
    measurable: true,
    reason: null,
  },
}

const SERVER_ROW = {
  server: 'mcp-a',
  is_mcp: true,
  tools: 3,
  schema_tokens: 300,
  total_schema_cost: 700,
  invocations: 5,
  unused_tools: 1,
  unused_cost: 120,
}

const SERVERS_BODY = {
  servers: [SERVER_ROW],
  data: [SERVER_ROW],
  total: 2,
  limit: 25,
  offset: 0,
  page: 1,
}

const SKILL_BODY = {
  measurable: true,
  rows: [
    {
      name: 'skill-a',
      listed: true,
      invocations: 2,
      turns_listed: [0, 1],
      derived_tokens: 20,
      description: 'Does the thing',
    },
  ],
  total: 1,
  limit: 25,
  offset: 0,
  page: 1,
}

const TIMELINE_BODY = {
  sessionId: 'sess-1',
  parent: null,
  nodes: [
    {
      spanId: 'span-1',
      parentId: null,
      startMs: 0,
      durationMs: 120,
      kind: 'internal',
      name: 'run',
      status: 'ok',
      attributes: {},
      isSubagent: false,
      isAuxiliary: false,
      cost: { basis: 'published', status: 'priced', value: 0.01 },
      childCount: 2,
    },
  ],
  total: 1,
  limit: 25,
  offset: 0,
  page: 1,
  bounds: { startMs: 0, endMs: 120 },
}

const REQUEST_ROW = {
  request: 'span-r1',
  timestamp: '2026-11-19T10:00:00.000Z',
  turns: 2,
  model: 'claude-sonnet',
  prompt: 'hello',
}

const REQUESTS_BODY = {
  requests: [REQUEST_ROW],
  data: [REQUEST_ROW],
  total: 128,
  limit: 25,
  offset: 0,
  page: 1,
}

const RECONCILIATION_ROW = {
  request: 'span-r1',
  root_input: 900,
  sum_chat_input: 900,
  input_match: true,
  root_output: 130,
  sum_chat_output: 130,
  output_match: true,
}

const RECONCILIATION_BODY = {
  reconciliation: [RECONCILIATION_ROW],
  data: [RECONCILIATION_ROW],
  total: 128,
  limit: 25,
  offset: 0,
  page: 1,
}

const PROBLEM_ROW = {
  id: 7,
  spanId: 'span-1',
  severity: 'warning',
  code: 'PARSE_SKIPPED',
  message: 'A record was skipped',
  location: null,
  sessionId: 'sess-1',
  harness: 'claude-code',
  timestamp: '2026-11-19T10:00:00.000Z',
}

const PROBLEMS_BODY = {
  problems: [PROBLEM_ROW],
  data: [PROBLEM_ROW],
  total: 30,
  limit: 25,
  offset: 0,
  page: 1,
}

const STORED_FIELDS_BODY = {
  sessionKey: 'sess-1',
  tables: [
    {
      tableName: 'session',
      columnNames: ['session_id', 'title'],
      rowCount: 1,
      missingParentKeys: [],
    },
  ],
}

const STORED_ROWS_BODY = {
  tableName: 'session',
  columnNames: ['session_id', 'title'],
  totalRows: 30,
  page: 2,
  limit: 25,
  offset: 25,
  rows: [
    {
      cells: [{ columnName: 'session_id', columnType: 1, textValue: 'sess-1' }],
    },
  ],
}

const TURN_BODY = {
  sessionId: 'sess-1',
  turnIndex: 1,
  turn: { index: 1, spanId: 'span-2', input: 900, output: 130, fresh: 50, cache_read: 860 },
  context: {
    index: 2,
    buckets: { system_prompt: 10 },
    toolDefinitionsByServer: {},
    builtinToolDefinitionTokens: 0,
    strippedInstructionBlocks: { count: 0, tokens: 0 },
    bucketedTokens: 10,
    residual: { tokens: 0, attribution: 'none' },
    headroom: 100,
    pressure: 0.1,
    accumulationRate: 0.2,
    freshInput: 50,
    previousFreshInput: 40,
    freshJumpFactor: 1.1,
    nonResidentTokens: 0,
  },
}

describe('session section fetchers', () => {
  it('GETs the bounded summary route and returns the validated document', async () => {
    const calls = stubFetch(200, SUMMARY_BODY)

    const summary = await fetchKyberSessionSummary('sess-1')

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/summary')
    expect(summary).toEqual(SUMMARY_BODY)
  })

  it('rejects a summary body whose reconciliation is not an object', async () => {
    stubFetch(200, { ...SUMMARY_BODY, reconciliation: 'none' })
    await expect(fetchKyberSessionSummary('sess-1')).rejects.toThrow(/malformed/i)
  })

  it('GETs the charts route with paging and sort params and returns the validated turns, window and context', async () => {
    const bodyWithContextSummary = {
      ...CHARTS_BODY,
      order: 'asc' as KyberSectionPageOrder,
      context: {
        ...CHARTS_BODY.context,
        measuredInputTotal: 2_700,
        maxBucketTokens: 1_800,
        observedBuckets: ['system_prompt'],
        observedServers: ['mcp-a'],
        missingBuckets: ['tool_definitions'],
        residualReasons: ['unattributed_framing'],
      },
    }
    const calls = stubFetch(200, bodyWithContextSummary)

    const charts: KyberSessionChartsSection = await fetchKyberSessionCharts('sess-1', 2, 50, 'asc')

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/charts?page=2&limit=50&order=asc')
    const windowCheck: KyberSectionWindow = charts
    expect(windowCheck.total).toBe(2)
    expect(windowCheck.order).toBe('asc')
    expect(charts).toEqual(bodyWithContextSummary)
  })

  it('defaults charts route paging to page=1, limit=25 and order=desc', async () => {
    const calls = stubFetch(200, CHARTS_BODY)

    const charts = await fetchKyberSessionCharts('sess-1')

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/charts?page=1&limit=25&order=desc')
    expect(charts).toEqual(CHARTS_BODY)
  })

  it('rejects a charts body whose context is not a measurable document', async () => {
    stubFetch(200, { ...CHARTS_BODY, context: 'not a context' })
    await expect(fetchKyberSessionCharts('sess-1')).rejects.toThrow(/malformed/i)
  })

  it('keeps the tokenizer label beside the session context and each labelled turn', async () => {
    const labelled = {
      ...CHARTS_BODY,
      context: {
        ...CHARTS_BODY.context,
        tokenizer: 'o200k_base',
        tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
        tokenizerIsModelOwn: false,
        turns: [
          {
            index: 1,
            buckets: { system_prompt: 10 },
            tokenizer: 'o200k_base',
            tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
            tokenizerIsModelOwn: false,
          },
        ],
      },
    }
    stubFetch(200, labelled)

    const charts = await fetchKyberSessionCharts('sess-1')

    expect(charts.context).toMatchObject({
      tokenizer: 'o200k_base',
      tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
      tokenizerIsModelOwn: false,
    })
    if (charts.context.measurable) {
      expect(charts.context.turns[0]).toMatchObject({ tokenizerIsModelOwn: false })
    }
  })

  it('rejects a context figure whose tokenizer label is incomplete', async () => {
    stubFetch(200, {
      ...CHARTS_BODY,
      context: { ...CHARTS_BODY.context, tokenizer: 'o200k_base' },
    })
    await expect(fetchKyberSessionCharts('sess-1')).rejects.toThrow(/incomplete tokenizer label/i)
  })

  it('rejects a context turn whose tokenizer list is empty', async () => {
    stubFetch(200, {
      ...CHARTS_BODY,
      context: {
        ...CHARTS_BODY.context,
        turns: [
          {
            index: 1,
            buckets: { system_prompt: 10 },
            tokenizer: 'mixed',
            tokenizers: [],
            tokenizerIsModelOwn: null,
          },
        ],
      },
    })
    await expect(fetchKyberSessionCharts('sess-1')).rejects.toThrow(/tokenizers is not a non-empty array/i)
  })

  it('rejects a context figure whose mixed label names one tokenizer', async () => {
    stubFetch(200, {
      ...CHARTS_BODY,
      context: {
        ...CHARTS_BODY.context,
        tokenizer: 'mixed',
        tokenizers: [{ name: 'o200k_base', isModelOwn: false }],
        tokenizerIsModelOwn: null,
      },
    })
    await expect(fetchKyberSessionCharts('sess-1')).rejects.toThrow(/disagrees with its list/i)
  })

  it('GETs the tools route with the page and returns the page and its aggregate', async () => {
    const calls = stubFetch(200, TOOLS_BODY)

    const page = await fetchKyberSessionTools('sess-1', 2)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/tools?page=2&limit=25&order=desc')
    expect(page).toMatchObject({ total: 30, page: 2, limit: 25, offset: 25, aggregate: TOOLS_BODY.aggregate })
    expect(page.entries).toEqual([TOOL_ROW])
  })

  it('threads custom limit and order across section fetchers', async () => {
    const customLimit: KyberSectionPageLimit = 50
    const customOrder: KyberSectionPageOrder = 'asc'
    const calls = stubFetch(200, { ...TOOLS_BODY, limit: customLimit, order: customOrder })

    const page = await fetchKyberSessionTools('sess-1', 2, customLimit, customOrder)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/tools?page=2&limit=50&order=asc')
    expect(page.order).toBe('asc')
  })

  it('rejects a tools page whose order is not desc or asc', async () => {
    stubFetch(200, { ...TOOLS_BODY, order: 'sideways' })
    await expect(fetchKyberSessionTools('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('rejects a tools page whose aggregate is missing', async () => {
    stubFetch(200, { ...TOOLS_BODY, aggregate: null })
    await expect(fetchKyberSessionTools('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs the servers route with the page and returns the validated rows', async () => {
    const calls = stubFetch(200, SERVERS_BODY)

    const page = await fetchKyberSessionServers('sess-1', 1)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/servers?page=1&limit=25&order=desc')
    expect(page.entries).toEqual([SERVER_ROW])
    expect(page.total).toBe(2)
  })

  it('rejects a servers page without the paged envelope members', async () => {
    stubFetch(200, { servers: [SERVER_ROW] })
    await expect(fetchKyberSessionServers('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs the skills route with the page and returns the paged skill block', async () => {
    const calls = stubFetch(200, SKILL_BODY)

    const page = await fetchKyberSessionSkills('sess-1', 1)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/skills?page=1&limit=25&order=desc')
    expect(page).toEqual(SKILL_BODY)
  })

  it('rejects a skills body whose rows is not an array', async () => {
    stubFetch(200, { measurable: true, rows: 'none', total: 0, page: 1, limit: 25, offset: 0 })
    await expect(fetchKyberSessionSkills('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs the kw-skills route with the page and returns the paged KW skill rows', async () => {
    const body = {
      rows: [
        {
          skill: 'kw-docs',
          agent: 'claude',
          session_id: 'sess-1',
          span_id: 'span-1',
          loaded_at: '2026-11-19T10:05:00.000Z',
        },
        { skill: null, agent: null, session_id: 'sess-1', span_id: 'span-2', loaded_at: null },
      ],
      total: 2,
      limit: 25,
      offset: 0,
      page: 1,
    }
    const calls = stubFetch(200, body)

    const page = await fetchKyberSessionKwSkills('sess-1', 1)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/kw-skills?page=1&limit=25&order=desc')
    expect(page).toEqual(body)
  })

  it('rejects a kw-skills body whose rows is not an array', async () => {
    stubFetch(200, { rows: 'none', total: 0, page: 1, limit: 25, offset: 0 })
    await expect(fetchKyberSessionKwSkills('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('rejects a kw-skills row that is not an object', async () => {
    stubFetch(200, { rows: ['kw-docs'], total: 1, page: 1, limit: 25, offset: 0 })
    await expect(fetchKyberSessionKwSkills('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('rejects a kw-skills row whose session or span id is empty', async () => {
    const row = { skill: 'kw-docs', agent: 'claude', session_id: 'sess-1', span_id: 'span-1', loaded_at: null }
    stubFetch(200, { rows: [{ ...row, session_id: ' ' }], total: 1, page: 1, limit: 25, offset: 0 })
    await expect(fetchKyberSessionKwSkills('sess-1', 1)).rejects.toThrow(/malformed/i)
    stubFetch(200, { rows: [{ ...row, span_id: '' }], total: 1, page: 1, limit: 25, offset: 0 })
    await expect(fetchKyberSessionKwSkills('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('rejects a kw-skills body without the paging window', async () => {
    stubFetch(200, { rows: [], total: 0 })
    await expect(fetchKyberSessionKwSkills('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs one timeline level with the parent, page, limit and order', async () => {
    const calls = stubFetch(200, TIMELINE_BODY)

    const page = await fetchKyberSessionTimeline('sess-1', { parent: '(auxiliary)', page: 2 })

    expect(calls[0]!.url).toBe(
      '/api/kyber/session/sess-1/timeline?page=2&limit=25&order=desc&parent=%28auxiliary%29',
    )
    expect(page).toEqual(TIMELINE_BODY)
  })

  it('GETs one timeline level with custom limit and order', async () => {
    const calls = stubFetch(200, TIMELINE_BODY)

    await fetchKyberSessionTimeline('sess-1', { parent: '(auxiliary)', page: 2, limit: 50, order: 'asc' })

    expect(calls[0]!.url).toBe(
      '/api/kyber/session/sess-1/timeline?page=2&limit=50&order=asc&parent=%28auxiliary%29',
    )
  })

  it('rejects a timeline body without the tree bounds', async () => {
    stubFetch(200, { ...TIMELINE_BODY, bounds: null })
    await expect(fetchKyberSessionTimeline('sess-1')).rejects.toThrow(/malformed/i)
  })

  it('GETs the requests route with the page and encodes the session id', async () => {
    const calls = stubFetch(200, REQUESTS_BODY)

    const page = await fetchKyberSessionRequests('sess/a b', 3, 100, 'asc')

    expect(calls[0]!.url).toBe('/api/kyber/session/sess%2Fa%20b/requests?page=3&limit=100&order=asc')
    expect(page.entries).toEqual([REQUEST_ROW])
    expect(page.total).toBe(128)
  })

  it('rejects a requests page whose row is not an object', async () => {
    stubFetch(200, { ...REQUESTS_BODY, requests: ['not a row'] })
    await expect(fetchKyberSessionRequests('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('carries the projection status a faulted session page answers', async () => {
    stubFetch(200, { ...REQUESTS_BODY, projection_status: 'PROJECTION_FAILED' })

    const page = await fetchKyberSessionRequests('sess-1', 1)

    expect(page.projection_status).toBe('PROJECTION_FAILED')
  })

  it('GETs the reconciliation route with the page and returns the validated rows', async () => {
    const calls = stubFetch(200, RECONCILIATION_BODY)

    const page = await fetchKyberSessionReconciliation('sess-1', 1)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/reconciliation?page=1&limit=25&order=desc')
    expect(page.entries).toEqual([RECONCILIATION_ROW])
  })

  it('rejects a reconciliation row whose match flag is not a boolean', async () => {
    stubFetch(200, { ...RECONCILIATION_BODY, reconciliation: [{ ...RECONCILIATION_ROW, output_match: 'yes' }] })
    await expect(fetchKyberSessionReconciliation('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs the problems route with the page and returns the validated rows', async () => {
    const calls = stubFetch(200, PROBLEMS_BODY)

    const page = await fetchKyberSessionProblems('sess-1', 1)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/problems?page=1&limit=25&order=desc')
    expect(page.entries).toEqual([PROBLEM_ROW])
  })

  it('rejects a problems row without a string severity', async () => {
    stubFetch(200, { ...PROBLEMS_BODY, problems: [{ ...PROBLEM_ROW, severity: 3 }] })
    await expect(fetchKyberSessionProblems('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs the stored-fields route and returns the inventory without rows', async () => {
    const calls = stubFetch(200, STORED_FIELDS_BODY)

    const stored = await fetchKyberSessionStoredFields('sess-1')

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/stored-fields')
    expect(stored).toEqual(STORED_FIELDS_BODY)
  })

  it('rejects a stored-fields body whose tables is not an array', async () => {
    stubFetch(200, { sessionKey: 'sess-1', tables: 'none' })
    await expect(fetchKyberSessionStoredFields('sess-1')).rejects.toThrow(/malformed/i)
  })

  it('rejects a stored-fields table without its row count', async () => {
    stubFetch(200, {
      sessionKey: 'sess-1',
      tables: [{ tableName: 'session', columnNames: ['session_id'], missingParentKeys: [] }],
    })
    await expect(fetchKyberSessionStoredFields('sess-1')).rejects.toThrow(/malformed/i)
  })

  it('GETs one stored-fields rows page with the table URL-encoded and the page', async () => {
    const calls = stubFetch(200, STORED_ROWS_BODY)

    const page = await fetchStoredTableRowsPage('sess/one', 'session', 2)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess%2Fone/stored-fields/session?page=2&limit=25')
    expect(page.rows).toEqual(STORED_ROWS_BODY.rows)
    expect(page.totalRows).toBe(30)
    expect(page.tableName).toBe('session')
  })

  it('rejects a rows page whose rows is not an array', async () => {
    stubFetch(200, { tableName: 'session', columnNames: [], totalRows: 0, page: 1, limit: 25, offset: 0, rows: 'no' })
    await expect(fetchStoredTableRowsPage('sess-1', 'session', 1)).rejects.toThrow(/malformed/i)
  })

  it('GETs the widened turn route and returns the turn with its context', async () => {
    const calls = stubFetch(200, TURN_BODY)

    const section = await fetchKyberSessionTurn('sess-1', 1)

    expect(calls[0]!.url).toBe('/api/kyber/session/sess-1/turn/1')
    expect(section).toEqual(TURN_BODY)
  })

  it('rejects a turn body without the turn row', async () => {
    stubFetch(200, { ...TURN_BODY, turn: null })
    await expect(fetchKyberSessionTurn('sess-1', 1)).rejects.toThrow(/malformed/i)
  })

  it('reports a non-2xx section answer as a KyberApiError carrying the route', async () => {
    stubFetch(404, { error: 'Session not found' })
    await expect(fetchKyberSessionSummary('sess-missing')).rejects.toMatchObject({
      status: 404,
      path: '/api/kyber/session/sess-missing/summary',
    })
  })
})

describe('kyberSessionSectionQueryKey', () => {
  it('keys a section under the session with its page, limit and order', () => {
    expect(kyberSessionSectionQueryKey('sess-1', 'tools', 2)).toEqual([
      'kyber-session',
      'sess-1',
      'tools',
      2,
      25,
      'desc',
    ])
    expect(kyberSessionSectionQueryKey('sess-1', 'tools', 2, 50, 'asc')).toEqual([
      'kyber-session',
      'sess-1',
      'tools',
      2,
      50,
      'asc',
    ])
  })

  it('defaults the page to one, limit to 25 and order to desc', () => {
    expect(kyberSessionSectionQueryKey('sess-1', 'summary')).toEqual([
      'kyber-session',
      'sess-1',
      'summary',
      1,
      25,
      'desc',
    ])
    expect(KYBER_SECTION_PAGE_LIMIT).toBe(25)
  })
})
