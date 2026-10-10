// P2.5 — Pi collector OTLP content keys (@dpalfery/pi-statusline; G1-Q1 = (a)).
// A collector-shaped `pi.llm.request` maps `pi.tools.selected` / `pi.tools.snippets`
// to `tool_definitions` and `pi.skills` / `pi.context_files` to
// `instruction_context`. `pi.llm.request.payload` stays on raw only.

import { describe, expect, it } from 'vitest'

import collectorAttributes from '../fixtures/pi-collector/collector-request.json' with { type: 'json' }
import { piAdapter } from './pi.js'
import { rawSpan } from './testing.js'

const COLLECTOR_SPAN_NAME = 'pi.llm.request'

describe('pi collector OTLP mapping (P2.5)', () => {
  it('maps pi.tools.selected and pi.tools.snippets into tool_definitions', () => {
    const record = piAdapter.normalize(
      rawSpan({
        spanId: 'pi-collector-req',
        name: COLLECTOR_SPAN_NAME,
        attributes: collectorAttributes as Record<string, unknown>,
      }),
    )

    expect(record.content.tool_definitions).toEqual(
      expect.stringContaining('read_file'),
    )
    expect(record.content.tool_definitions).toEqual(
      expect.stringContaining('grep'),
    )
  })

  it('maps pi.skills and pi.context_files into instruction_context', () => {
    const record = piAdapter.normalize(
      rawSpan({
        spanId: 'pi-collector-req-ctx',
        name: COLLECTOR_SPAN_NAME,
        attributes: collectorAttributes as Record<string, unknown>,
      }),
    )

    expect(record.content.instruction_context).toEqual(
      expect.stringContaining('kyber-weave-docs'),
    )
    expect(record.content.instruction_context).toEqual(
      expect.stringContaining('AGENTS.md'),
    )
  })

  it('leaves pi.llm.request.payload on raw without promoting it to a content bucket', () => {
    const attributes = collectorAttributes as Record<string, unknown>
    const record = piAdapter.normalize(
      rawSpan({
        spanId: 'pi-collector-payload',
        name: COLLECTOR_SPAN_NAME,
        attributes,
      }),
    )

    expect(record.raw).toMatchObject({
      'pi.llm.request.payload': attributes['pi.llm.request.payload'],
    })
    expect(record.content).not.toHaveProperty('pi.llm.request.payload')
  })
})
