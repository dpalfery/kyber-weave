// Two sessions per JSONL file-import harness, each 10 or 12 turns, in the harness's native
// shape (copied from its mapper tests under dash/server.tests/KyberDash.Data.Tests/Harnesses).
// Timestamps derive from the run clock passed in (D10). A contract states each section as
// measured or as its explicit not-measurable state, from the mapper tests and
// docs/dash/telemetry-inventory.md, never from what the screen shows.
import type { FileHarnessContract, FileHarnessFixture } from './file-harness-contracts.js'
import type { HarnessFile } from './file-harness-world.js'

export const TURN_SPACING_MS = 30_000
const TOOL_EVERY = 3

export type SessionPlan = { readonly id: string; readonly turns: number }

export function plansFor(harness: string): readonly [SessionPlan, SessionPlan] {
  return [
    { id: `file-e2e-${harness}-a-12t`, turns: 12 },
    { id: `file-e2e-${harness}-b-10t`, turns: 10 },
  ]
}

/** Tool calls the builders emit: one on every third turn. */
export function toolCallCount(turns: number): number {
  return Math.floor(turns / TOOL_EVERY)
}

export function hasTool(turn: number): boolean {
  return turn % TOOL_EVERY === 0
}

export function iso(ms: number): string {
  return new Date(ms).toISOString()
}

function jsonl(lines: readonly unknown[]): string {
  return `${lines.map((line) => JSON.stringify(line)).join('\n')}\n`
}

/** Contract fields every JSONL harness shares unless it says otherwise. */
export const BASE_CONTRACT = {
  cacheCreationMeasurable: false,
  contextMeasurable: false,
  schemaMeasurable: false,
  skillsMeasurable: false,
  pairedWithSessionFiles: false,
  expectedSkills: [] as readonly string[],
  expectedGapSubstrings: [] as readonly string[],
  sendsLogs: false,
} as const

export function fixturesFor(
  harness: string,
  contractFor: (plan: SessionPlan) => FileHarnessContract,
  filesFor: (plan: SessionPlan, baseTimeMs: number) => readonly HarnessFile[],
): FileHarnessFixture[] {
  return plansFor(harness).map((plan) => ({
    contract: contractFor(plan),
    files: (baseTimeMs: number) => filesFor(plan, baseTimeMs),
  }))
}

// --- claude-code: ~/.claude/projects/<slug>/<session>.jsonl --------------------------------
const CLAUDE_MODEL = 'claude-sonnet-4-5'

function claudeLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = []
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      type: 'user',
      sessionId: plan.id,
      timestamp: iso(at),
      ...(turn === 1 ? { customTitle: `Claude file import ${plan.id}` } : {}),
      message: { role: 'user', content: [{ type: 'text', text: `Step ${turn}: continue the work` }] },
    })
    const content: unknown[] = [{ type: 'text', text: `Finished step ${turn}.` }]
    if (hasTool(turn)) {
      content.push({ type: 'tool_use', id: `${plan.id}-tc-${turn}`, name: 'read_file', input: { path: `file-${turn}.txt` } })
    }
    lines.push({
      type: 'assistant',
      sessionId: plan.id,
      timestamp: iso(at + 1_000),
      message: {
        id: `${plan.id}-msg-${turn}`,
        role: 'assistant',
        model: CLAUDE_MODEL,
        content,
        usage: {
          input_tokens: 100 + turn,
          output_tokens: 40 + turn,
          cache_read_input_tokens: 500,
          cache_creation_input_tokens: 40,
        },
      },
    })
    if (hasTool(turn)) {
      lines.push({
        type: 'user',
        sessionId: plan.id,
        timestamp: iso(at + 2_000),
        message: {
          role: 'user',
          content: [{ type: 'tool_result', tool_use_id: `${plan.id}-tc-${turn}`, content: `contents ${turn}` }],
        },
      })
    }
  }
  return lines
}

