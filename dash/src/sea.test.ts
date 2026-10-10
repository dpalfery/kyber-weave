/**
 * Tests for the `node:sea` seam in `dash/src/sea.ts`.
 *
 * `embeddedTextAsset()` wraps Node's real `getAsset()`, which throws when the
 * requested key is absent. Every other test of this module (and of its callers)
 * mocks `getAsset` to directly *return* `undefined` rather than throw, so the
 * `catch` branch itself was never exercised. This file adds that missing case:
 * if `getAsset` throws, `embeddedTextAsset()` must catch it and return
 * `undefined` rather than letting the exception propagate.
 */

import { afterEach, describe, expect, it, vi } from 'vitest'

const { mockGetAsset, mockIsSea } = vi.hoisted(() => ({
  mockGetAsset: vi.fn((_key: string): string | Buffer | undefined => undefined),
  mockIsSea: vi.fn(() => false),
}))

vi.mock('node:sea', () => ({
  isSea: mockIsSea,
  getAsset: mockGetAsset,
}))

import { embeddedTextAsset } from './sea.js'

describe('embeddedTextAsset()', () => {
  afterEach(() => {
    mockIsSea.mockReset()
    mockGetAsset.mockReset()
  })

  it('returns undefined when getAsset() throws for a missing key', () => {
    mockIsSea.mockReturnValue(true)
    mockGetAsset.mockImplementation(() => {
      throw new Error('some key not found')
    })

    expect(embeddedTextAsset('missing-key.json')).toBeUndefined()
  })
})
