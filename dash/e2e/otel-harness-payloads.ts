// Multi-turn OTLP trace and log payload builders (up to 12 turns per harness) and
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

export const GEMINI_EXCLUDED_SESSION_ID = 'otel-e2e-gemini-excluded'

// Recent UTC base timestamp (2026-11-20T10:00:00.000Z) so 14-day retention never purges parts.
const BASE_TIME_NS = 1_795_168_800_000_000_000n

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

function hexTraceId(harnessIndex: number, runIndex = 1): string {
  const hi = harnessIndex.toString(16).padStart(4, '0')
  const lo = runIndex.toString(16).padStart(4, '0')
  return `${hi}${lo}000000000000000000000000`.slice(0, 32)
}

function hexSpanId(harnessIndex: number, seq: number): string {
  const hi = harnessIndex.toString(16).padStart(4, '0')
  const lo = seq.toString(16).padStart(12, '0')
  return `${hi}${lo}`
}

function nanoAt(offsetMs: number): string {
  return (BASE_TIME_NS + BigInt(offsetMs) * 1_000_000n).toString()
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

export const STORED_OTLP_HARNESS_CONTRACTS: readonly HarnessExpectationContract[] = [
  {
    harness: 'claude-code',
    sessionId: 'otel-e2e-claude-code-12t',
    turns: 12,
    expectedToolCalls: 4,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: true,
    expectedSkills: ['tdd-workflow'],
    expectedCostStatus: 'harness',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: true,
  },
  {
    harness: 'opencode',
    sessionId: 'otel-e2e-opencode-12t',
    turns: 12,
    expectedToolCalls: 4,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    expectedSkills: ['kw-probe-skill'],
    expectedCostStatus: 'harness',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['Skill listings were not exported'],
    sendsLogs: false,
  },
  {
    harness: 'droid',
    sessionId: 'otel-e2e-droid-8t',
    turns: 8,
    expectedToolCalls: 3,
    tokensMeasurable: false,
    cacheCreationMeasurable: false,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    expectedSkills: [],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['gpt-5-codex'],
    expectedGapSubstrings: ['no_message_structure', 'Cache creation tokens were not reported'],
    sendsLogs: false,
  },
  {
    harness: 'codex',
    sessionId: 'otel-e2e-codex-12t',
    turns: 12,
    expectedToolCalls: null,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    expectedSkills: [],
    expectedCostStatus: 'published_priced',
    expectedModels: ['gpt-5-codex'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: true,
  },
  {
    harness: 'copilot',
    sessionId: 'otel-e2e-copilot-12t',
    turns: 12,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    expectedSkills: [],
    expectedCostStatus: 'no_rate',
    expectedModels: ['gpt-4o'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: false,
  },
  {
    harness: 'pi',
    sessionId: 'otel-e2e-pi-12t',
    turns: 12,
    expectedToolCalls: 3,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: true,
    schemaMeasurable: true,
    skillsMeasurable: true,
    expectedSkills: ['pi-telemetry-skill'],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['claude-sonnet-4-20250514'],
    expectedGapSubstrings: ['Skill listings were not exported'],
    sendsLogs: false,
  },
  {
    harness: 'antigravity',
    sessionId: 'otel-e2e-antigravity-10t',
    turns: 10,
    expectedToolCalls: 2,
    tokensMeasurable: true,
    cacheCreationMeasurable: true,
    contextMeasurable: false,
    schemaMeasurable: false,
    skillsMeasurable: false,
    expectedSkills: [],
    expectedCostStatus: 'out_of_scope',
    expectedModels: ['gemini-2.5-pro'],
    expectedGapSubstrings: ['no_message_structure', 'tool-definition schema data'],
    sendsLogs: false,
  },
]

function buildClaudeCodeFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-claude-code-12t'
  const traceId = hexTraceId(1)
  const rootSpanId = hexSpanId(1, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent claude-code',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(24_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('claude_code.session.id', sessionId),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('gen_ai.agent.name', 'claude-code'),
        strAttr('kyber.session.label', 'Claude Code 12-turn OTLP run'),
      ],
    },
  ]

  let seq = 2
  const logRecords: OtlpLogRecordWire[] = []

  for (let turn = 0; turn < 12; turn++) {
    const startMs = turn * 1_800 + 50
    const endMs = startMs + 900
    const turnSpanId = hexSpanId(1, seq++)
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
      const toolSpanId = hexSpanId(1, seq++)
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
    turns: 12,
    tracePayload: wrapTraces('claude-code', 'com.anthropic.claude_code', spans),
    logPayload: wrapLogs('claude-code', 'com.anthropic.claude_code', logRecords),
  }
}

function buildOpencodeFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-opencode-12t'
  const traceId = hexTraceId(2)
  const requestSpanId = hexSpanId(2, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: requestSpanId,
      name: 'opencode.request',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(26_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('opencode.contract_version', '3'),
        strAttr('opencode.session.id', sessionId),
        strAttr('opencode.span.kind', 'request'),
        strAttr('opencode.agent', 'build'),
        strAttr('opencode.prompt.text', 'Run the 12-turn OpenCode telemetry validation pass'),
        strAttr('kyber.session.label', 'OpenCode v3 12-turn OTLP run'),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < 12; turn++) {
    const startMs = turn * 1_900 + 40
    const endMs = startMs + 950
    const stepSpanId = hexSpanId(2, seq++)
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
        spanId: hexSpanId(2, seq++),
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
          strAttr('opencode.tool.call_id', `oc_call_${turn + 1}`),
          strAttr('opencode.tool.status', 'completed'),
        ],
      })
    }
  }

  return {
    harness: 'opencode',
    sessionId,
    turns: 12,
    tracePayload: wrapTraces('kyberdash-opencode-plugin', 'kyberdash.opencode-plugin', spans),
    logPayload: null,
  }
}

function buildDroidFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-droid-8t'
  const traceId = hexTraceId(3)
  const rootSpanId = hexSpanId(3, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'factory.session',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(16_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('factory.contract_version', '1'),
        strAttr('factory.session.id', sessionId),
        strAttr('factory.cwd', '/tmp/kyber-droid-e2e'),
        strAttr('kyber.session.label', 'Factory Droid 8-turn OTLP run'),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < 8; turn++) {
    const startMs = turn * 1_700 + 30
    const endMs = startMs + 700
    const turnSpanId = hexSpanId(3, seq++)
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
        spanId: hexSpanId(3, seq++),
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
          strAttr('factory.tool.call_id', `droid_call_${turn + 1}`),
          strAttr('factory.tool.status', 'completed'),
        ],
      })
    }
  }

  return {
    harness: 'droid',
    sessionId,
    turns: 8,
    tracePayload: wrapTraces('kyberdash-factory-plugin', 'kyberdash.factory-plugin', spans),
    logPayload: null,
  }
}

function buildCodexFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-codex-12t'
  const traceId = hexTraceId(4)
  const spans: OtlpSpanWire[] = []

  let seq = 1
  const logRecords: OtlpLogRecordWire[] = []

  for (let turn = 0; turn < 12; turn++) {
    const startMs = turn * 1_800 + 60
    const endMs = startMs + 820
    const turnSpanId = hexSpanId(4, seq++)
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
          strAttr('call_id', `codex_call_${turn + 1}`),
          strAttr('output', 'ok'),
        ],
      })
    }
  }

  return {
    harness: 'codex',
    sessionId,
    turns: 12,
    tracePayload: wrapTraces('codex_cli_rs', 'codex.otel', spans),
    logPayload: wrapLogs('codex_cli_rs', 'codex.otel', logRecords),
  }
}

function buildCopilotFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-copilot-12t'
  const traceId = hexTraceId(5)
  const rootSpanId = hexSpanId(5, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent copilot',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(22_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('copilot_chat.chat_session_id', sessionId),
        strAttr('kyber.session.label', 'Copilot 12-turn OTLP run'),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < 12; turn++) {
    const startMs = turn * 1_750 + 40
    const endMs = startMs + 780
    const turnSpanId = hexSpanId(5, seq++)
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
        spanId: hexSpanId(5, seq++),
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
    turns: 12,
    tracePayload: wrapTraces('copilot-chat', 'github.copilot.chat', spans),
    logPayload: null,
  }
}

function buildPiFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-pi-12t'
  const traceId = hexTraceId(6)
  const rootSpanId = hexSpanId(6, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent pi',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(23_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('pi.session.id', sessionId),
        strAttr('kyber.session.label', 'Pi 12-turn OTLP run'),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < 12; turn++) {
    const startMs = turn * 1_800 + 45
    const endMs = startMs + 810
    const turnSpanId = hexSpanId(6, seq++)
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
        spanId: hexSpanId(6, seq++),
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
    turns: 12,
    tracePayload: wrapTraces('pi-agent', 'pi.telemetry', spans),
    logPayload: null,
  }
}

function buildAntigravityFixture(): HarnessOtlpFixture {
  const sessionId = 'otel-e2e-antigravity-10t'
  const traceId = hexTraceId(7)
  const rootSpanId = hexSpanId(7, 1)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: rootSpanId,
      name: 'invoke_agent antigravity',
      kind: 'SPAN_KIND_INTERNAL',
      startTimeUnixNano: nanoAt(0),
      endTimeUnixNano: nanoAt(19_000),
      status: { code: 'STATUS_CODE_OK' },
      attributes: [
        strAttr('gen_ai.operation.name', 'invoke_agent'),
        strAttr('gen_ai.agent.name', 'antigravity'),
        strAttr('gen_ai.session.id', sessionId),
        strAttr('kyber.session.label', 'Antigravity 10-turn OTLP run'),
      ],
    },
  ]

  let seq = 2
  for (let turn = 0; turn < 10; turn++) {
    const startMs = turn * 1_700 + 40
    const endMs = startMs + 760
    const turnSpanId = hexSpanId(7, seq++)
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
        spanId: hexSpanId(7, seq++),
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
    turns: 10,
    tracePayload: wrapTraces('antigravity', 'antigravity.otel', spans),
    logPayload: null,
  }
}

function buildGeminiExcludedFixture(): HarnessOtlpFixture {
  const sessionId = GEMINI_EXCLUDED_SESSION_ID
  const traceId = hexTraceId(8)
  const spans: OtlpSpanWire[] = [
    {
      traceId,
      spanId: hexSpanId(8, 1),
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

export function buildHarnessOtlpFixture(harness: AllOtlpHarnessId): HarnessOtlpFixture {
  switch (harness) {
    case 'claude-code':
      return buildClaudeCodeFixture()
    case 'opencode':
      return buildOpencodeFixture()
    case 'droid':
      return buildDroidFixture()
    case 'codex':
      return buildCodexFixture()
    case 'copilot':
      return buildCopilotFixture()
    case 'pi':
      return buildPiFixture()
    case 'antigravity':
      return buildAntigravityFixture()
    case 'gemini':
      return buildGeminiExcludedFixture()
  }
}

export function buildAllHarnessOtlpFixtures(): readonly HarnessOtlpFixture[] {
  return [
    buildClaudeCodeFixture(),
    buildOpencodeFixture(),
    buildDroidFixture(),
    buildCodexFixture(),
    buildCopilotFixture(),
    buildPiFixture(),
    buildAntigravityFixture(),
    buildGeminiExcludedFixture(),
  ]
}