// --- codex: ~/.codex/sessions/<y>/<m>/<d>/rollout-*.jsonl ----------------------------------
const CODEX_MODEL = 'gpt-5.2-codex'

function codexLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = [
    {
      timestamp: iso(baseTimeMs),
      type: 'session_meta',
      payload: {
        session_id: plan.id,
        model: CODEX_MODEL,
        cwd: '/workspace/repo',
        base_instructions: { text: 'System instructions' },
      },
    },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      timestamp: iso(at),
      type: 'response_item',
      payload: { type: 'message', role: 'user', content: [{ type: 'input_text', text: `Step ${turn}: continue the work` }] },
    })
    if (hasTool(turn)) {
      lines.push({
        timestamp: iso(at + 500),
        type: 'response_item',
        payload: {
          type: 'function_call',
          call_id: `${plan.id}-call-${turn}`,
          name: 'shell',
          arguments: JSON.stringify({ command: 'git status' }),
        },
      })
      lines.push({
        timestamp: iso(at + 750),
        type: 'response_item',
        payload: { type: 'function_call_output', call_id: `${plan.id}-call-${turn}`, output: 'nothing to commit' },
      })
    }
    lines.push({
      timestamp: iso(at + 1_000),
      type: 'event_msg',
      payload: {
        type: 'token_count',
        info: {
          model: CODEX_MODEL,
          last_token_usage: {
            input_tokens: 130 + turn,
            cached_input_tokens: 30,
            output_tokens: 20 + turn,
            reasoning_output_tokens: 4,
          },
        },
      },
    })
    lines.push({ timestamp: iso(at + 1_100), type: 'event_msg', payload: { type: 'task_complete' } })
  }
  return lines
}

// --- pi: ~/.pi/agent/sessions/<slug>/<session>.jsonl ---------------------------------------
const PI_MODEL = 'gpt-5.4'

function piLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = [
    { type: 'session', version: 3, id: plan.id, timestamp: iso(baseTimeMs), cwd: '/Users/test/myproject' },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      type: 'message',
      id: `${plan.id}-u-${turn}`,
      timestamp: iso(at),
      message: { role: 'user', content: [{ type: 'text', text: `Step ${turn}: continue the work` }] },
    })
    const content: unknown[] = [{ type: 'text', text: `Finished step ${turn}.` }]
    if (hasTool(turn)) {
      content.push({ type: 'toolCall', id: `${plan.id}-c-${turn}`, name: 'bash', arguments: { command: 'git status' } })
    }
    lines.push({
      type: 'message',
      id: `${plan.id}-a-${turn}`,
      timestamp: iso(at + 1_000),
      message: {
        role: 'assistant',
        content,
        model: PI_MODEL,
        responseId: `${plan.id}-resp-${turn}`,
        usage: { input: 2000 + turn, output: 400 + turn, cacheRead: 5000, cacheWrite: 100, cost: { total: 0.0055 } },
      },
    })
  }
  return lines
}

// --- commandcode: ~/.commandcode/projects/<slug>/<session>.jsonl --------------------------
const COMMANDCODE_MODEL = 'claude-opus-4-6'

function commandcodeLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = [
    { type: 'session', version: 3, id: plan.id, timestamp: iso(baseTimeMs), cwd: '/Users/test/myproject' },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      type: 'message',
      id: `${plan.id}-u-${turn}`,
      timestamp: iso(at),
      message: { role: 'user', content: [{ type: 'text', text: `Step ${turn}: continue the work` }] },
    })
    const content: unknown[] = [{ type: 'text', text: `Finished step ${turn}.` }]
    if (hasTool(turn)) {
      content.push({ type: 'tool_use', name: 'read_file', input: { path: `file-${turn}.ts` } })
    }
    lines.push({
      type: 'message',
      id: `${plan.id}-a-${turn}`,
      timestamp: iso(at + 1_000),
      model: COMMANDCODE_MODEL,
      message: { role: 'assistant', content },
      usage: {
        inputTokens: 1500 + turn,
        outputTokens: 200 + turn,
        cacheReadTokens: 1000,
        cacheWriteTokens: 100,
        costUsd: 0.012,
      },
    })
  }
  return lines
}

