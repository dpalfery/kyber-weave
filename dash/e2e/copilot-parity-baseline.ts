// The sanitized Copilot baseline contract and the full cross-harness parity matrix.
//
// Two Copilot surfaces are pinned here: the observed `~/.kyberdash/canon.db` OTLP snapshot
// (schema version 30, detector version 8) and the `CopilotCliMapper` file-transcript shape.
// Only structural counts, thresholds, and marker codes are stored. No prompt, title,
// transcript text, session id, or local path from any real store is committed here.
//
// `normalizeParityHarnessId` is a deliberate line-for-line mirror of
// `CanonPricing.NormalizeHarnessName` (KyberDash.Domain) because the Playwright and Vitest
// layers cannot call C#. Keep the two in step: the parity matrix is keyed on the same
// canonical identifiers the server stores and the dashboard displays.

/** Support classification of a harness in the parity matrix, mirroring `HarnessSourceSupport`. */
export type CopilotParitySupportTier =
  | 'local-files'
  | 'otlp-only'
  | 'classified-only'
  | 'unevidenced'
  | 'excluded'

/** Where a harness's telemetry originates. Anything neither file nor OTLP is `none`. */
export type CopilotParitySourceKind = 'file' | 'otlp' | 'none'

/**
 * The content layers a parity comparison inspects. `ingest-diagnostic` covers a harness whose
 * only observable surface is a collection problem rather than session content.
 */
export const ALL_COPILOT_PARITY_DIMENSIONS = [
  'session-collection',
  'turn-projection',
  'token-accounting',
  'context-content',
  'system-prompt',
  'tool-definitions',
  'tool-invocations',
  'skills',
  'user-requests',
  'stored-fields',
  'cost',
  'ingest-diagnostic',
] as const

/** One content layer a parity comparison inspects. */
export type CopilotParityDimension = (typeof ALL_COPILOT_PARITY_DIMENSIONS)[number]

/** How many rows of one held-back content bucket were withheld against how many exist. */
export type CopilotWithheldCoverage = {
  readonly withheld: number
  readonly total: number
}

/** Measure percentages for the three layers that decide whether a session projects at all. */
export type CopilotProjectionCoverage = {
  readonly contextPercent: number
  readonly schemaPercent: number
  readonly tokensPercent: number
}

/** Stored and projected system-prompt coverage, with the truncation threshold. */
export type CopilotSystemPromptCoverage = {
  readonly storedParts: number
  readonly projectedTurns: number
  readonly truncatedAtBytes: number
  readonly truncatedParts: number
}

/** Tool-definition schema coverage as observed across `llm.invoke` and `agent.invoke` records. */
export type CopilotToolDefinitionCoverage = {
  readonly rows: number
  readonly turnsWithDefinitions: number
  readonly rankedToolRows: number
}

/** Tool-invocation span coverage against stored `tool_call` rows. */
export type CopilotToolInvocationCoverage = {
  readonly spans: number
  readonly withToolCallRows: number
}

/** The `CopilotCliMapper` file-transcript contract, the second half of the dual baseline. */
export type CopilotCliTranscriptShape = {
  readonly harness: 'copilot-cli'
  readonly schemaVersion: number
  readonly emitsLlmInvokePerAssistantMessage: boolean
  readonly emitsToolInvokeWhenExecutionStartedOrCompleted: boolean
  readonly systemPromptBucket: 'system_prompt'
  readonly conversationHistoryBucket: 'conversation_history'
  readonly toolResultBucket: 'tool_result_content'
  readonly toolResultWithheldWhenDocument: boolean
  readonly subagentRecordsFromDelegationCalls: boolean
}

/** The sanitized dual Copilot baseline: the OTLP snapshot plus the CLI file-transcript shape. */
export type CopilotDbBaselineSnapshot = {
  readonly harness: 'copilot'
  readonly sourceKind: 'otlp'
  readonly schemaVersion: 30
  readonly detectorVersion: 8
  readonly projectionSchemaVersion: 30
  readonly capturedFrom: string
  readonly capturedTo: string
  readonly recordsAtSnapshot: number
  readonly sessions: number
  readonly runs: number
  readonly turns: number
  readonly tokensRecordedPercent: number
  readonly projection: CopilotProjectionCoverage
  readonly systemPrompt: CopilotSystemPromptCoverage
  readonly toolDefinitions: CopilotToolDefinitionCoverage
  readonly toolInvocations: CopilotToolInvocationCoverage
  readonly agentInvocations: number
  readonly skills: number
  readonly rates: {
    readonly cacheHitRate: number
    readonly toolYield: number
  }
  readonly contentPartWithheld: {
    readonly conversationHistory: CopilotWithheldCoverage
    readonly toolResult: CopilotWithheldCoverage
  }
  readonly gaps: readonly string[]
  readonly copilotCli: CopilotCliTranscriptShape
}

