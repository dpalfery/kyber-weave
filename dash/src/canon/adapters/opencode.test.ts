// T4 RED: the OpenCode adapter slot sits in ADAPTERS as a placeholder that
// detects nothing and claims no span, leaving existing votes unchanged.

import { describe, expect, it } from 'vitest'

import { ADAPTERS } from '../ingest.js'
import { opencodeAdapter } from './opencode.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'

describe('opencode adapter placeholder (T4)', () => {
  it('registers an opencode placeholder adapter', () => {
    const adapter = ADAPTERS.find((entry) => entry.name === 'opencode')
    expect(adapter).toBeDefined()
  })

  it('claims no span, not even an opencode-namespaced or usage-carrying one', () => {
    const adapter = ADAPTERS.find((entry) => entry.name === 'opencode')
    expect(adapter).toBeDefined()
    if (adapter === undefined) return
    const candidates = [
      rawSpan({ spanId: 'empty', attributes: {} }),
      rawSpan({ spanId: 'usage', attributes: { 'gen_ai.usage.input_tokens': 512 } }),
      rawSpan({
        spanId: 'vendor',
        attributes: { 'opencode.session.id': 'synth-session', 'gen_ai.usage.input_tokens': 512 },
      }),
    ]
    for (const span of candidates) {
      expect(adapter.detect(span), span.spanId).toBe(0)
    }
  })

  it('leaves the existing ingest votes unchanged', () => {
    const registry = new AdapterRegistry([...ADAPTERS])
    const attributed = registry.attribute([
      rawSpan({
        spanId: 'pi-span',
        source: 'pi-synth',
        traceId: 'trace-pi',
        attributes: { 'pi.session.id': 'synth-session', 'gen_ai.usage.input_tokens': 4821 },
      }),
      rawSpan({
        spanId: 'codex-span',
        source: 'codex-synth',
        traceId: 'trace-codex',
        attributes: { 'codex.tool.name': 'synth-tool', 'gen_ai.usage.input_tokens': 100 },
      }),
    ])
    expect(attributed.get('pi-span')).toBe('pi')
    expect(attributed.get('codex-span')).toBe('codex')
  })

  it('does not file a message envelope as conversation when content is missing', () => {
    const record = opencodeAdapter.normalize(
      rawSpan({
        spanId: 'legacy-missing-content',
        attributes: {
          'ai.prompt.messages': [
            { role: 'user', content: 'keep this sentence' },
            { role: 'user', content: null, id: 'synthetic-id' },
          ],
        },
      }),
    )
    const text = (record.parts ?? []).map((part) => part.text).join('\n')
    expect(text).toContain('keep this sentence')
    expect(text).not.toContain('synthetic-id')
  })
})