// --- kimi-code: ~/.kimi-code/sessions/<wd>/session_<id>/agents/main/wire.jsonl -----------
const KIMI_MODEL = 'kimi-k3'

function kimiLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = []
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    const alias = `${plan.id}-alias-${turn}`
    lines.push({
      type: 'turn.prompt',
      input: [{ type: 'text', text: `Step ${turn}: continue the work` }],
      origin: { kind: 'user' },
      time: at,
    })
    lines.push({
      type: 'llm.request',
      kind: 'loop',
      provider: 'fixture-provider',
      model: KIMI_MODEL,
      modelAlias: alias,
      maxTokens: 4096,
      messageCount: 2,
      turnStep: `${turn - 1}.1`,
      time: at + 1_000,
    })
    if (hasTool(turn)) {
      lines.push({
        type: 'context.append_loop_event',
        event: {
          type: 'tool.call',
          name: 'Read',
          args: { path: `notes-${turn}.txt` },
          turnId: turn - 1,
          step: 1,
          toolCallId: `${plan.id}-tool-${turn}`,
        },
      })
    }
    lines.push({
      type: 'usage.record',
      model: alias,
      usage: { inputOther: 20 + turn, output: 5 + turn, inputCacheRead: 100, inputCacheCreation: 10 },
      usageScope: 'turn',
      time: at + 2_000,
    })
  }
  return lines
}


// --- copilot-cli: ~/.copilot/session-state/<session>/events.jsonl -----------------------------
const COPILOT_MODEL = 'claude-sonnet-4.5'

function copilotLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = [
    {
      type: 'session.start',
      id: `${plan.id}-e0`,
      timestamp: iso(baseTimeMs),
      data: { sessionId: plan.id, selectedModel: COPILOT_MODEL },
    },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      type: 'user.message',
      id: `${plan.id}-u${turn}`,
      timestamp: iso(at),
      data: { content: `Step ${turn}: continue the work` },
    })
    lines.push({
      type: 'assistant.message',
      id: `${plan.id}-a${turn}`,
      timestamp: iso(at + 1_000),
      data: {
        messageId: `${plan.id}-msg-${turn}`,
        content: `Finished step ${turn}.`,
        inputTokens: 900 + turn,
        outputTokens: 145 + turn,
        ...(hasTool(turn)
          ? { toolRequests: [{ toolCallId: `${plan.id}-tc-${turn}`, name: 'view', arguments: { path: `file-${turn}.ts` } }] }
          : {}),
      },
    })
  }
  return lines
}

// --- droid (file-only): ~/.factory/sessions/<slug>/<session>.jsonl + .settings.json ----------
const DROID_MODEL = 'GLM-5.1'

function droidLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = [
    { type: 'session_start', id: plan.id, title: `Droid file import ${plan.id}`, cwd: '/home/dev/projects/kyber-weave' },
  ]
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      type: 'message',
      timestamp: iso(at),
      message: { role: 'user', content: [{ type: 'text', text: `Step ${turn}: continue the work` }] },
    })
    lines.push({
      type: 'message',
      id: `${plan.id}-a-${turn}`,
      timestamp: iso(at + 2_500),
      message: { role: 'assistant', content: [{ type: 'text', text: `Finished step ${turn}.` }] },
    })
  }
  return lines
}

function droidSettings(plan: SessionPlan): string {
  return JSON.stringify({
    model: `custom:${DROID_MODEL}-[Proxy]-0`,
    tokenUsage: {
      inputTokens: 101 * plan.turns,
      outputTokens: 41 * plan.turns,
      cacheCreationTokens: 10 * plan.turns,
      cacheReadTokens: 203 * plan.turns,
      thinkingTokens: 7 * plan.turns,
    },
  })
}