/**
 * The observed `~/.kyberdash/canon.db` Copilot snapshot (read-only, 2026-11-26) and the
 * `CopilotCliMapper` shape it is paired with. Values are aggregate counts only.
 */
export const COPILOT_DB_BASELINE_SNAPSHOT: CopilotDbBaselineSnapshot = {
  harness: 'copilot',
  sourceKind: 'otlp',
  schemaVersion: 30,
  detectorVersion: 8,
  projectionSchemaVersion: 30,
  capturedFrom: '2026-11-26T15:46:35Z',
  capturedTo: '2026-11-26T15:49:31Z',
  recordsAtSnapshot: 316,
  sessions: 3,
  runs: 3,
  turns: 118,
  tokensRecordedPercent: 100,
  projection: {
    contextPercent: 100,
    schemaPercent: 100,
    tokensPercent: 100,
  },
  systemPrompt: {
    storedParts: 25,
    projectedTurns: 118,
    truncatedAtBytes: 65_536,
    truncatedParts: 23,
  },
  toolDefinitions: {
    rows: 1_090,
    turnsWithDefinitions: 19,
    rankedToolRows: 241,
  },
  toolInvocations: {
    spans: 184,
    withToolCallRows: 184,
  },
  agentInvocations: 6,
  skills: 16,
  rates: {
    cacheHitRate: 0.9172,
    toolYield: 0.0747,
  },
  contentPartWithheld: {
    conversationHistory: { withheld: 193, total: 205 },
    toolResult: { withheld: 183, total: 183 },
  },
  gaps: [
    'content_part_withheld',
    'tool_call_input_text_absent',
    'tool_call_result_text_absent',
    'session_request_sparse',
    'system_prompt_truncated',
  ],
  copilotCli: {
    harness: 'copilot-cli',
    schemaVersion: 30,
    emitsLlmInvokePerAssistantMessage: true,
    emitsToolInvokeWhenExecutionStartedOrCompleted: true,
    systemPromptBucket: 'system_prompt',
    conversationHistoryBucket: 'conversation_history',
    toolResultBucket: 'tool_result_content',
    toolResultWithheldWhenDocument: true,
    subagentRecordsFromDelegationCalls: true,
  },
}

/** One harness's entry in the parity matrix. */
export type HarnessParityExpectation = {
  readonly harness: string
  readonly supportTier: CopilotParitySupportTier
  readonly sourceKind: CopilotParitySourceKind
  /** Whether an isolated Playwright run can exercise this harness over HTTP. */
  readonly testable: boolean
  /** Content layers a parity comparison inspects for this harness. */
  readonly expectedDimensions: readonly CopilotParityDimension[]
  /** Documented collection or display gap markers observed for this harness. */
  readonly knownGaps: readonly string[]
}

const FILE_DIMENSIONS: readonly CopilotParityDimension[] = [
  'session-collection',
  'turn-projection',
  'token-accounting',
  'context-content',
  'tool-invocations',
  'skills',
  'user-requests',
  'stored-fields',
  'cost',
]

const FILE_SYSTEM_PROMPT_DIMENSIONS: readonly CopilotParityDimension[] = [...FILE_DIMENSIONS, 'system-prompt']

const OTLP_DIMENSIONS: readonly CopilotParityDimension[] = [
  ...FILE_DIMENSIONS,
  'system-prompt',
  'tool-definitions',
]

const DIAGNOSTIC_DIMENSIONS: readonly CopilotParityDimension[] = ['ingest-diagnostic']

type DimensionOptions = {
  readonly dimensions?: readonly CopilotParityDimension[]
  readonly gaps?: readonly string[]
}

function localFile(harness: string, options: DimensionOptions = {}): HarnessParityExpectation {
  return {
    harness,
    supportTier: 'local-files',
    sourceKind: 'file',
    testable: true,
    expectedDimensions: options.dimensions ?? FILE_DIMENSIONS,
    knownGaps: options.gaps ?? [],
  }
}

function otlp(
  harness: string,
  options: DimensionOptions & { readonly testable?: boolean } = {},
): HarnessParityExpectation {
  return {
    harness,
    supportTier: 'otlp-only',
    sourceKind: 'otlp',
    testable: options.testable ?? true,
    expectedDimensions: options.dimensions ?? OTLP_DIMENSIONS,
    knownGaps: options.gaps ?? [],
  }
}

function nonCollectable(
  harness: string,
  supportTier: 'otlp-only' | 'classified-only',
): HarnessParityExpectation {
  return {
    harness,
    supportTier,
    sourceKind: supportTier === 'otlp-only' ? 'otlp' : 'none',
    testable: false,
    expectedDimensions: [],
    knownGaps: [],
  }
}

