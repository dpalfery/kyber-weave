import { describe, expect, it } from 'vitest'

import { parseArgs } from './store-memory.js'

describe('store-memory tool args', () => {
  it('throws when --db has no value instead of measuring the live store', () => {
    expect(() => parseArgs(['--db'])).toThrow(/--db/)
  })
})