// --- antigravity and antigravity-cli: <root>/brain/<conversation>/.system_generated/logs/transcript.jsonl
function encodeTwice(value: unknown): string {
  return JSON.stringify(JSON.stringify(value))
}

function antigravityLines(plan: SessionPlan, baseTimeMs: number): unknown[] {
  const lines: unknown[] = []
  let step = 0
  for (let turn = 1; turn <= plan.turns; turn += 1) {
    const at = baseTimeMs + turn * TURN_SPACING_MS
    lines.push({
      step_index: step++,
      source: 'USER_EXPLICIT',
      type: 'USER_INPUT',
      status: 'DONE',
      created_at: iso(at),
      content: `Step ${turn}: continue the work`,
    })
    lines.push({
      step_index: step++,
      source: 'MODEL',
      type: 'PLANNER_RESPONSE',
      status: 'DONE',
      created_at: iso(at + 1_000),
      content: `Finished step ${turn}.`,
      ...(hasTool(turn)
        ? {
            tool_calls: [
              {
                name: 'view_file',
                args: { AbsolutePath: encodeTwice(`/synthetic/workspace/src/file-${turn}.ts`), StartLine: encodeTwice(12) },
              },
            ],
          }
        : {}),
    })
    if (hasTool(turn)) {
      lines.push({
        step_index: step++,
        source: 'SYSTEM',
        type: 'GENERIC',
        status: 'DONE',
        created_at: iso(at + 2_000),
        content: `synthetic file contents ${turn}`,
      })
    }
  }
  return lines
}


function antigravityFixtures(harness: string, brainRoot: string): FileHarnessFixture[] {
  return fixturesFor(
    harness,
    (plan) => ({
      ...BASE_CONTRACT,
      harness,
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: false,
      contextMeasurable: false,
      perTurnTokensMeasurable: false,
      expectedCostStatus: 'out_of_scope',
      expectedModels: [],
    }),
    (plan, base) => [
      {
        relativePath: `${brainRoot}/${plan.id}/.system_generated/logs/transcript.jsonl`,
        content: jsonl(antigravityLines(plan, base)),
      },
    ],
  )
}

export const FILE_HARNESS_JSONL_FIXTURES: readonly FileHarnessFixture[] = [
  ...fixturesFor(
    'claude-code',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'claude-code',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'published_priced',
      expectedModels: [CLAUDE_MODEL],
    }),
    (plan, base) => [
      { relativePath: `.claude/projects/e2e-project/${plan.id}.jsonl`, content: jsonl(claudeLines(plan, base)) },
    ],
  ),
  ...fixturesFor(
    'codex-cli',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'codex-cli',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'published_priced',
      expectedModels: [CODEX_MODEL],
    }),
    (plan, base) => [
      { relativePath: `.codex/sessions/2026/11/25/rollout-${plan.id}.jsonl`, content: jsonl(codexLines(plan, base)) },
    ],
  ),
  ...fixturesFor(
    'pi',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'pi',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'harness',
      expectedModels: [PI_MODEL],
    }),
    (plan, base) => [
      { relativePath: `.pi/agent/sessions/--e2e-project--/${plan.id}.jsonl`, content: jsonl(piLines(plan, base)) },
    ],
  ),
  ...fixturesFor(
    'commandcode',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'commandcode',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'harness',
      expectedModels: [COMMANDCODE_MODEL],
    }),
    (plan, base) => [
      {
        relativePath: `.commandcode/projects/e2e-project/${plan.id}.jsonl`,
        content: jsonl(commandcodeLines(plan, base)),
      },
    ],
  ),
  ...fixturesFor(
    'kimi-code',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'kimi-code',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'out_of_scope',
      expectedModels: [KIMI_MODEL],
    }),
    (plan, base) => [
      {
        relativePath: `.kimi-code/sessions/wd_e2e-project_0123456789ab/session_${plan.id}/agents/main/wire.jsonl`,
        content: jsonl(kimiLines(plan, base)),
      },
    ],
  ),
  ...fixturesFor(
    'copilot-cli',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'copilot-cli',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: null,
      tokensMeasurable: true,
      contextMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedCostStatus: 'no_rate',
      expectedModels: [COPILOT_MODEL],
    }),
    (plan, base) => [
      { relativePath: `.copilot/session-state/${plan.id}/events.jsonl`, content: jsonl(copilotLines(plan, base)) },
    ],
  ),
  ...fixturesFor(
    'droid',
    (plan) => ({
      ...BASE_CONTRACT,
      harness: 'droid',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: null,
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: false,
      perTurnTokensMeasurable: false,
      expectedCostStatus: 'published_priced',
      expectedModels: [DROID_MODEL],
    }),
    (plan, base) => [
      { relativePath: `.factory/sessions/e2e-project/${plan.id}.jsonl`, content: jsonl(droidLines(plan, base)) },
      { relativePath: `.factory/sessions/e2e-project/${plan.id}.settings.json`, content: droidSettings(plan) },
    ],
  ),
  ...antigravityFixtures('antigravity', '.gemini/antigravity/brain'),
  ...antigravityFixtures('antigravity-cli', '.gemini/antigravity-cli/brain'),
]

