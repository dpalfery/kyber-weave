// Multi-turn OTLP trace and log payload builders (two sessions of 10+ turns per harness) and
// expected-vs-gap dashboard contracts for all 7 stored OTLP harnesses plus the excluded `gemini` check.

export type StoredOtlpHarnessId =
  | 'claude-code'
  | 'opencode'
  | 'droid'
  | 'codex'
  | 'copilot'
  | 'pi'
  | 'antigravity'

export type AllOtlpHarnessId = StoredOtlpHarnessId | 'gemini'

export type OtlpAttributeValue =
  | { readonly stringValue: string }
  | { readonly intValue: string }
  | { readonly doubleValue: number }
  | { readonly boolValue: boolean }
  | { readonly arrayValue: { readonly values: readonly OtlpAttributeValue[] } }

export type OtlpKeyValue = {
  readonly key: string
  readonly value: OtlpAttributeValue
}

export type OtlpSpanWire = {
  readonly traceId: string
  readonly spanId: string
  readonly parentSpanId?: string
  readonly name: string
  readonly kind: string
  readonly startTimeUnixNano: string
  readonly endTimeUnixNano: string
  readonly status: { readonly code: string; readonly message?: string }
  readonly attributes: readonly OtlpKeyValue[]
}

export type OtlpExportTraceServiceRequest = {
  readonly resourceSpans: readonly [
    {
      readonly resource: { readonly attributes: readonly OtlpKeyValue[] }
      readonly scopeSpans: readonly [
        {
          readonly scope: { readonly name: string; readonly version: string }
          readonly spans: readonly OtlpSpanWire[]
        },
      ]
    },
  ]
}

export type OtlpLogRecordWire = {
  readonly timeUnixNano: string
  readonly traceId?: string
  readonly spanId?: string
  readonly eventName?: string
  readonly body?: { readonly stringValue: string }
  readonly attributes: readonly OtlpKeyValue[]
}

export type OtlpExportLogsServiceRequest = {
  readonly resourceLogs: readonly [
    {
      readonly resource: { readonly attributes: readonly OtlpKeyValue[] }
      readonly scopeLogs: readonly [
        {
          readonly scope: { readonly name: string; readonly version: string }
          readonly logRecords: readonly OtlpLogRecordWire[]
        },
      ]
    },
  ]
}

export type HarnessExpectationContract = {
  readonly harness: StoredOtlpHarnessId
  readonly sessionId: string
  readonly turns: number
  readonly expectedToolCalls: number | null
  readonly tokensMeasurable: boolean
  readonly cacheCreationMeasurable: boolean
  readonly contextMeasurable: boolean
  readonly schemaMeasurable: boolean
  readonly skillsMeasurable: boolean
  /** Whether the engine draws one measured bar per turn, or the not-measurable chart state. */
  readonly perTurnTokensMeasurable: boolean
  /** Whether the session pairs with transcript + companion files read from the server's home on refresh. */
  readonly pairedWithSessionFiles: boolean
  readonly expectedSkills: readonly string[]
  readonly expectedCostStatus: 'harness' | 'published_priced' | 'out_of_scope' | 'no_rate'
  readonly expectedModels: readonly string[]
  readonly expectedGapSubstrings: readonly string[]
  readonly sendsLogs: boolean
}

export type HarnessOtlpFixture = {
  readonly harness: AllOtlpHarnessId
  readonly sessionId: string
  readonly turns: number
  readonly tracePayload: OtlpExportTraceServiceRequest
  readonly logPayload: OtlpExportLogsServiceRequest | null
}

/** One paired Droid session's transcript and companion, with the paths they live at under the server's home. */
export type DroidSessionFilePair = {
  /** Relative path of the transcript under the server's home directory. */
  readonly transcriptPath: string
  /** Relative path of the companion settings under the server's home directory. */
  readonly companionPath: string
  readonly transcript: string
  readonly companion: string
}

export const GEMINI_EXCLUDED_SESSION_ID = 'otel-e2e-gemini-excluded'

// The base time is one hour before this run, floored to the minute (decision D10), so the
// 14-day retention window and the refresh history window never age the fixtures out, whatever
// day the suite runs on.
const RUN_MINUTE_MS = Math.floor(Date.now() / 60_000) * 60_000
const BASE_TIME_MS = RUN_MINUTE_MS - 3_600_000
const BASE_TIME_NS = BigInt(BASE_TIME_MS) * 1_000_000n

function strAttr(key: string, value: string): OtlpKeyValue {
  return { key, value: { stringValue: value } }
}

function intAttr(key: string, value: number): OtlpKeyValue {
  return { key, value: { intValue: String(Math.trunc(value)) } }
}

function doubleAttr(key: string, value: number): OtlpKeyValue {
  return { key, value: { doubleValue: value } }
}

function strArrayAttr(key: string, values: readonly string[]): OtlpKeyValue {
  return {
    key,
    value: {
      arrayValue: {
        values: values.map((v) => ({ stringValue: v })),
      },
    },
  }
}

function hexTraceId(harnessIndex: number, runIndex: number): string {
  const hi = harnessIndex.toString(16).padStart(4, '0')
  const mid = runIndex.toString(16).padStart(4, '0')
  return `${hi}${mid}000000000000000000000000`.slice(0, 32)
}

function hexSpanId(harnessIndex: number, runIndex: number, seq: number): string {
  const hi = harnessIndex.toString(16).padStart(4, '0')
  const mid = runIndex.toString(16).padStart(2, '0')
  const lo = seq.toString(16).padStart(10, '0')
  return `${hi}${mid}${lo}`
}

