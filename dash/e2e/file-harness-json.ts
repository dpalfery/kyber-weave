// Two sessions per JSON-format file-import harness, each 10 or 12 turns, in the harness's native
// shape (copied from its mapper tests under dash/server.tests/KyberDash.Data.Tests/Harnesses).
// Timestamps derive from the run clock passed in (D10). A contract states each section as
// measured or as its explicit not-measurable state, from the mapper tests and
// docs/dash/telemetry-inventory.md, never from what the screen shows.
import type { FileHarnessFixture } from './file-harness-contracts.js'
import {
  BASE_CONTRACT,
  TURN_SPACING_MS,
  fixturesFor,
  hasTool,
  iso,
  toolCallCount,
  type SessionPlan,
} from './file-harness-jsonl.js'

/** The VS Code user-data directory the registry scans for the running platform, under the home. */
function vscodeUserDir(): string {
  if (process.platform === 'darwin') {
    return 'Library/Application Support/Code/User'
  }
  if (process.platform === 'win32') {
    return 'AppData/Roaming/Code/User'
  }
  return '.config/Code/User'
}

// --- cline-family ui_messages.json (cline, roo-code, kilo-vscode-legacy) ----------------------
// One `api_req_started` entry per turn carries the counters; the first `user_feedback` message
// is the user prompt. The tree holds no tool records, no model and no per-turn history.
function uiMessagesDocument(plan: SessionPlan, baseTimeMs: number): string {
  const messages: unknown[] = [
    { type: 'say', say: 'user_feedback', text: `Start: ${plan.id}`, ts: baseTimeMs },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    messages.push({
      type: 'say',
      say: 'api_req_started',
      text: JSON.stringify({
        tokensIn: 200 + turn,
        tokensOut: 100 + turn,
        cacheReads: 50,
        cacheWrites: 30,
        cost: 0.05,
      }),
      ts: baseTimeMs + turn * TURN_SPACING_MS,
    })
  }
  return JSON.stringify(messages)
}

function uiMessagesFixtures(harness: string, tasksRoot: string): FileHarnessFixture[] {
  return fixturesFor(
    harness,
    (plan) => ({
      ...BASE_CONTRACT,
      harness,
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: null,
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'harness',
      expectedModels: ['cline-auto'],
    }),
    (plan, base) => [
      { relativePath: `${vscodeUserDir()}/globalStorage/${tasksRoot}/tasks/${plan.id}/ui_messages.json`, content: uiMessagesDocument(plan, base) },
    ],
  )
}

// --- cline-cli: ~/.cline/data/sessions/<id>/<id>.messages.json -------------------------------
const CLINE_CLI_MODEL = 'z-ai/glm-5.2'

function clineCliDocument(plan: SessionPlan, baseTimeMs: number): string {
  const messages: unknown[] = []
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    messages.push({
      id: `${plan.id}-u-${turn}`,
      role: 'user',
      content: [{ type: 'text', text: `Step ${turn}: continue the work` }],
      ts: at,
    })
    const content: unknown[] = [{ type: 'text', text: `Finished step ${turn}.` }]
    if (hasTool(turn)) {
      content.push({
        type: 'tool_use',
        id: `${plan.id}-call-${turn}`,
        name: 'run_commands',
        input: { commands: ['npm test'] },
      })
    }
    messages.push({
      id: `${plan.id}-a-${turn}`,
      role: 'assistant',
      content,
      ts: at + 1_000,
      metrics: {
        inputTokens: 120 + turn,
        outputTokens: 40 + turn,
        cacheReadTokens: 1000,
        cacheWriteTokens: 50,
        cost: 0.02,
      },
      modelInfo: { id: CLINE_CLI_MODEL },
    })
  }
  return JSON.stringify({ version: 1, sessionId: plan.id, messages })
}

// --- copilot-vscode: <workspaceStorage>/<workspace>/chatSessions/<session>.jsonl -------------
const COPILOT_VSCODE_MODEL = 'gpt-5'