// ── Baseline-parity fixtures ────────────────────────────────────────────────
//
// One richer session per harness than the FILE_HARNESS_JSONL_FIXTURES contracts above, used by
// dash/e2e/harness-content-parity.spec.ts to compare each harness's collection and display against
// the dual Copilot baseline. They carry synthetic text only: the same structural shapes the
// observed `~/.kyberdash/canon.db` snapshot showed (system prompt bucketing, reasoning, tool I/O,
// withheld JSON documents), never a transcript from any real store.

const PARITY_TURNS = 6

/** One parity session per harness, sized to the shared turn count. */
function parityPlan(harness: string): SessionPlan {
  return { id: `parity-${harness}-${PARITY_TURNS}t`, turns: PARITY_TURNS }
}

// --- copilot-cli full-content baseline: system prompt, reasoning, tool I/O, withheld document ---
const COPILOT_CLI_PARITY_MODEL = 'claude-sonnet-4.5'
const COPILOT_CLI_PARITY_SYSTEM = 'You are a coding agent verifying telemetry capture parity.'
const COPILOT_CLI_PARITY_DOCUMENT = JSON.stringify({
  files: Array.from({ length: 40 }, (_, index) => ({ path: `src/parity-module-${index}.ts`, bytes: 1_024 + index })),
})