function nanoAt(offsetMs: number): string {
  return (BASE_TIME_NS + BigInt(offsetMs) * 1_000_000n).toString()
}

function isoAt(offsetMs: number): string {
  return new Date(BASE_TIME_MS + offsetMs).toISOString()
}

function wrapTraces(
  serviceName: string,
  scopeName: string,
  spans: readonly OtlpSpanWire[],
): OtlpExportTraceServiceRequest {
  return {
    resourceSpans: [
      {
        resource: { attributes: [strAttr('service.name', serviceName)] },
        scopeSpans: [
          {
            scope: { name: scopeName, version: '1.0.0' },
            spans,
          },
        ],
      },
    ],
  }
}

function wrapLogs(
  serviceName: string,
  scopeName: string,
  logRecords: readonly OtlpLogRecordWire[],
): OtlpExportLogsServiceRequest {
  return {
    resourceLogs: [
      {
        resource: { attributes: [strAttr('service.name', serviceName)] },
        scopeLogs: [
          {
            scope: { name: scopeName, version: '1.0.0' },
            logRecords,
          },
        ],
      },
    ],
  }
}

const SHARED_TOOL_DEFS_JSON = JSON.stringify([
  { name: 'read', description: 'Read a file from the workspace' },
  { name: 'edit', description: 'Apply an edit to a file' },
  { name: 'bash', description: 'Execute a shell command' },
  { name: 'grep', description: 'Search files by regex' },
  { name: 'codegraph_explore', description: 'Explore symbol graph', server: 'codegraph' },
])

// Two contracts per harness (T3): the `a` session runs 12 turns first, the `b` session 10 turns
// later — except Droid, whose paired `a` session runs 10 turns and whose plugin-only `b` session
// runs 12, with the UUID-shaped ids Factory writes. Every contract states what the engine may
// show for that session and nothing else.
export const STORED_OTLP_HARNESS_CONTRACTS: readonly HarnessExpectationContract[] = [
  {
    harness: 'claude-code',
    sessionId: 'otel-e2e-claude-code-a-12t',
    turns: 12,
    expectedToolCalls: 4,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: ['tdd-workflow'],
    expectedCostStatus: 'harness',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: true,
  },
  {
    harness: 'claude-code',
    sessionId: 'otel-e2e-claude-code-b-10t',
    turns: 10,
    expectedToolCalls: 4,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: ['tdd-workflow'],
    expectedCostStatus: 'harness',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: true,
  },
  {
    harness: 'opencode',
    sessionId: 'otel-e2e-opencode-a-12t',
    turns: 12,
    expectedToolCalls: 4,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: ['kw-probe-skill'],
    expectedCostStatus: 'harness',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['Skill listings were not exported'],
    sendsLogs: false,
  },
  {
    harness: 'opencode',
    sessionId: 'otel-e2e-opencode-b-10t',
    turns: 10,
    expectedToolCalls: 4,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: ['kw-probe-skill'],
    expectedCostStatus: 'harness',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['Skill listings were not exported'],
    sendsLogs: false,
  },
  {
    // Paired with transcript + companion files (T4's counters): the session totals and cost come
    // from the companion's reported session usage over the plugin's turns, the tool calls come
    // from the transcript's tool records (the Factory source-precedence rule), and the per-turn
    // chart states the not-measured turn counters instead of drawing split bars. Context reads
    // the plugin turns and falls back to the counter records, but the file source measures
    // session totals only, never per-call input, so neither carries a per-turn context figure.
    harness: 'droid',
    sessionId: '9f1c3a57-2e4b-4a68-9d02-5b7c8e1a3f44',
    turns: 10,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: false,
    pairedWithSessionFiles: true,
    expectedSkills: [],
    expectedCostStatus: 'published_priced',
    expectedModels: ['gpt-5-codex'],
    expectedGapSubstrings: [
      'tool-definition schema data',
      'Tool definitions were not exported',
      'Harness-reported cost was not exported',
      'One or more turns lack input token counts',
      'No skill listings or skill invocations were recorded',
    ],
    sendsLogs: false,
  },
  {
    // Plugin only: the plugin exports no counters at all, so every token figure stays unmeasured.
    harness: 'droid',
    sessionId: '4c8e2b90-7d1a-4f35-8b46-2a9e6c0d7f11',
    turns: 12,
    expectedToolCalls: 3,
    tokensMeasurable: false,
    cacheCreationMeasurable: false,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: false,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['gpt-5-codex'],
    expectedGapSubstrings: ['no_message_structure', 'Cache creation tokens were not reported'],
    sendsLogs: false,
  },
  {
    harness: 'codex',
    sessionId: 'otel-e2e-codex-a-12t',
    turns: 12,
    expectedToolCalls: null,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'published_priced',
    expectedModels: ['gpt-5-codex'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: true,
  },
  {
    harness: 'codex',
    sessionId: 'otel-e2e-codex-b-10t',
    turns: 10,
    expectedToolCalls: null,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'published_priced',
    expectedModels: ['gpt-5-codex'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: true,
  },
  {
    harness: 'copilot',
    sessionId: 'otel-e2e-copilot-a-12t',
    turns: 12,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'no_rate',
    expectedModels: ['gpt-4o'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: false,
  },
  {
    harness: 'copilot',
    sessionId: 'otel-e2e-copilot-b-10t',
    turns: 10,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'no_rate',
    expectedModels: ['gpt-4o'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: false,
  },
  {
    harness: 'pi',
    sessionId: 'otel-e2e-pi-a-12t',
    turns: 12,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: ['pi-telemetry-skill'],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['Skill listings were not exported'],
    sendsLogs: false,
  },
  {
    harness: 'pi',
    sessionId: 'otel-e2e-pi-b-10t',
    turns: 10,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: ['pi-telemetry-skill'],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['Skill listings were not exported'],
    sendsLogs: false,
  },
  {
    harness: 'antigravity',
    sessionId: 'otel-e2e-antigravity-a-12t',
    turns: 12,
    expectedToolCalls: 2,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['gemini-2.5-pro'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: false,
  },
  {
    harness: 'antigravity',
    sessionId: 'otel-e2e-antigravity-b-10t',
    turns: 10,
    expectedToolCalls: 2,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    perTurnTokensMeasurable: true,
    pairedWithSessionFiles: false,
    expectedSkills: [],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['gemini-2.5-pro'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: false,
  },
]

/** The trace and span id ranges one session's fixture uses, so two sessions never share a record id. */
type FixtureIdRange = {
  readonly harnessIndex: number
  readonly runIndex: 1 | 2
}

/** The per-session base offset: the `b` fixture runs 5 minutes after the `a` fixture. */
function runBaseOffsetMs(range: FixtureIdRange): number {
  return range.runIndex === 1 ? 0 : 300_000
}

function buildClaudeCodeFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const rootSpanId = hexSpanId(range.harnessIndex, range.runIndex, 1)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent claude-code',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(baseShiftMs),
      endTimeUnixNano: nanoAt(baseShiftMs + contract.turns * 2_000 + 4_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('claude_code.session.id', sessionId),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('gen_ai.agent.name', 'claude-code'),
        strAttr('kyber.session.label', `Claude Code ${contract.turns}-turn OTLP run (${sessionId})`),
      ],
    },
  ]

  let seq = 2
  const logRecords: OtlpLogRecordWire[] = []

  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_800 + 50
    const endMs = startMs + 900
    const turnSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    spans.push({
      traceId,
      spanId: turnSpanId,
      parentSpanId: rootSpanId,
      name: `claude_code.llm.turn.${turn + 1}`,
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('claude_code.session.id', sessionId),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('gen_ai.response.model', 'claude-sonnet-4-20250514'),
        intAttr('gen_ai.usage.input_tokens', 1_200 + turn * 200),
        intAttr('gen_ai.usage.cache_read.input_tokens', 3_000 + turn * 400),
        intAttr('gen_ai.usage.cache_creation.input_tokens', turn === 0 ? 1_600 : 250),
        intAttr('gen_ai.usage.output_tokens', 180 + turn * 15),
        doubleAttr('gen_ai.usage.cost', 0.014 + turn * 0.002),
      ],
    })

    if (turn < 4) {
      const toolSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
      const isSkillTool = turn === 0
      spans.push({
        traceId,
        spanId: toolSpanId,
        parentSpanId: turnSpanId,
        name: isSkillTool ? 'claude_code.tool.Skill' : `claude_code.tool.turn.${turn + 1}`,
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 20),
        endTimeUnixNano: nanoAt(endMs + 140),
        status: { code: 'STATUS_CODE_OK' },
        attributes: [
          strAttr('claude_code.session.id', sessionId),
          strAttr('gen_ai.session.id', sessionId),
          strAttr('gen_ai.tool.name', isSkillTool ? 'Skill' : turn % 2 === 0 ? 'read' : 'edit'),
          ...(isSkillTool
            ? [strAttr('gen_ai.tool.call.arguments', JSON.stringify({ skill: 'tdd-workflow' }))]
            : []),
        ],
      })
    }

    if (turn < 3) {
      logRecords.push({
        timeUnixNano: nanoAt(startMs - 10),
        traceId,
        spanId: turnSpanId,
        eventName: 'claude_code.user_prompt',
        body: { stringValue: `Turn ${turn + 1}: inspect OTLP ingest coverage` },
        attributes: [
          strAttr('event.name', 'claude_code.user_prompt'),
          strAttr('claude_code.session.id', sessionId),
          strAttr('gen_ai.session.id', sessionId),
        ],
      })
    }
  }

  return {
    harness: 'claude-code',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('claude-code', 'com.anthropic.claude_code', spans),
    logPayload: wrapLogs('claude-code', 'com.anthropic.claude_code', logRecords),
  }
}

function buildOpencodeFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const requestSpanId = hexSpanId(range.harnessIndex, range.runIndex, 1)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: requestSpanId,
      name: 'opencode.request',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(baseShiftMs),
      endTimeUnixNano: nanoAt(baseShiftMs + contract.turns * 2_100 + 5_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('opencode.contract_version', '3'),
        strAttr('opencode.session.id', sessionId),
        strAttr('opencode.span.kind', 'request'),
        strAttr('opencode.agent', 'build'),
        strAttr('opencode.prompt.text', `Run the ${contract.turns}-turn OpenCode telemetry validation pass`),
        strAttr('kyber.session.label', `OpenCode v3 ${contract.turns}-turn OTLP run (${sessionId})`),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_900 + 40
    const endMs = startMs + 950
    const stepSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    const stepAttrs: OtlpKeyValue[] = [
      strAttr('opencode.contract_version', '3'),
      strAttr('opencode.session.id', sessionId),
      strAttr('opencode.span.kind', 'step'),
      strAttr('opencode.model.id', 'claude-sonnet-4-20250514'),
      strAttr('opencode.agent', 'build'),
      intAttr('opencode.usage.input', 1_500 + turn * 250),
      intAttr('opencode.usage.cache_read', 4_000 + turn * 500),
      intAttr('opencode.usage.cache_write', turn === 0 ? 2_200 : 300),
      intAttr('opencode.usage.output', 210 + turn * 20),
      intAttr('opencode.usage.reasoning', 140),
      doubleAttr('opencode.cost', 0.018 + turn * 0.002),
      strAttr('opencode.context.tools', SHARED_TOOL_DEFS_JSON),
      strAttr('opencode.prompt.text', `OpenCode turn ${turn + 1} user follow-up message`),
    ]
    if (turn === 0) {
      stepAttrs.push(
        strAttr('opencode.context.system', 'You are OpenCode, a coding agent helping with telemetry tests.'),
        strArrayAttr('kyber.skills.invoked', ['kw-probe-skill']),
      )
    }

    spans.push({
      traceId,
      spanId: stepSpanId,
      parentSpanId: requestSpanId,
      name: 'opencode.step',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: stepAttrs,
    })

    if (turn < 4) {
      const toolName = turn === 0 ? 'codegraph_explore' : turn % 2 === 0 ? 'read' : 'edit'
      spans.push({
        traceId,
        spanId: hexSpanId(range.harnessIndex, range.runIndex, seq++),
        parentSpanId: stepSpanId,
        name: 'opencode.tool',
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 15),
        endTimeUnixNano: nanoAt(endMs + 120),
        status: { code: 'STATUS_CODE_OK' },
        attributes: [
          strAttr('opencode.contract_version', '3'),
          strAttr('opencode.session.id', sessionId),
          strAttr('opencode.span.kind', 'tool'),
          strAttr('opencode.tool.name', toolName),
          strAttr('opencode.tool.call_id', `${sessionId}_call_${turn + 1}`),
          strAttr('opencode.tool.status', 'completed'),
        ],
      })
    }
  }

  return {
    harness: 'opencode',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('kyberdash-opencode-plugin', 'kyberdash.opencode-plugin', spans),
    logPayload: null,
  }
}

function buildDroidFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const rootSpanId = hexSpanId(range.harnessIndex, range.runIndex, 1)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'factory.session',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(baseShiftMs),
      endTimeUnixNano: nanoAt(baseShiftMs + contract.turns * 1_700 + 2_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('factory.contract_version', '1'),
        strAttr('factory.session.id', sessionId),
        strAttr('factory.cwd', '/tmp/kyber-droid-e2e'),
        strAttr('kyber.session.label', `Factory Droid ${contract.turns}-turn OTLP run (${sessionId})`),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_700 + 30
    const endMs = startMs + 700
    const turnSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    const turnAttrs: OtlpKeyValue[] = [
      strAttr('factory.contract_version', '1'),
      strAttr('factory.session.id', sessionId),
      strAttr('factory.model.id', 'gpt-5-codex'),
      strAttr('factory.message.id', `droid-msg-${turn + 1}`),
    ]
    if (turn === 0) {
      turnAttrs.push(strAttr('factory.turn.prompt.text', 'Verify honest unobservability on Factory plugin turns'))
    }
    spans.push({
      traceId,
      spanId: turnSpanId,
      parentSpanId: rootSpanId,
      name: 'factory.turn',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: turnAttrs,
    })

    if (turn < 3) {
      spans.push({
        traceId,
        spanId: hexSpanId(range.harnessIndex, range.runIndex, seq++),
        parentSpanId: turnSpanId,
        name: 'factory.tool',
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 20),
        endTimeUnixNano: nanoAt(endMs + 110),
        status: { code: 'STATUS_CODE_OK' },
        attributes: [
          strAttr('factory.contract_version', '1'),
          strAttr('factory.session.id', sessionId),
          strAttr('factory.tool.name', turn === 0 ? 'Read' : 'Bash'),
          strAttr('factory.tool.call_id', `droid_${sessionId}_call_${turn + 1}`),
          strAttr('factory.tool.status', 'completed'),
        ],
      })
    }
  }

  return {
    harness: 'droid',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('kyberdash-factory-plugin', 'kyberdash.factory-plugin', spans),
    logPayload: null,
  }
}

function buildCodexFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = []

  let seq = 1
  const logRecords: OtlpLogRecordWire[] = []

  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_800 + 60
    const endMs = startMs + 820
    const turnSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    const cacheRead = 2_400 + turn * 300
    const cacheWrite = turn === 0 ? 1_200 : 200
    const fresh = 1_100 + turn * 150
    // CodexAdapter assumes cache-inclusive input (input >= cacheRead + cacheWrite).
    const inclusiveInput = fresh + cacheRead + cacheWrite
    spans.push({
      traceId,
      spanId: turnSpanId,
      name: `codex.responses.turn.${turn + 1}`,
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('codex.turn', String(turn + 1)),
        strAttr('conversation.id', sessionId),
        strAttr('model', 'gpt-5-codex'),
        intAttr('gen_ai.usage.input_tokens', inclusiveInput),
        intAttr('gen_ai.usage.cached_tokens', cacheRead),
        intAttr('gen_ai.usage.cache_write.input_tokens', cacheWrite),
        intAttr('gen_ai.usage.output_tokens', 220 + turn * 12),
        intAttr('codex.usage.reasoning_output_tokens', 160),
      ],
    })

    if (turn < 3) {
      logRecords.push({
        timeUnixNano: nanoAt(endMs + 25),
        traceId,
        spanId: turnSpanId,
        eventName: 'codex.tool_result',
        body: { stringValue: `Codex tool result ${turn + 1}` },
        attributes: [
          strAttr('event.name', 'codex.tool_result'),
          strAttr('conversation.id', sessionId),
          strAttr('tool_name', turn === 0 ? 'shell' : 'apply_patch'),
          strAttr('call_id', `${sessionId}_call_${turn + 1}`),
          strAttr('output', 'ok'),
        ],
      })
    }
  }

  return {
    harness: 'codex',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('codex_cli_rs', 'codex.otel', spans),
    logPayload: wrapLogs('codex_cli_rs', 'codex.otel', logRecords),
  }
}

function buildCopilotFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const rootSpanId = hexSpanId(range.harnessIndex, range.runIndex, 1)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent copilot',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(baseShiftMs),
      endTimeUnixNano: nanoAt(baseShiftMs + contract.turns * 2_000 + 3_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('copilot_chat.chat_session_id', sessionId),
        strAttr('kyber.session.label', `Copilot ${contract.turns}-turn OTLP run (${sessionId})`),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_750 + 40
    const endMs = startMs + 780
    const turnSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    const cacheRead = 1_800 + turn * 250
    const cacheCreation = turn === 0 ? 900 : 150
    const fresh = 950 + turn * 100
    spans.push({
      traceId,
      spanId: turnSpanId,
      parentSpanId: rootSpanId,
      name: `copilot_chat.turn.${turn + 1}`,
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('copilot_chat.chat_session_id', sessionId),
        strAttr('gen_ai.response.model', 'gpt-4o'),
        intAttr('gen_ai.usage.input_tokens', fresh + cacheRead + cacheCreation),
        intAttr('gen_ai.usage.cache_read.input_tokens', cacheRead),
        intAttr('gen_ai.usage.cache_creation.input_tokens', cacheCreation),
        intAttr('gen_ai.usage.output_tokens', 160 + turn * 10),
      ],
    })

    if (turn < 3) {
      spans.push({
        traceId,
        spanId: hexSpanId(range.harnessIndex, range.runIndex, seq++),
        parentSpanId: turnSpanId,
        name: `copilot_chat.tool.${turn + 1}`,
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 15),
        endTimeUnixNano: nanoAt(endMs + 110),
        status: { code: 'STATUS_CODE_OK' },
        attributes: [
          strAttr('copilot_chat.chat_session_id', sessionId),
          strAttr('gen_ai.tool.name', turn === 0 ? 'read_file' : 'replace_string_in_file'),
        ],
      })
    }
  }

  return {
    harness: 'copilot',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('copilot-chat', 'github.copilot.chat', spans),
    logPayload: null,
  }
}

function buildPiFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const rootSpanId = hexSpanId(range.harnessIndex, range.runIndex, 1)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent pi',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(baseShiftMs),
      endTimeUnixNano: nanoAt(baseShiftMs + contract.turns * 2_000 + 3_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('pi.session.id', sessionId),
        strAttr('kyber.session.label', `Pi ${contract.turns}-turn OTLP run (${sessionId})`),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_800 + 45
    const endMs = startMs + 810
    const turnSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    const turnAttrs: OtlpKeyValue[] = [
      strAttr('pi.session.id', sessionId),
      strAttr('gen_ai.response.model', 'claude-sonnet-4-20250514'),
      intAttr('gen_ai.usage.input_tokens', 1_100 + turn * 140),
      intAttr('gen_ai.usage.cache_read.input_tokens', 2_200 + turn * 300),
      intAttr('gen_ai.usage.cache_creation.input_tokens', turn === 0 ? 1_400 : 200),
      intAttr('gen_ai.usage.output_tokens', 175 + turn * 12),
      intAttr('gen_ai.usage.reasoning_tokens', 90),
      strAttr('pi.tools.selected', SHARED_TOOL_DEFS_JSON),
      strAttr('pi.context_files', 'AGENTS.md, docs/dash/architecture.md'),
    ]
    if (turn === 0) {
      turnAttrs.push(
        strAttr('pi.skills', 'pi-telemetry-skill'),
        strArrayAttr('kyber.skills.invoked', ['pi-telemetry-skill']),
      )
    }

    spans.push({
      traceId,
      spanId: turnSpanId,
      parentSpanId: rootSpanId,
      name: `pi.turn.${turn + 1}`,
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: turnAttrs,
    })

    if (turn < 3) {
      spans.push({
        traceId,
        spanId: hexSpanId(range.harnessIndex, range.runIndex, seq++),
        parentSpanId: turnSpanId,
        name: `pi.tool.${turn + 1}`,
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 20),
        endTimeUnixNano: nanoAt(endMs + 130),
        status: { code: 'STATUS_CODE_OK' },
        attributes: [
          strAttr('pi.session.id', sessionId),
          strAttr('gen_ai.tool.name', turn === 0 ? 'read' : 'bash'),
        ],
      })
    }
  }

  return {
    harness: 'pi',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('pi-agent', 'pi.telemetry', spans),
    logPayload: null,
  }
}

