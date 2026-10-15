import { describe, expect, it } from 'vitest'

import {
  barPercent,
  formatDuration,
  formatModelLabel,
  formatPercentRatio,
  formatSessionId,
  formatSessionTitle,
  formatSpanLabel,
  isMachineLabel,
} from './labels.js'

describe('formatSessionId', () => {
  it('does not take a blind 8-character prefix of a harness-shaped id', () => {
    expect(formatSessionId('claude-desktop')).not.toBe('claude-d')
    expect(formatSessionId('claude-desktop')).toBe('claude-desktop')
    expect(formatSessionId('cursor-agent')).not.toBe('cursor-a')
    expect(formatSessionId('sess_sub')).toBe('sess_sub')
  })

  it('strips synth prefixes and keeps uuid ends distinguishable', () => {
    const a = formatSessionId('synth:claude-desktop:d625dd4c-74ef-4cc6-8efe-4828a71a7541:aaaa')
    const b = formatSessionId('synth:claude-desktop:a20aa21b-8290-45f1-9c11-0000e4598e54:bbbb')
    expect(a).not.toContain('synth:')
    expect(a).not.toEqual(b)
    expect(a).toContain('…')
  })
})

describe('formatSessionTitle', () => {
  it('keeps a human title', () => {
    expect(formatSessionTitle({ label: 'Fix the login form', sessionId: 'abc' })).toBe('Fix the login form')
  })

  it('does not use a raw synth id or span op as the title', () => {
    expect(
      formatSessionTitle({
        label: 'synth:claude-desktop:d625dd4c-74ef-4cc6-8efe-4828a71a7541:turn',
        harness: 'claude-desktop',
        sessionId: 'sess_subagent_01',
      }),
    ).toBe('Claude Desktop session')
    expect(
      formatSessionTitle({
        label: 'claude_code.llm_request',
        harness: 'claude-code',
      }),
    ).toBe('Claude Code session')
  })
})

describe('formatSessionTitle, agent and session name', () => {
  it('leads with the agent and trails the session name', () => {
    expect(formatSessionTitle({ label: 'Fix the login form', agentName: 'architect', harness: 'copilot' })).toBe(
      'architect · Fix the login form',
    )
  })

  it('never shows a GenAI span name as the title', () => {
    expect(formatSessionTitle({ label: 'invoke_agent architect', agentName: 'architect', harness: 'copilot' })).toBe(
      'architect',
    )
    expect(formatSessionTitle({ label: 'chat gpt-4o-mini-2024-07-18', sessionId: 'fc98e860', harness: 'copilot' })).toBe(
      'Copilot session',
    )
    expect(formatSessionTitle({ label: 'execute_tool manage_todo_list', harness: 'copilot' })).toBe('Copilot session')
  })

  it('uses a readable slug id as the session name and ignores an agent that is just the harness', () => {
    expect(
      formatSessionTitle({
        label: 'chat Claude Sonnet 5.5 (Medium)',
        sessionId: 'sessions-subagents-and-names',
        agentName: 'antigravity',
        harness: 'antigravity',
      }),
    ).toBe('sessions-subagents-and-names')
  })
})

describe('formatSessionTitle, review edge cases', () => {
  it('keeps a human title that merely starts with chat or embeddings', () => {
    expect(formatSessionTitle({ label: 'Chat about the login form', harness: 'copilot' })).toBe('Chat about the login form')
    expect(formatSessionTitle({ label: 'Embeddings cleanup plan', harness: 'copilot' })).toBe('Embeddings cleanup plan')
  })

  it('keeps human chat titles that carry a count or a hyphenated word', () => {
    expect(formatSessionTitle({ label: 'Chat about 2 new features', harness: 'copilot' })).toBe('Chat about 2 new features')
    expect(formatSessionTitle({ label: 'Chat kick-off meeting', harness: 'copilot' })).toBe('Chat kick-off meeting')
  })

  it('still treats model-shaped chat spans as machine labels', () => {
    expect(isMachineLabel('chat gpt-6-luna')).toBe(true)
    expect(isMachineLabel('chat o1')).toBe(true)

    expect(isMachineLabel('chat gpt-4o-mini-2024-07-18')).toBe(true)
    expect(isMachineLabel('chat Claude Sonnet 5.5 (Medium)')).toBe(true)
    expect(isMachineLabel('chat Gemini 3.8 Flash (High)')).toBe(true)
  })

  it('accepts readable ids with digits or capitals, but not uuids or tool-call ids', () => {
    expect(formatSessionTitle({ sessionId: 'session-2024-10-15', harness: 'antigravity' })).toBe('session-2024-10-15')
    expect(formatSessionTitle({ sessionId: 'Sessions-Subagents', harness: 'antigravity' })).toBe('Sessions-Subagents')
    expect(formatSessionTitle({ sessionId: '728380ff-c64a-4c31-8d9e-bdcf6a5c6ce6', harness: 'copilot' })).toBe('Copilot session')
    expect(formatSessionTitle({ sessionId: 'toolu_01Fx1LciXhDzP3h8JRSudYb3', harness: 'copilot' })).toBe('Copilot session')
  })

  it('does not repeat the agent when the session name differs only by case', () => {
    expect(formatSessionTitle({ label: 'Architect', agentName: 'architect', harness: 'copilot' })).toBe('architect')
  })
})

