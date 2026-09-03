import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { describe, expect, it, vi } from 'vitest'

import { CanonStore } from './store.js'
import type { Measurability } from './types.js'
import {
  DERIVED,
  O200K_TOKENIZER,
  activeTokenizer,
  approximateO200kBase,
  cacheKey,
  countO200kBase,
  tokenize,
  type TokenCount,
} from './tokens.js'

function memoryStore(): InstanceType<typeof DatabaseSync> {
  return new DatabaseSync(':memory:')
}

function cachedRow(store: InstanceType<typeof DatabaseSync>, text: string, model: string) {
  return store
    .prepare('SELECT count, model FROM token_cache WHERE hash = ?')
    .get(cacheKey(text, model)) as { count: number; model: string } | undefined
}

describe('tokenize', () => {
  describe('cache', () => {
    it('misses on first call: computes once and stores the row', async () => {
      const store = memoryStore()
      const countTokens = vi.fn(() => 42)

      const result = await tokenize('hello world', 'gpt-4o', store, countTokens)

      expect(result).toEqual({ count: 42, model: 'gpt-4o', derived: true })
      expect(countTokens).toHaveBeenCalledTimes(1)

      // Store-backed, not process-local: the row is in token_cache under the
      // model-scoped key.
      expect(cachedRow(store, 'hello world', 'gpt-4o')).toEqual({ count: 42, model: 'gpt-4o' })
    })

    it('hits on the second call: same count, no recomputation', async () => {
      const store = memoryStore()
      const countTokens = vi.fn((text: string) => approximateO200kBase(text))

      const first = await tokenize('repeat me', 'claude-sonnet-4', store, countTokens)
      const second = await tokenize('repeat me', 'claude-sonnet-4', store, countTokens)

      expect(second.count).toBe(first.count)
      expect(second.model).toBe('claude-sonnet-4')
      expect(countTokens).toHaveBeenCalledTimes(1)
    })

    it('recomputes for unseen text, then serves that from the cache too', async () => {
      const store = memoryStore()
      const countTokens = vi.fn(() => 7)

      await tokenize('first text', 'gpt-4o', store, countTokens)
      await tokenize('second text', 'gpt-4o', store, countTokens)
      expect(countTokens).toHaveBeenCalledTimes(2)

      await tokenize('second text', 'gpt-4o', store, countTokens)
      expect(countTokens).toHaveBeenCalledTimes(2)
    })

    it('keys the cache per model — the same text tokenizes per tokenizer', async () => {
      // R4.6's measured difference between tokenizers only stays observable if
      // the model is part of the cache identity; a shared key would serve one
      // tokenizer's count under another's name.
      const store = memoryStore()
      const countTokens = vi.fn(() => 100)

      const forGpt = await tokenize('shared text', 'gpt-4o', store, countTokens)
      const forClaude = await tokenize('shared text', 'claude-sonnet-4', store, countTokens)

      expect(countTokens).toHaveBeenCalledTimes(2)
      expect(forGpt.model).toBe('gpt-4o')
      expect(forClaude.model).toBe('claude-sonnet-4')
      expect(cacheKey('shared text', 'gpt-4o')).not.toBe(cacheKey('shared text', 'claude-sonnet-4'))
      expect(cachedRow(store, 'shared text', 'gpt-4o')).toEqual({ count: 100, model: 'gpt-4o' })
      expect(cachedRow(store, 'shared text', 'claude-sonnet-4')).toEqual({ count: 100, model: 'claude-sonnet-4' })
    })

    it('serves a hit across store instances backed by the same database file', async () => {
      const dir = mkdtempSync(join(tmpdir(), 'kyber-tokens-'))
      try {
        const countTokens = vi.fn(() => 7)
        await tokenize('persisted text', 'o200k_base', new DatabaseSync(join(dir, 'cache.db')), countTokens)

        const reopened = await tokenize('persisted text', 'o200k_base', new DatabaseSync(join(dir, 'cache.db')), countTokens)

        // Memoized by the store, not the process: a fresh connection hits.
        expect(countTokens).toHaveBeenCalledTimes(1)
        expect(reopened).toEqual({ count: 7, model: 'o200k_base', derived: true })
      } finally {
        rmSync(dir, { recursive: true, force: true })
      }
    })

    it('creates the table idempotently: repeated tokenize calls on one store', async () => {
      const store = memoryStore()
      await tokenize('a', 'gpt-4o', store)
      await expect(tokenize('a', 'gpt-4o', store)).resolves.toEqual({
        count: approximateO200kBase('a'),
        model: 'gpt-4o',
        derived: true,
      })
    })

    it('refuses an unusable count before it can poison the cache', async () => {
      const store = memoryStore()
      await expect(tokenize('x', 'gpt-4o', store, () => -1)).rejects.toThrow(TypeError)
      await expect(tokenize('x', 'gpt-4o', store, () => Number.NaN)).rejects.toThrow(TypeError)

      // Nothing was written, so the key stays a miss rather than a bad hit.
      expect(cachedRow(store, 'x', 'gpt-4o')).toBeUndefined()
    })

    it('reads and writes the token_cache table the CanonStore schema builds', async () => {
      // Task 3.2 versions token_cache in its own SCHEMA_SQL; tokenize()
      // reaches it through the database file itself. Whichever CREATE TABLE
      // IF NOT EXISTS ran, the table is the same one — a miss inserts against
      // the store-built schema, and a fresh connection hits without recompute.
      const dir = mkdtempSync(join(tmpdir(), 'kyber-tokens-'))
      try {
        const path = join(dir, 'canon.db')
        const canon = new CanonStore(path)
        try {
          const countTokens = vi.fn(() => 19)

          const first = await tokenize('shared row', 'gpt-4o', new DatabaseSync(path), countTokens)
          expect(first).toEqual({ count: 19, model: 'gpt-4o', derived: true })

          const hit = await tokenize('shared row', 'gpt-4o', new DatabaseSync(path), countTokens)
          expect(hit).toEqual({ count: 19, model: 'gpt-4o', derived: true })
          expect(countTokens).toHaveBeenCalledTimes(1)
        } finally {
          canon.close()
        }
      } finally {
        rmSync(dir, { recursive: true, force: true })
      }
    })
  })

  describe('derived tagging (R4.6)', () => {
    it('tags every result derived: true and carries the input model', async () => {
      const store = memoryStore()
      const fromMiss = await tokenize('some content', 'claude-sonnet-4', store)
      const fromHit = await tokenize('some content', 'claude-sonnet-4', store)

      for (const result of [fromMiss, fromHit]) {
        expect(result.derived).toBe(true)
        expect(result.model).toBe('claude-sonnet-4')
      }
    })

    it('never returns a bare number: the shape is { count, model, derived }', async () => {
      const store = memoryStore()
      const result = await tokenize('some content', 'gpt-4o', store)

      expect(Object.keys(result).sort()).toEqual(['count', 'derived', 'model'])
      expect(typeof result.count).toBe('number')
      // A derived count cannot be relabeled as measured at the type level;
      // `derived` is the literal `true`, not a boolean.
      // @ts-expect-error - derived is the literal true, not a boolean
      const mislabeled: TokenCount = { count: 1, model: 'gpt-4o', derived: false }
      expect(mislabeled).toBeTruthy()
    })

    it('DERIVED is the "derived" arm of MetricAvailability and propagates into measurability', async () => {
      const store = memoryStore()
      const result = await tokenize('some content', 'gpt-4o', store)

      expect(DERIVED).toBe('derived')
      const measurability: Measurability = { input_tokens: DERIVED }
      expect(measurability.input_tokens).toBe('derived')
      // The tag on the result and the tag in the vocabulary agree, so a
      // consumer labeling a derived count cannot invent a third state.
      expect(result.derived === true && DERIVED === 'derived').toBe(true)
    })
  })

  describe('lower bound', () => {
    it('yields finite, non-negative counts for empty and non-empty text', async () => {
      const store = memoryStore()
      const empty = await tokenize('', 'gpt-4o', store)
      const filled = await tokenize('four five six seven', 'gpt-4o', store)

      expect(empty.count).toBeGreaterThanOrEqual(0)
      expect(Number.isFinite(empty.count)).toBe(true)
      expect(filled.count).toBeGreaterThan(0)
      expect(Number.isFinite(filled.count)).toBe(true)
    })

    it('approximates o200k_base as ceil(chars / 4) at the boundary', () => {
      expect(approximateO200kBase('')).toBe(0)
      expect(approximateO200kBase('abcd')).toBe(1)
      expect(approximateO200kBase('abcde')).toBe(2)
    })
  })
})

describe('the real o200k_base encoder (R4.6)', () => {
  it('counts what the published tokenization counts, not what chars/4 guesses', async () => {
    // Published o200k_base tokenizations. The approximation is not close on
    // any of them, which is the point: a bucket chart built on chars/4 needs
    // a "treat this as a lower bound" caveat that a real count does not.
    expect(await countO200kBase('hello world')).toBe(2)
    expect(await countO200kBase('The quick brown fox jumps over the lazy dog.')).toBe(10)
    expect(await countO200kBase('a'.repeat(1000))).toBe(125)
  })

  it('is materially different from the approximation it replaces', async () => {
    const text = 'a'.repeat(1000)

    expect(approximateO200kBase(text)).toBe(250)
    expect(await countO200kBase(text)).toBe(125)
  })

  it('names the tokenizer that actually ran', async () => {
    expect(await activeTokenizer()).toBe(O200K_TOKENIZER)
  })

  it('is the default counter tokenize() uses', async () => {
    const db = new DatabaseSync(':memory:')
    const result = await tokenize('hello world', 'gpt-4o', db)

    expect(result.count).toBe(2)
    expect(result.derived).toBe(true)
    db.close()
  })
})