function buildAntigravityFixture(contract: HarnessExpectationContract, range: FixtureIdRange): HarnessOtlpFixture {
  const sessionId = contract.sessionId
  const traceId = hexTraceId(range.harnessIndex, range.runIndex)
  const rootSpanId = hexSpanId(range.harnessIndex, range.runIndex, 1)
  const baseShiftMs = runBaseOffsetMs(range)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent antigravity',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(baseShiftMs),
      endTimeUnixNano: nanoAt(baseShiftMs + contract.turns * 1_900 + 3_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('gen_ai.agent.name', 'antigravity'),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('kyber.session.label', `Antigravity ${contract.turns}-turn OTLP run (${sessionId})`),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = baseShiftMs + turn * 1_700 + 40
    const endMs = startMs + 760
    const turnSpanId = hexSpanId(range.harnessIndex, range.runIndex, seq++)
    const cacheRead = 1_600 + turn * 200
    const fresh = 900 + turn * 120
    spans.push({
      traceId,
      spanId: turnSpanId,
      parentSpanId: rootSpanId,
      name: `antigravity.llm.turn.${turn + 1}`,
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.agent.name', 'antigravity'),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('gen_ai.response.model', 'gemini-2.5-pro'),
        intAttr('gen_ai.usage.input_tokens', fresh + cacheRead),
        intAttr('gen_ai.usage.cache_read.input_tokens', cacheRead),
        intAttr('gen_ai.usage.output_tokens', 190 + turn * 10),
        intAttr('gemini.usage.thoughts_tokens', 120),
      ],
    })

    if (turn < 2) {
      spans.push({
        traceId,
        spanId: hexSpanId(range.harnessIndex, range.runIndex, seq++),
        parentSpanId: turnSpanId,
        name: `antigravity.tool.${turn + 1}`,
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 15),
        endTimeUnixNano: nanoAt(endMs + 95),
        status: { code: 'STATUS_CODE_OK' },
        attributes: [
          strAttr('gen_ai.agent.name', 'antigravity'),
          strAttr('gen_ai.session.id', sessionId),
          strAttr('gen_ai.tool.name', turn === 0 ? 'grep_search' : 'view_file'),
        ],
      })
    }
  }

  return {
    harness: 'antigravity',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('antigravity', 'antigravity.otel', spans),
    logPayload: null,
  }
}

function buildGeminiExcludedFixture(): HarnessOtlpFixture {
  const sessionId = GEMINI_EXCLUDED_SESSION_ID
  const traceId = hexTraceId(8, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: hexSpanId(8, 1, 1),
      name: 'gemini.generate_content',
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(100),
      endTimeUnixNano: nanoAt(950),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.system', 'gemini'),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('gen_ai.response.model', 'gemini-2.5-pro'),
        intAttr('gen_ai.usage.input_tokens', 2_400),
        intAttr('gen_ai.usage.cache_read.input_tokens', 800),
        intAttr('gen_ai.usage.output_tokens', 260),
        intAttr('gemini.usage.thoughts_tokens', 180),
      ],
    },
  ]

  return {
    harness: 'gemini',
    sessionId,
    turns: 1,
    tracePayload: wrapTraces('gemini-cli', 'gemini.telemetry', spans),
    logPayload: null,
  }
}

function buildContractFixture(contract: HarnessExpectationContract): HarnessOtlpFixture {
  const harnessIndex = 1 + STORED_OTLP_HARNESS_CONTRACTS.findIndex(
    (candidate) => candidate.harness === contract.harness,
  )
  const ordinal = STORED_OTLP_HARNESS_CONTRACTS.filter(
    (candidate) => candidate.harness === contract.harness,
  ).indexOf(contract)
  if (ordinal < 0) {
    throw new Error(`contract ${contract.sessionId} is not in STORED_OTLP_HARNESS_CONTRACTS`)
  }
  const runIndex: 1 | 2 = ordinal === 0 ? 1 : 2
  const range: FixtureIdRange = { harnessIndex, runIndex }
  switch (contract.harness) {
    case 'claude-code':
      return buildClaudeCodeFixture(contract, range)
    case 'opencode':
      return buildOpencodeFixture(contract, range)
    case 'droid':
      return buildDroidFixture(contract, range)
    case 'codex':
      return buildCodexFixture(contract, range)
    case 'copilot':
      return buildCopilotFixture(contract, range)
    case 'pi':
      return buildPiFixture(contract, range)
    case 'antigravity':
      return buildAntigravityFixture(contract, range)
  }
}

export function buildHarnessOtlpFixture(contract: HarnessExpectationContract): HarnessOtlpFixture {
  return buildContractFixture(contract)
}

export function buildAllHarnessOtlpFixtures(): readonly HarnessOtlpFixture[] {
  return [
    ...STORED_OTLP_HARNESS_CONTRACTS.map(buildHarnessOtlpFixture),
    buildGeminiExcludedFixture(),
  ]
}

// The workspace folder mirrors the cwd leaf, the way Factory names the sessions folder.
const DROID_WORKSPACE = 'kyber-droid-e2e'
const DROID_CWD = `/tmp/${DROID_WORKSPACE}`

/**
 * Builds the paired Droid session's transcript (`<id>.jsonl`) and companion
 * (`<id>.settings.json`) files, shaped as `DroidSessionMapperTests` writes them: the
 * `session_start` carries the session id, one assistant message per turn, and the companion
 * carries the reported session usage (the pinned counters) with the model the session ran on.
 */
export function buildDroidSessionFiles(contract: HarnessExpectationContract): DroidSessionFilePair {
  const transcriptPath = `.factory/sessions/${DROID_WORKSPACE}/${contract.sessionId}.jsonl`
  const companionPath = `.factory/sessions/${DROID_WORKSPACE}/${contract.sessionId}.settings.json`

  const lines: string[] = [
    JSON.stringify({
      type: 'session_start',
      id: contract.sessionId,
      title: 'Droid paired OTLP run',
      cwd: DROID_CWD,
    }),
    // Turn 1 carries the transcript's tool records: three tool_use calls with their results, so
    // the file source owns the session's tool invocations under the source-precedence rule.
    JSON.stringify({
      type: 'message',
      id: 'droid-msg-1',
      timestamp: isoAt(0),
      message: {
        role: 'assistant',
        content: [
          { type: 'tool_use', id: 'tu-1', name: 'Read', input: { path: 'src/telemetry.ts' } },
          { type: 'tool_use', id: 'tu-2', name: 'Bash', input: { command: 'dotnet test' } },
          { type: 'tool_use', id: 'tu-3', name: 'Edit', input: { path: 'src/telemetry.ts' } },
        ],
      },
    }),
    JSON.stringify({
      type: 'message',
      timestamp: isoAt(4_000),
      message: {
        role: 'user',
        content: [
          { type: 'tool_result', tool_use_id: 'tu-1', is_error: false, content: 'const collector = 1' },
          { type: 'tool_result', tool_use_id: 'tu-2', is_error: false, content: 'tests passed' },
          { type: 'tool_result', tool_use_id: 'tu-3', is_error: false, content: 'edited' },
        ],
      },
    }),
  ]
  for (let turn = 1; turn < contract.turns; turn++) {
    lines.push(JSON.stringify({
      type: 'message',
      id: `droid-msg-${turn + 1}`,
      timestamp: isoAt(turn * 1_700 + 30),
      message: {
        role: 'assistant',
        content: [{ type: 'text', text: `Turn ${turn + 1}: paired-session analysis complete.` }],
      },
    }))
  }

  const companion = JSON.stringify({
    model: 'gpt-5-codex',
    tokenUsage: {
      inputTokens: 25829,
      outputTokens: 6811,
      cacheReadTokens: 283904,
      cacheCreationTokens: 1200,
    },
  })

  return {
    transcriptPath,
    companionPath,
    transcript: lines.join('\n') + '\n',
    companion: companion + '\n',
  }
}

