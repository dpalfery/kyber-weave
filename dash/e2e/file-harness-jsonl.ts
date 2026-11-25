// Two sessions per JSONL file-import harness, each 10 or 12 turns, in the harness's native
// shape (copied from its mapper tests under dash/server.tests/KyberDash.Data.Tests/Harnesses).
// Timestamps derive from the run clock passed in (D10). A contract states each section as
// measured or as its explicit not-measurable state, from the mapper tests and
// docs/dash/telemetry-inventory.md, never from what the screen shows.
import type { FileHarnessContract, FileHarnessFixture } from './file-harness-contracts.js'
import type { HarnessFile } from './file-harness-world.js'

const TURN_SPACING_MS = 30_000
const TOOL_EVERY = 3

type SessionPlan = { readonly id: string; readonly turns: number }

function plansFor(harness: string): readonly [SessionPlan, SessionPlan] {
  return [
    { id: `file-e2e-${harness}-a-12t`, turns: 12 },
    { id: `file-e2e-${harness}-b-10t`, turns: 10 },
  ]
}

/** Tool calls the builders emit: one on every third turn. */
function toolCallCount(turns: number): number {
  return Math.floor(turns / TOOL_EVERY)
}

function hasTool(turn: number): boolean {
  return turn % TOOL_EVERY === 0
}

function iso(ms: number): string {
  return new Date(ms).toISOString()
}

function jsonl(lines: readonly unknown[]): string {
  return `${lines.map((line) => JSON.stringify(line)).join('\n')}\n`
}

/** Contract fields every JSONL harness shares unless it says otherwise. */
const BASE_CONTRACT = {
  cacheCreationMeasurable: false,
  contextMeasurable: false,
  schemaMeasurable: false,
  skillsMeasurable: false,
  pairedWithSessionFiles: false,
  expectedSkills: [] as readonly string[],
  expectedGapSubstrings: [] as readonly string[],
  sendsLogs: false,
} as const

function fixturesFor(
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
]
