// Tokenization with its store-backed memo cache, ported from the Python
// pipeline's `tokens.py` (spec: docs/specs/kyberdash, design.md "Analysis
// layer", R4.6).
//
// A count that no counter supplied is derived by tokenizing content, and a
// derived count is never interchangeable with a measured one: it is a lower
// bound. The `o200k_base` proxy leaves a 2.8–4.4% unattributed residual
// against one measured model and 35–41% against another — the same text
// tokenizes differently per model, and the difference is the tokenizer, not
// missing content. Two guarantees keep that legible downstream: every result
// carries `derived: true`, and every result names the model it was tokenized
// against. A bare number can say neither.

import { createHash } from 'node:crypto'

import type { MetricAvailability } from './types.js'

/**
 * A token count together with the facts that make it interpretable (R4.6):
 * it was derived by tokenizing rather than read from a counter, and it is
 * attributed to a named model. Consumers must present `count` as a lower
 * bound; `derived: true` is the literal tag that makes mislabeling a
 * type-level error rather than a rendering choice.
 */
export type TokenCount = {
  /** The count; a lower bound, not a harness counter (R4.6). */
  count: number
  /** The model the text was tokenized against. */
  model: string
  /** Always `true` — the count was derived, never measured. */
  derived: true
}

/**
 * The availability a derived count carries. Consumers writing a
 * `Measurability` entry for a metric they derived by tokenization should use
 * this constant rather than re-typing the string, so the tag cannot drift
 * from the vocabulary in types.ts.
 */
export const DERIVED: MetricAvailability = 'derived'

/**
 * What tokenization needs from the store: the `exec`/`prepare` surface of
 * the runtime's built-in SQLite database (`node:sqlite` `DatabaseSync`).
 * Structural on purpose — the `CanonStore` of task 3.2 versions the
 * `token_cache` table in its own schema but keeps its connection private, so
 * `tokenize` takes the database itself: the store's connection, a fresh one
 * on the same file, or a bare `DatabaseSync` (as the tests use) all read and
 * write the same table.
 */
export type TokenCacheStore = {
  exec(sql: string): unknown
  prepare(sql: string): {
    get(...params: unknown[]): unknown
    run(...params: unknown[]): unknown
  }
}

/** Turns text into a token count; may load an encoder, hence the promise. */
export type TokenCounter = (text: string) => number | Promise<number>

/**
 * Memo-cache DDL, textually identical to the `token_cache` table in the
 * store's versioned schema, so whichever `CREATE TABLE IF NOT EXISTS` runs
 * first — task 3.2's `SCHEMA_SQL` on construction or this one on first use —
 * the table is the same one (its accessors are here; its home is the store).
 */
const TOKEN_CACHE_SCHEMA = `
CREATE TABLE IF NOT EXISTS token_cache (
  hash TEXT PRIMARY KEY,
  count INTEGER NOT NULL,
  model TEXT
);
`

const ensuredStores = new WeakSet<TokenCacheStore>()

/**
 * Cache key: the model and the SHA-256 of the text. The digest is always 64
 * hex characters, so the `model:` prefix cannot be misread at a boundary.
 * Keying on the model is the point, not a detail: different tokenizers over
 * the same text land on different keys and never cross-contaminate, because
 * the residual between tokenizers is the measured difference (R4.6).
 */
export function cacheKey(text: string, model: string): string {
  return `${model}:${createHash('sha256').update(text, 'utf8').digest('hex')}`
}

/**
 * The default tokenizer: an `o200k_base` approximation of one token per four
 * characters. Deliberately a named seam rather than an inline expression —
 * when `js-tiktoken` lands it replaces this default without touching any
 * caller, and until then the approximation's name travels with the model the
 * way R4.6 requires.
 */
export function approximateO200kBase(text: string): number {
  return Math.ceil(text.length / 4)
}

/**
 * Tokenize `text` against `model`, memoized in the store's `token_cache`
 * table (R4.6). A miss computes the count and inserts; a hit returns the
 * cached row without recomputation. Both paths return a `TokenCount` tagged
 * `derived` and carrying the model name, so a consumer can label the figure a
 * lower bound and name the tokenizer behind it.
 */
export async function tokenize(
  text: string,
  model: string,
  store: TokenCacheStore,
  countTokens: TokenCounter = approximateO200kBase
): Promise<TokenCount> {
  if (!ensuredStores.has(store)) {
    store.exec(TOKEN_CACHE_SCHEMA)
    ensuredStores.add(store)
  }

  const key = cacheKey(text, model)
  const cached = store
    .prepare('SELECT count, model FROM token_cache WHERE hash = ?')
    .get(key) as { count: number; model: string } | undefined
  if (cached !== undefined) {
    return { count: cached.count, model: cached.model, derived: true }
  }

  const count = await countTokens(text)
  if (!Number.isFinite(count) || count < 0) {
    // A non-finite or negative count would poison every future hit on this
    // key; refuse it before it reaches the table.
    throw new TypeError(`tokenizer returned an unusable count (${count}) for model ${model}`)
  }

  // IGNORE, not REPLACE: a concurrent writer on the same key ran the same
  // deterministic counter over the same text, so either row is correct.
  store
    .prepare('INSERT OR IGNORE INTO token_cache (hash, count, model) VALUES (?, ?, ?)')
    .run(key, count, model)

  return { count, model, derived: true }
}