/** Which of the two Copilot capture surfaces a baseline fixture models. */
export type CopilotBaselineSourceMode = 'primary' | 'secondary'

/** Options for {@link buildCopilotBaselineFixture}; every field has a documented default. */
export type CopilotBaselineFixtureOptions = {
  readonly sessionId?: string
  readonly sourceMode?: CopilotBaselineSourceMode
}

/** The `IsDocument` result the OTLP mapper withholds into `content_part_withheld`. */
export type CopilotBaselineWithheldCase = {
  readonly spanName: string
  readonly resultAttribute: 'gen_ai.tool.call.result'
  readonly documentKind: 'json-document'
  readonly expectedBucket: 'content_part_withheld'
  readonly expectedPlainText: ''
}

/** A rich Copilot OTLP fixture plus the withheld-content case it pins. */
export type CopilotBaselineFixture = HarnessOtlpFixture & {
  readonly sourceMode: CopilotBaselineSourceMode
  readonly withheldCase: CopilotBaselineWithheldCase
}

/**
 * The rich Copilot baseline contract: one fully measurable session covering every content
 * dimension the canonical `copilot` OTLP baseline exhibits. Distinct from the stored
 * `copilot` contracts above, so their assertions are untouched.
 */
export const COPILOT_BASELINE_CONTRACT: HarnessExpectationContract = {
  harness: 'copilot',
  sessionId: 'otel-e2e-copilot-baseline-rich',
  turns: 6,
  expectedToolCalls: 4,
  tokensMeasurable: true,
  cacheCreationMeasurable: true,
  contextMeasurable: true,
  schemaMeasurable: true,
  skillsMeasurable: true,
  perTurnTokensMeasurable: true,
  pairedWithSessionFiles: false,
  expectedSkills: ['tdd-workflow'],
  expectedCostStatus: 'no_rate',
  expectedModels: ['gpt-4o'],
  expectedGapSubstrings: ['content_part_withheld'],
  sendsLogs: true,
}

const COPILOT_BASELINE_WITHHELD_SPAN = 'copilot_chat.tool.withheld'

// A JSON object result the mapper classifies as a document and withholds: `JsonDocumentText`
// treats a complete object/array as a document, so this pins the `content_part_withheld` path.
const COPILOT_BASELINE_WITHHELD_DOCUMENT = JSON.stringify({
  files: Array.from({ length: 40 }, (_, index) => ({ file: `src/module-${index}.ts`, bytes: 1_024 + index })),
})

type CopilotBaselineToolSpec = {
  readonly name: string
  readonly tool: string
  readonly arguments: string
  readonly result: string
  readonly mcpServer?: string
  readonly mcpTool?: string
}

const COPILOT_BASELINE_TOOLS: readonly CopilotBaselineToolSpec[] = [
  {
    name: 'copilot_chat.tool.read',
    tool: 'read_file',
    arguments: JSON.stringify({ file: 'src/telemetry.ts' }),
    result: 'const collector = 1',
  },
  {
    name: COPILOT_BASELINE_WITHHELD_SPAN,
    tool: 'read_file',
    arguments: JSON.stringify({ file: 'package-lock.json' }),
    result: COPILOT_BASELINE_WITHHELD_DOCUMENT,
  },
  {
    name: 'copilot_chat.tool.mcp',
    tool: 'codegraph_explore',
    arguments: JSON.stringify({ query: 'KyberBridgeEngine' }),
    result: 'symbol graph',
    mcpServer: 'codegraph',
    mcpTool: 'explore',
  },
  {
    name: 'copilot_chat.tool.edit',
    tool: 'edit_file',
    arguments: JSON.stringify({ file: 'src/telemetry.ts' }),
    result: 'applied',
  },
]

/**
 * Builds the rich dual Copilot baseline OTLP payload: system prompt, conversation history,
 * tool definitions, `tool.invoke` spans (one with a withheld document result), an
 * `agent.invoke` span, skill invocations, and log records.
 */
