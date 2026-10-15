import { describe, expect, it } from 'vitest'

import { formatSessionTitle, isMachineLabel } from './naming.js'

describe('isMachineLabel', () => {
  it('identifies exact claude_code.llm_request as a machine label', () => {
    expect(isMachineLabel('claude_code.llm_request')).toBe(true)
  })

  it('identifies chat <model> variants as machine labels', () => {
    expect(isMachineLabel('chat gpt-4o')).toBe(true)
    expect(isMachineLabel('chat claude-3-5-sonnet')).toBe(true)
    expect(isMachineLabel('chat claude-3-haiku-20240307')).toBe(true)
    expect(isMachineLabel('chat gemini-1.5-pro')).toBe(true)
    expect(isMachineLabel('chat o1-preview')).toBe(true)
  })

  it('identifies operation names ending in .llm_request, .llm_invoke, .generate, or .completion as machine labels', () => {
    expect(isMachineLabel('copilot.llm_request')).toBe(true)
    expect(isMachineLabel('provider.azure.llm_invoke')).toBe(true)
    expect(isMachineLabel('vertex.ai.generate')).toBe(true)
    expect(isMachineLabel('openai.completion')).toBe(true)
    expect(isMachineLabel('bedrock.runtime.llm_invoke')).toBe(true)
  })

  it('identifies raw UUIDs as machine labels', () => {
    expect(isMachineLabel('123e4567-e89b-12d3-a456-426614174000')).toBe(true)
    expect(isMachineLabel('9B1DEB4D-3B7D-4BAD-9BDD-2B0D7B3DCB6D')).toBe(true)
    expect(isMachineLabel('00000000-0000-0000-0000-000000000000')).toBe(true)
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

  it('does not classify human task descriptions, titles, or branch names as machine labels', () => {
    expect(isMachineLabel('Implement subagent hierarchy')).toBe(false)
    expect(isMachineLabel('Fix migration v20 column order')).toBe(false)
    expect(isMachineLabel('Architect')).toBe(false)
    expect(isMachineLabel('feat/sessions-subagents')).toBe(false)
    expect(isMachineLabel('chat')).toBe(false)
    expect(isMachineLabel('generate-report')).toBe(false)
  })

  it('handles null, undefined, and empty string without throwing', () => {
    expect(isMachineLabel(null)).toBe(false)
    expect(isMachineLabel(undefined)).toBe(false)
    expect(isMachineLabel('')).toBe(false)
  })
})

describe('formatSessionTitle', () => {
  it('formats "<agent name> · <task summary>" when both agent name and task summary are present', () => {
    const title = formatSessionTitle({
      agentName: 'Architect',
      taskSummary: 'Implement subagent hierarchy',
      harness: 'claude-code',
      sessionId: 'sess-123',
    })
    expect(title).toBe('Architect · Implement subagent hierarchy')
  })

  it('formats "<agent name> · <task summary>" even when rawLabel contains a machine label', () => {
    const title = formatSessionTitle({
      agentName: 'test-dev',
      taskSummary: 'Author contract tests',
      rawLabel: 'claude_code.llm_request',
      harness: 'claude-code',
    })
    expect(title).toBe('test-dev · Author contract tests')
  })

  it('formats "<agent name> · session" when only agent name is present and task summary is absent', () => {
    const title = formatSessionTitle({
      agentName: 'Architect',
      harness: 'claude-code',
    })
    expect(title).toBe('Architect · session')
  })

  it('formats "<agent name> · session" when task summary is absent and rawLabel is a machine operation', () => {
    const title = formatSessionTitle({
      agentName: 'csharp-dev',
      rawLabel: 'copilot.llm_request',
      harness: 'copilot',
    })
    expect(title).toBe('csharp-dev · session')
  })

  it('formats "<agent name> · <rawLabel>" when task summary is absent but rawLabel is a human label', () => {
    const title = formatSessionTitle({
      agentName: 'Architect',
      rawLabel: 'Review schema design',
    })
    expect(title).toBe('Architect · Review schema design')
  })

  it('formats "<task summary>" when only task summary is present and agent name is absent', () => {
    const title = formatSessionTitle({
      taskSummary: 'Implement subagent hierarchy',
      harness: 'claude-code',
    })
    expect(title).toBe('Implement subagent hierarchy')
  })

  it('suppresses machine operation labels when agent name and task summary are absent', () => {
    const machineLabels = [
      'claude_code.llm_request',
      'chat gpt-4o',
      'copilot.llm_request',
      'bedrock.llm_invoke',
      'vertex.generate',
      'openai.completion',
      '123e4567-e89b-12d3-a456-426614174000',
    ]

    for (const rawLabel of machineLabels) {
      const title = formatSessionTitle({
        rawLabel,
        harness: 'claude-code',
      })
      // The machine label must not leak into the title
      expect(title).not.toContain(rawLabel)
      // Must not fabricate fake agent names
      expect(title).not.toMatch(/^agent\b/i)
      expect(title).not.toContain('Unknown Agent')
    }
  })

  it('uses clean human rawLabel as title when task summary and agent name are absent', () => {
    const title = formatSessionTitle({
      rawLabel: 'Investigate flaky test in migration suite',
      harness: 'claude-code',
    })
    expect(title).toBe('Investigate flaky test in migration suite')
  })

  it('never fabricates fake agent names or synthetic prompt summaries when telemetry is absent (honest unobservability)', () => {
    const emptyTitle = formatSessionTitle({})
    expect(emptyTitle).not.toMatch(/^agent\b/i)
    expect(emptyTitle).not.toContain('Unknown Agent')
    expect(emptyTitle).not.toContain('Synthetic')
    expect(emptyTitle).not.toContain('Prompt')

    const harnessOnlyTitle = formatSessionTitle({ harness: 'claude-code' })
    expect(harnessOnlyTitle).not.toContain('Agent')
    expect(harnessOnlyTitle).not.toContain('Prompt')
  })
})
