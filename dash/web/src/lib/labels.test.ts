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