export function buildCopilotBaselineFixture(options: CopilotBaselineFixtureOptions = {}): CopilotBaselineFixture {
  const contract = COPILOT_BASELINE_CONTRACT
  const sessionId = options.sessionId ?? contract.sessionId
  const sourceMode = options.sourceMode ?? 'primary'
  const traceId = hexTraceId(90, 1)
  const rootSpanId = hexSpanId(90, 1, 1)

  const systemInstructions = 'You are a coding agent verifying telemetry capture.'
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent copilot',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(contract.turns * 1_700 + 4_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('gen_ai.agent.name', 'copilot'),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('copilot_chat.chat_session_id', sessionId),
        strAttr('kyber.session.label', `Copilot baseline rich OTLP run (${sourceMode})`),
      ],
    },
  ]

  let seq = 2
  const logRecords: OtlpLogRecordWire[] = []

  for (let turn = 0; turn < contract.turns; turn++) {
    const startMs = turn * 1_700 + 40
    const endMs = startMs + 800
    const turnSpanId = hexSpanId(90, 1, seq++)
    const messages = JSON.stringify([
      { role: 'system', content: systemInstructions },
      { role: 'user', content: `Baseline turn ${turn + 1}: capture full content.` },
      { role: 'assistant', content: `Baseline turn ${turn + 1}: content captured.` },
    ])
    const cacheRead = 1_800 + turn * 250
    const cacheCreation = turn === 0 ? 900 : 150
    const fresh = 950 + turn * 100

    const turnAttributes: OtlpKeyValue[] = [
      strAttr('gen_ai.session.id', sessionId),
      strAttr('copilot_chat.chat_session_id', sessionId),
      strAttr('gen_ai.response.model', 'gpt-4o'),
      intAttr('gen_ai.usage.input_tokens', fresh + cacheRead + cacheCreation),
      intAttr('gen_ai.usage.cache_read.input_tokens', cacheRead),
      intAttr('gen_ai.usage.cache_creation.input_tokens', cacheCreation),
      intAttr('gen_ai.usage.output_tokens', 160 + turn * 10),
      strAttr('gen_ai.system_instructions', systemInstructions),
      strAttr('gen_ai.input.messages', messages),
    ]

    if (turn === 0) {
      turnAttributes.push(
        strAttr('gen_ai.rules', 'Follow the repository rules.'),
        strAttr('gen_ai.skills', 'tdd-workflow'),
        strArrayAttr('kyber.skills.invoked', ['tdd-workflow']),
        strAttr('gen_ai.tool.definitions', SHARED_TOOL_DEFS_JSON),
      )
    }

    spans.push({
      traceId,
      spanId: turnSpanId,
      parentSpanId: rootSpanId,
      name: `copilot_chat.turn.${turn + 1}`,
      kind: 'SPAN_KIND_CLIENT',
      startTimeUnixNano: nanoAt(startMs),
      endTimeUnixNano: nanoAt(endMs),
      status: { code: 'STATUS_CODE_OK' },
      attributes: turnAttributes,
    })

    const tool = COPILOT_BASELINE_TOOLS[turn]
    if (tool !== undefined) {
      const toolAttributes: OtlpKeyValue[] = [
        strAttr('gen_ai.session.id', sessionId),
        strAttr('copilot_chat.chat_session_id', sessionId),
        strAttr('gen_ai.tool.name', tool.tool),
        strAttr('gen_ai.tool.call.id', `${sessionId}-call-${turn + 1}`),
        strAttr('gen_ai.tool.call.arguments', tool.arguments),
        strAttr('gen_ai.tool.call.result', tool.result),
      ]
      if (tool.mcpServer !== undefined) toolAttributes.push(strAttr('gen_ai.tool.mcp.server', tool.mcpServer))
      if (tool.mcpTool !== undefined) toolAttributes.push(strAttr('gen_ai.tool.mcp.tool', tool.mcpTool))

      spans.push({
        traceId,
        spanId: hexSpanId(90, 1, seq++),
        parentSpanId: turnSpanId,
        name: tool.name,
        kind: 'SPAN_KIND_INTERNAL',
        startTimeUnixNano: nanoAt(endMs + 15),
        endTimeUnixNano: nanoAt(endMs + 110),
        status: { code: 'STATUS_CODE_OK' },
        attributes: toolAttributes,
      })
    }

    if (turn < 2) {
      logRecords.push({
        timeUnixNano: nanoAt(startMs - 10),
        traceId,
        spanId: hexSpanId(90, 1, 100 + turn),
        eventName: 'copilot_chat.user_prompt',
        body: { stringValue: `Baseline turn ${turn + 1}: capture full content.` },
        attributes: [
          strAttr('event.name', 'copilot_chat.user_prompt'),
          strAttr('gen_ai.session.id', sessionId),
          strAttr('copilot_chat.chat_session_id', sessionId),
        ],
      })
    }
  }

  spans.push({
    traceId,
    spanId: hexSpanId(90, 1, seq++),
    parentSpanId: rootSpanId,
    name: 'invoke_agent explore',
    kind: 'SPAN_KIND_INTERNAL',
    startTimeUnixNano: nanoAt(2_000),
    endTimeUnixNano: nanoAt(8_000),
    status: { code: 'STATUS_CODE_OK' },
    attributes: [
      strAttr('gen_ai.operation.name', 'invoke_agent'),
      strAttr('gen_ai.agent.name', 'explore'),
      strAttr('gen_ai.session.id', sessionId),
      strAttr('copilot_chat.chat_session_id', sessionId),
      strAttr('gen_ai.tool.call.id', `${sessionId}-spawn`),
    ],
  })

  return {
    harness: 'copilot',
    sessionId,
    turns: contract.turns,
    tracePayload: wrapTraces('copilot-chat', 'github.copilot.chat', spans),
    logPayload: wrapLogs('copilot-chat', 'github.copilot.chat', logRecords),
    sourceMode,
    withheldCase: {
      spanName: COPILOT_BASELINE_WITHHELD_SPAN,
      resultAttribute: 'gen_ai.tool.call.result',
      documentKind: 'json-document',
      expectedBucket: 'content_part_withheld',
      expectedPlainText: '',
    },
  }
}