describe('formatSessionTitle, labels the server already titled', () => {
  it('does not prefix the agent a second time', () => {
    expect(
      formatSessionTitle({ label: 'explorer · explore workspace architecture', agentName: 'explorer', harness: 'claude-code' }),
    ).toBe('explorer · explore workspace architecture')
  })

  it('still prefixes a plain task label with the agent', () => {
    expect(
      formatSessionTitle({ label: 'explore workspace architecture', agentName: 'explorer', harness: 'claude-code' }),
    ).toBe('explorer · explore workspace architecture')
  })
})

describe('formatModelLabel / formatSpanLabel', () => {
  it('replaces synthetic and unknown placeholders', () => {
    expect(formatModelLabel('claude:<synthetic>')).toBe('Claude (unspecified)')
    expect(formatModelLabel('antigravity:unknown')).toBe('Antigravity (unspecified)')
    expect(formatModelLabel('claude_code.llm_request')).toBe('LLM request')
  })

  it('prettifies request labels that are raw synth ids', () => {
    expect(formatSpanLabel('synth:claude-desktop:d625dd4c-74ef-4cc6-8efe-4828a71a7541:x')).not.toContain(
      'synth:',
    )
    expect(formatSpanLabel('claude_code.llm_request')).toBe('LLM request')
  })

  it('keeps human labels that start with chat, and still filters model-shaped chat spans', () => {
    expect(isMachineLabel('chat with team about launch')).toBe(false)
    expect(isMachineLabel('Chat about 2 new features')).toBe(false)
    expect(isMachineLabel('Chat kick-off meeting')).toBe(false)
    expect(isMachineLabel('chat gpt-4o-mini-2024-07-18')).toBe(true)
    expect(isMachineLabel('chat gpt-6-luna')).toBe(true)
    expect(isMachineLabel('chat o1')).toBe(true)
    expect(isMachineLabel('chat Claude Sonnet 5.5 (Medium)')).toBe(true)
  })

  it('treats those placeholders as machine labels', () => {
    expect(isMachineLabel('claude:<synthetic>')).toBe(true)
    expect(isMachineLabel('Fix login')).toBe(false)
  })
})

describe('percent and duration presentation', () => {
  it('shows pressure overflow instead of clipping the number', () => {
    expect(formatPercentRatio(1.08)).toBe('108%')
    expect(formatPercentRatio(1.09)).toBe('109%')
    expect(barPercent(1.08)).toBe(100)
  })

  it('renders unmeasured duration as an em dash, including stored zero', () => {
    expect(formatDuration(null)).toBe('—')
    expect(formatDuration(undefined)).toBe('—')
    expect(formatDuration(0)).toBe('—')
    expect(formatDuration(450)).toBe('450ms')
    expect(formatDuration(2500)).toBe('2.5s')
    expect(formatDuration(65000)).toBe('1m 5s')
  })
})

describe('formatSessionId, scoped subagent ids', () => {
  it('keeps a scoped subagent id distinguishable from its parent', () => {
    const parent = '728380ff-c64a-4c31-8d9e-bdcf6a5c6ce6'
    const claude = formatSessionId(`${parent}#agent-a4b10a0830747b668`)
    const copilot = formatSessionId(`${parent}#invoke-agent-span-1`)
    expect(claude).not.toBe(formatSessionId(parent))
    expect(copilot).not.toBe(formatSessionId(parent))
    expect(claude).toContain('#agent-a4b10a08')
    expect(claude).toContain(formatSessionId(parent))
    expect(claude).not.toBe(formatSessionId(`${parent}#agent-b9c20b1941858c779`))
  })
})
