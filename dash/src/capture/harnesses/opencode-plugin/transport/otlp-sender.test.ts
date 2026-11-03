// The OTLP sender exports to the loopback receiver only (Q2): the V3 traces path on 127.0.0.1.

import { describe, expect, it } from 'vitest'

import { DEFAULT_OTLP_ENDPOINT, OTLP_TRACES_PATH } from './otlp-sender.js'
import { LOOPBACK_TRACES_URL } from '../testing.js'

describe('OTLP sender endpoint (Q2: loopback only)', () => {
  it('exports to the loopback V3 traces path and nowhere else', () => {
    expect(OTLP_TRACES_PATH).toBe('/v1/traces')
    expect(DEFAULT_OTLP_ENDPOINT).toBe(LOOPBACK_TRACES_URL)
  })
})