function unevidenced(harness: string, gaps: readonly string[]): HarnessParityExpectation {
  return {
    harness,
    supportTier: 'unevidenced',
    sourceKind: 'none',
    testable: true,
    expectedDimensions: DIAGNOSTIC_DIMENSIONS,
    knownGaps: gaps,
  }
}

function excluded(harness: string): HarnessParityExpectation {
  return {
    harness,
    supportTier: 'excluded',
    sourceKind: 'none',
    testable: false,
    expectedDimensions: [],
    knownGaps: [],
  }
}

/**
 * Every canonical harness the dashboard recognises: the `24` `HarnessSourceRegistry` entries
 * plus OTLP `codex` (`25` canonical ids), and the `3` BL-3 excluded ids. The `22` testable
 * entries are the ones an isolated Playwright run can drive over HTTP.
 */
export const HARNESS_PARITY_MATRIX: readonly HarnessParityExpectation[] = [
  // LocalFiles (18): each has an evidenced local reader.
  localFile('antigravity', {
    dimensions: OTLP_DIMENSIONS,
    gaps: ['no_message_structure', 'tool_invocations_absent'],
  }),
  localFile('antigravity-cli'),
  localFile('claude-code', {
    dimensions: OTLP_DIMENSIONS,
    gaps: ['system_prompt_bucket_absent', 'partial_turn_projection', 'tool_call_input_text_absent'],
  }),
  localFile('cline'),
  localFile('cline-cli'),
  localFile('codex-cli'),
  localFile('commandcode'),
  localFile('copilot-cli', { dimensions: FILE_SYSTEM_PROMPT_DIMENSIONS }),
  localFile('copilot-vscode', { dimensions: FILE_SYSTEM_PROMPT_DIMENSIONS }),
  localFile('cursor', { gaps: ['cursor_workspace_unreadable'] }),
  localFile('devin', { gaps: ['devin_session_unreadable'] }),
  localFile('droid', {
    dimensions: OTLP_DIMENSIONS,
    gaps: ['no_message_structure', 'tool_definition_schema_absent', 'record_token_count_absent'],
  }),
  localFile('kilo-shared-runtime'),
  localFile('kilo-vscode-legacy'),
  localFile('kimi-code'),
  localFile('pi', { dimensions: OTLP_DIMENSIONS }),
  localFile('roo-code'),
  localFile('warp', { gaps: ['sqlite_source_unreadable'] }),

  // OtlpOnly (3 registry entries) plus OTLP `codex` mapped by CodexAdapter.
  otlp('copilot', {
    gaps: [
      'content_part_withheld',
      'tool_call_input_text_absent',
      'tool_call_result_text_absent',
      'session_request_sparse',
      'system_prompt_truncated',
    ],
  }),
  otlp('opencode', { gaps: ['skill_listing_absent'] }),
  otlp('codex', { gaps: ['no_message_structure'] }),
  nonCollectable('copilot-agent', 'otlp-only'),

  // ClassifiedOnly (2): classified surfaces with no reader of their own.
  nonCollectable('claude-unclassified', 'classified-only'),
  nonCollectable('codex-desktop', 'classified-only'),

  // Unevidenced (1): discovered on disk but its format cannot be parsed.
  unevidenced('zcode', ['source_format_unevidenced']),

  // Excluded (3, BL-3): never collected or displayed as coding harnesses.
  excluded('windsurf'),
  excluded('aider'),
  excluded('gemini'),
]

/** Filter applied by {@link listParityExpectations}; every field present narrows the result. */
export type ParityExpectationFilter = {
  readonly testableOnly?: boolean
  readonly supportTier?: CopilotParitySupportTier
  readonly sourceKind?: CopilotParitySourceKind
}

/**
 * Lowercases ASCII letters only, so a non-ASCII character keeps its case, then trims and folds
 * the identifier the way `CanonPricing.NormalizeHarnessName` does.
 */
