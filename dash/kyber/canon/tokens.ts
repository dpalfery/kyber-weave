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
 * The character-count approximation: one token per four characters. Kept as
 * the fallback for environments where the real encoder cannot load, and as
 * the reference the real tokenizer is measured against. It is not accurate —
 * over a run of repeated characters it reports 250 where `o200k_base` counts
 * 125 — which is exactly why a count carries the name of what produced it.
 */
export function approximateO200kBase(text: string): number {
  return Math.ceil(text.length / 4)
}

/** Name reported for counts produced by the character approximation. */
export const APPROXIMATE_TOKENIZER = 'approximate/chars-per-4'

/** Name reported for counts produced by the real `o200k_base` encoder. */
export const O200K_TOKENIZER = 'js-tiktoken/o200k_base'

/**
 * The loaded encoder, or `null` once loading has been tried and failed.
 * `js-tiktoken/lite` plus the single `o200k_base` rank table is 2.3 MB; the
 * package's default entry point pulls every encoding it ships, which is not
 * what this needs.
 */
let encoder: { encode(text: string): unknown[] } | null | undefined

/**
 * Load the real encoder once. A failure is remembered rather than retried per
 * call: if the rank table is unavailable in this environment it will be
 * unavailable for the next hundred thousand strings too, and the caller has
 * a documented fallback.
 */
async function loadEncoder(): Promise<{ encode(text: string): unknown[] } | null> {
  if (encoder !== undefined) return encoder
  try {
    const [{ Tiktoken }, ranks] = await Promise.all([
      import('js-tiktoken/lite'),
      import('js-tiktoken/ranks/o200k_base'),
    ])
    encoder = new Tiktoken((ranks as { default: ConstructorParameters<typeof Tiktoken>[0] }).default)
  } catch {
    encoder = null
  }
  return encoder
}

/**
 * Load the encoder and return a SYNCHRONOUS counter over it.
 *
 * `analyzeContext` takes a sync `countTokens` on purpose — it walks thousands
 * of parts and cannot await each one — while loading the rank table is
 * necessarily async. This bridges the two: await once, then count freely.
 * Falls back to the character approximation if the encoder is unavailable,
 * so a caller always gets a counter and the tokenizer name says which ran.
 */
export async function loadO200kCounter(): Promise<(text: string) => number> {
  const loaded = await loadEncoder()
  if (loaded === null) return approximateO200kBase
  return (text: string) => loaded.encode(text).length
}

/**
 * The effective tokenizer name, without awaiting a load. Reports the
 * configured encoder until a load has been attempted and failed, then reports
 * the fallback. Exposed for metadata surfaces that are synchronous; anything
 * that can await should prefer `activeTokenizer`.
 */
export function tokenizerName(): string {
  return encoder === null ? APPROXIMATE_TOKENIZER : O200K_TOKENIZER
}

/**
 * Which tokenizer the last count came from. Surfaces are required to name
 * the tokenizer behind a derived figure (R4.6), and naming one that did not
 * run is worse than naming none — the dashboard advertised
 * `tiktoken/o200k_base` for counts this module produced with `length / 4`.
 */
export async function activeTokenizer(): Promise<string> {
  return (await loadEncoder()) === null ? APPROXIMATE_TOKENIZER : O200K_TOKENIZER
}

/**
 * The default tokenizer: the real `o200k_base` encoding, falling back to the
 * character approximation when the encoder cannot be loaded. Still a named
 * seam — a caller measuring one model against another passes its own counter.
 */
export async function countO200kBase(text: string): Promise<number> {
  const loaded = await loadEncoder()
  return loaded === null ? approximateO200kBase(text) : loaded.encode(text).length
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
  countTokens: TokenCounter = countO200kBase
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