/** Builds the rich Copilot CLI journal: the file half of the dual Copilot baseline. */
export function buildCopilotCliParityFixture(): FileHarnessFixture {
  const plan = parityPlan('copilot-cli')
  return {
    contract: {
      ...BASE_CONTRACT,
      harness: 'copilot-cli',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: plan.turns,
      tokensMeasurable: true,
      cacheCreationMeasurable: false,
      contextMeasurable: true,
      schemaMeasurable: false,
      skillsMeasurable: true,
      perTurnTokensMeasurable: true,
      expectedSkills: ['tdd-workflow'],
      expectedCostStatus: 'no_rate',
      expectedModels: [COPILOT_CLI_PARITY_MODEL],
    },
    files: (baseTimeMs) => {
      const lines: unknown[] = [
        {
          type: 'session.start',
          id: `${plan.id}-e0`,
          timestamp: iso(baseTimeMs),
          data: {
            sessionId: plan.id,
            selectedModel: COPILOT_CLI_PARITY_MODEL,
            copilotVersion: '1.0.99',
            context: { cwd: '/synthetic/workspace', branch: 'main' },
          },
        },
        {
          type: 'system.message',
          id: `${plan.id}-e1`,
          timestamp: iso(baseTimeMs + 100),
          data: { content: COPILOT_CLI_PARITY_SYSTEM },
        },
      ]
      for (let turn = 1; turn <= plan.turns; turn += 1) {
        const at = baseTimeMs + turn * TURN_SPACING_MS
        const toolCallId = `${plan.id}-call-${turn}`
        lines.push({
          type: 'user.message',
          id: `${plan.id}-u${turn}`,
          timestamp: iso(at),
          data: { content: `Parity step ${turn}: continue the work` },
        })
        lines.push({
          type: 'assistant.reasoning',
          id: `${plan.id}-r${turn}`,
          timestamp: iso(at + 200),
          data: { reasoningText: `Parity step ${turn}: review the captured context.` },
        })
        lines.push({
          type: 'assistant.message',
          id: `${plan.id}-a${turn}`,
          timestamp: iso(at + 1_000),
          data: {
            messageId: `${plan.id}-msg-${turn}`,
            content: `Finished parity step ${turn}.`,
            inputTokens: 900 + turn,
            outputTokens: 145 + turn,
            cacheReadTokens: 300,
            toolRequests: [{ toolCallId, name: 'view', arguments: { path: `src/parity-${turn}.ts` } }],
          },
        })
        lines.push({
          type: 'tool.execution_start',
          id: `${plan.id}-s${turn}`,
          parentId: `${plan.id}-a${turn}`,
          timestamp: iso(at + 1_100),
          data: { toolCallId, toolName: 'view', arguments: { path: `src/parity-${turn}.ts` } },
        })
        const result = turn === 2
          ? { items: JSON.parse(COPILOT_CLI_PARITY_DOCUMENT) }
          : { content: `contents of src/parity-${turn}.ts` }
        lines.push({
          type: 'tool.execution_complete',
          id: `${plan.id}-c${turn}`,
          parentId: `${plan.id}-s${turn}`,
          timestamp: iso(at + 1_450),
          data: { toolCallId, toolName: 'view', success: true, result },
        })
      }
      lines.push({
        type: 'skill.invoked',
        id: `${plan.id}-skill`,
        timestamp: iso(baseTimeMs + TURN_SPACING_MS + 1_600),
        data: { skillName: 'tdd-workflow' },
      })
      lines.push({
        type: 'session.shutdown',
        id: `${plan.id}-shutdown`,
        timestamp: iso(baseTimeMs + plan.turns * TURN_SPACING_MS + 2_000),
        data: {
          modelMetrics: [
            {
              model: COPILOT_CLI_PARITY_MODEL,
              inputTokens: plan.turns * 900,
              outputTokens: plan.turns * 145,
              cacheReadTokens: 300,
            },
          ],
        },
      })
      return [
        { relativePath: `.copilot/session-state/${plan.id}/events.jsonl`, content: jsonl(lines) },
      ]
    },
  }
}

// --- droid collection-gap shape: conversation content without message structure or counters -----
const DROID_PARITY_MODEL = 'GLM-5.1'

/**
 * Builds a droid session matching the `canon.db` collection gap: rich conversation content, but
 * no message structure to project context from, no tool-definition schema, and no token counters
 * (no companion settings file). Its contract names those gaps explicitly.
 */
export function buildDroidParityFixture(): FileHarnessFixture {
  const plan = parityPlan('droid')
  return {
    contract: {
      ...BASE_CONTRACT,
      harness: 'droid',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: null,
      tokensMeasurable: false,
      cacheCreationMeasurable: false,
      contextMeasurable: false,
      schemaMeasurable: false,
      skillsMeasurable: false,
      perTurnTokensMeasurable: false,
      expectedSkills: [],
      expectedCostStatus: 'out_of_scope',
      expectedModels: [DROID_PARITY_MODEL],
    },
    files: (baseTimeMs) => [
      {
        relativePath: `.factory/sessions/parity-workspace/${plan.id}.jsonl`,
        content: jsonl([
          {
            type: 'session_start',
            id: plan.id,
            title: `Droid parity gap ${plan.id}`,
            cwd: '/synthetic/workspace',
          },
          ...Array.from({ length: plan.turns }, (_, index) => {
            const turn = index + 1
            const at = baseTimeMs + turn * TURN_SPACING_MS
            return [
              {
                type: 'message',
                timestamp: iso(at),
                message: { role: 'user', content: [{ type: 'text', text: `Parity step ${turn}: continue the work` }] },
              },
              {
                type: 'message',
                id: `${plan.id}-a-${turn}`,
                timestamp: iso(at + 2_500),
                message: {
                  role: 'assistant',
                  content: [
                    { type: 'thinking', thinking: `Parity step ${turn}: plan the change.` },
                    { type: 'text', text: `Finished parity step ${turn}.` },
                  ],
                },
              },
            ]
          }).flat(),
        ]),
      },
    ],
  }
}