export function normalizeParityHarnessId(harness: string): string {
  const lower = harness.trim().replace(/[A-Z]/g, (char) => String.fromCharCode(char.charCodeAt(0) + 32))
  if (lower === 'claude-cli' || lower === 'claude-unclassified') return lower
  if (lower === 'claude-desktop') return 'claude-code'
  if (lower === 'claude' || lower === 'claude-code') return lower === 'claude' ? 'claude-unclassified' : 'claude-code'
  if (
    lower === 'copilot-cli' ||
    lower === 'copilot-vscode' ||
    lower === 'copilot-jetbrains' ||
    lower === 'copilot-agent'
  ) {
    return lower
  }
  if (lower === 'copilot-chat') return 'copilot-vscode'
  if (lower === 'copilot' || lower === 'github-copilot') return 'copilot'
  if (lower === 'cursor-agent' || lower === 'cursor') return 'cursor'
  if (lower === 'windsurf' || lower === 'cascade') return 'windsurf'
  if (lower === 'roo' || lower === 'roo-code' || lower === 'roo-cline') return 'roo-code'
  if (lower === 'cline-cli' || lower === 'cline' || lower === 'aider') return lower
  if (lower === 'codex-cli' || lower === 'codex-desktop' || lower === 'codex-unclassified') return lower
  if (lower === 'codex' || lower === 'openai-codex') return lower === 'openai-codex' ? 'codex-unclassified' : 'codex'
  if (lower === 'gemini') return 'gemini'
  if (lower === 'antigravity-cli' || lower === 'antigravity-ide') return lower
  if (lower === 'antigravity' || lower === 'agy') return 'antigravity'
  if (lower === 'opencode') return 'opencode'
  if (lower === 'kilo-vscode-legacy') return 'kilo-vscode-legacy'
  if (
    lower === 'kilo-shared-runtime' ||
    lower === 'kilo' ||
    lower === 'kilo-code' ||
    lower.startsWith('kilo')
  ) {
    return 'kilo-shared-runtime'
  }
  if (lower === 'kimicode' || lower === 'kimi-code') return 'kimi-code'
  if (lower === 'commandcode' || lower === 'command-code') return 'commandcode'
  return lower
}

/** Returns the matrix entries matching every supplied filter field, as a fresh array. */
export function listParityExpectations(filter: ParityExpectationFilter = {}): HarnessParityExpectation[] {
  return HARNESS_PARITY_MATRIX.filter((entry) => {
    if (filter.testableOnly === true && !entry.testable) return false
    if (filter.supportTier !== undefined && entry.supportTier !== filter.supportTier) return false
    if (filter.sourceKind !== undefined && entry.sourceKind !== filter.sourceKind) return false
    return true
  })
}

/**
 * Looks up one matrix entry by raw or canonical harness id.
 *
 * @throws {Error} `Unknown parity harness: <id>` when the id normalizes to nothing in the matrix.
 */
export function getParityExpectation(harnessId: string): HarnessParityExpectation {
  const normalized = normalizeParityHarnessId(harnessId)
  const found = HARNESS_PARITY_MATRIX.find((entry) => entry.harness === normalized)
  if (found === undefined) {
    throw new Error(`Unknown parity harness: ${harnessId}`)
  }
  return found
}

// Exact object keys whose presence means verbatim store content slipped into a contract.
// Matching is on the whole key, not a substring, so `systemPrompt` or `knownGaps` stay legal.
const FORBIDDEN_KEYS: ReadonlySet<string> = new Set([
  'prompt',
  'prompts',
  'raw',
  'rawtext',
  'transcript',
  'transcripttext',
  'title',
  'sessiontitle',
  'workspace',
  'cwd',
  'home',
  'homedir',
  'content',
  'contenttext',
  'parttext',
  'message',
  'messages',
  'email',
  'apikey',
  'api_key',
  'password',
  'secret',
  'token',
  'snippet',
])

const EMAIL_PATTERN = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/
const HOME_PATH_PATTERN = /(?:^|[\\/])(?:Users|home)[\\/][^\\/]+/
const SECRET_PATTERN = /\b(?:sk|ghp|gho|ghu|xox[baprs])[-_][A-Za-z0-9]{8,}\b/

function leak(detail: string): never {
  throw new Error(`Parity fixture violates non-PII boundary: ${detail}`)
}

function inspectForLeak(value: unknown, path: string): void {
  if (typeof value === 'string') {
    if (EMAIL_PATTERN.test(value)) leak(`email-like value at ${path}`)
    if (HOME_PATH_PATTERN.test(value)) leak(`home path value at ${path}`)
    if (SECRET_PATTERN.test(value)) leak(`secret-like value at ${path}`)
    return
  }

  if (Array.isArray(value)) {
    value.forEach((item, index) => inspectForLeak(item, `${path}[${index}]`))
    return
  }

  if (value !== null && typeof value === 'object') {
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      if (FORBIDDEN_KEYS.has(key.toLowerCase())) {
        leak(`forbidden raw-content key '${key}' at ${path}`)
      }
      inspectForLeak(child, `${path}.${key}`)
    }
  }
}

/**
 * Walks a committed parity fixture and rejects verbatim store content: forbidden raw-content
 * keys, email-like values, user-home paths, and secret-like tokens.
 *
 * @throws {Error} `Parity fixture violates non-PII boundary: <detail>` on the first violation.
 */
export function assertNoRawCanonDbLeak(fixture: unknown): void {
  inspectForLeak(fixture, 'root')
}