function copilotVscodeJournal(plan: SessionPlan, baseTimeMs: number): string {
  const lines: unknown[] = [
    { kind: 0, v: { sessionId: plan.id, creationDate: iso(baseTimeMs), requests: [] } },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const metadata: Record<string, unknown> = {
      promptTokens: 180 + turn,
      outputTokens: 30 + turn,
      resolvedModel: COPILOT_VSCODE_MODEL,
    }
    if (hasTool(turn)) {
      metadata['toolCallRounds'] = [
        { toolCalls: [{ id: `${plan.id}-tc-${turn}`, name: 'read_file', arguments: { path: `src/file-${turn}.ts` } }] },
      ]
    }
    lines.push({
      kind: 2,
      k: ['requests'],
      v: [
        {
          requestId: `${plan.id}-request-${turn}`,
          timestamp: iso(baseTimeMs + turn * TURN_SPACING_MS),
          modelId: `copilot/${COPILOT_VSCODE_MODEL}`,
          message: {
            text: `Step ${turn}: continue the work`,
            parts: [{ type: 'text', text: `Step ${turn}: continue the work` }],
            modeInstructions: 'Work in agent mode and keep edits scoped.',
          },
          modeInstructions: 'Work in agent mode and keep edits scoped.',
          result: { metadata },
          response: { text: `Finished step ${turn}.`, parts: [{ type: 'text', text: `Finished step ${turn}.` }] },
        },
      ],
    })
  }
  return `${lines.map((line) => JSON.stringify(line)).join('\n')}\n`
}

// --- devin: ~/.local/share/devin/cli/transcripts/<name>.json ---------------------------------
const DEVIN_MODEL = 'claude-sonnet-4-6'

function devinTranscript(plan: SessionPlan, baseTimeMs: number): string {
  const steps: unknown[] = []
  let stepId = 1
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    steps.push({
      step_id: stepId++,
      message: `Step ${turn}: continue the work`,
      metadata: { is_user_input: true, created_at: iso(at) },
    })
    steps.push({
      step_id: stepId++,
      model_name: DEVIN_MODEL,
      metadata: {
        created_at: iso(at + 1_000),
        committed_acu_cost: 0.02,
        metrics: {
          input_tokens: 100 + turn,
          output_tokens: 20 + turn,
          cache_creation_tokens: 10,
          cache_read_tokens: 5,
        },
      },
      ...(hasTool(turn)
        ? { tool_calls: [{ tool_call_id: `${plan.id}-tc-${turn}`, function_name: 'read_file' }] }
        : {}),
    })
  }
  return JSON.stringify({ schema_version: '1', session_id: plan.id, agent: { model_name: DEVIN_MODEL }, steps })
}

/** JSON-document harnesses (T6). */
export const FILE_HARNESS_JSON_FIXTURES: readonly FileHarnessFixture[] = [
  ...uiMessagesFixtures('cline', 'saoudrizwan.claude-dev'),
  ...fixturesFor(
    'cline-cli',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'cline-cli',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'harness',
      expectedModels: [CLINE_CLI_MODEL],
    }),
    (plan, base) => [
      {
        relativePath: `.cline/data/sessions/${plan.id}/${plan.id}.messages.json`,
        content: clineCliDocument(plan, base),
      },
    ],
  ),
  ...uiMessagesFixtures('kilo-vscode-legacy', 'kilocode.kilo-code'),
  ...fixturesFor(
    'copilot-vscode',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'copilot-vscode',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'no_rate',
      expectedModels: [COPILOT_VSCODE_MODEL],
    }),
    (plan, base) => [
      {
        relativePath: `${vscodeUserDir()}/workspaceStorage/e2e-workspace/chatSessions/${plan.id}.jsonl`,
        content: copilotVscodeJournal(plan, base),
      },
    ],
  ),
  ...fixturesFor(
    'devin',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'devin',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'out_of_scope',
      expectedModels: ['Sonnet 4.6'],
    }),
    (plan, base) => [
      {
        relativePath: `.local/share/devin/cli/transcripts/${plan.id}.json`,
        content: devinTranscript(plan, base),
      },
    ],
  ),
]