// --- claude-code projection-gap shape: system text in history, no system bucket, partial turns ---
const CLAUDE_PARITY_MODEL = 'claude-sonnet-4-5'

/**
 * Builds a claude-code session matching the `canon.db` projection gap: the system text rides in
 * `conversation_history` with a `system` role rather than a `system_prompt` bucket, tool inputs
 * travel without text, and the tool-result turns carry content the projection only partly places.
 */
export function buildClaudeParityFixture(): FileHarnessFixture {
  const plan = parityPlan('claude-code')
  return {
    contract: {
      ...BASE_CONTRACT,
      harness: 'claude-code',
      sessionId: plan.id,
      turns: plan.turns,
      expectedToolCalls: toolCallCount(plan.turns),
      tokensMeasurable: true,
      cacheCreationMeasurable: true,
      contextMeasurable: false,
      schemaMeasurable: false,
      skillsMeasurable: false,
      perTurnTokensMeasurable: true,
      expectedSkills: [],
      expectedCostStatus: 'published_priced',
      expectedModels: [CLAUDE_PARITY_MODEL],
    },
    files: (baseTimeMs) => {
      const lines: unknown[] = []
      for (let turn = 1; turn <= plan.turns; turn += 1) {
        const at = baseTimeMs + turn * TURN_SPACING_MS
        lines.push({
          type: 'user',
          sessionId: plan.id,
          timestamp: iso(at),
          ...(turn === 1 ? { customTitle: `Claude parity gap ${plan.id}` } : {}),
          message: { role: 'system', content: [{ type: 'text', text: 'System guidance rides in history.' }] },
        })
        lines.push({
          type: 'user',
          sessionId: plan.id,
          timestamp: iso(at + 100),
          message: { role: 'user', content: [{ type: 'text', text: `Parity step ${turn}: continue the work` }] },
        })
        const content: unknown[] = [{ type: 'text', text: `Finished parity step ${turn}.` }]
        if (hasTool(turn)) {
          content.push({
            type: 'tool_use',
            id: `${plan.id}-tc-${turn}`,
            name: 'read_file',
            input: { path: `src/parity-${turn}.ts` },
          })
        }
        lines.push({
          type: 'assistant',
          sessionId: plan.id,
          timestamp: iso(at + 1_000),
          message: {
            id: `${plan.id}-msg-${turn}`,
            role: 'assistant',
            model: CLAUDE_PARITY_MODEL,
            content,
            usage: {
              input_tokens: 100 + turn,
              output_tokens: 40 + turn,
              cache_read_input_tokens: 500,
              cache_creation_input_tokens: 40,
            },
          },
        })
        if (hasTool(turn)) {
          lines.push({
            type: 'user',
            sessionId: plan.id,
            timestamp: iso(at + 2_000),
            message: {
              role: 'user',
              content: [{ type: 'tool_result', tool_use_id: `${plan.id}-tc-${turn}`, content: `contents ${turn}` }],
            },
          })
        }
      }
      return [
        { relativePath: `.claude/projects/parity-project/${plan.id}.jsonl`, content: jsonl(lines) },
      ]
    },
  }
}
