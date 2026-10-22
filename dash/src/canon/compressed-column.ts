// One compressed column's two stored forms.
//
// WHY this module exists: the compressed-column helpers used to live in
// `canon/store.ts`, and the split-read contract needs the read half of them —
// a turn row holds the deflated turn, and the reader that turns a row into a
// turn inflates it first. Importing the store from there would close an
// import cycle, because the store imports the split-read contract itself, and
// the initialization order of two modules that depend on each other is not
// something to leave to whichever of them was loaded first.
//
// So the codec lives here, in a leaf both sides import, and the store
// re-exports it so an importer that still resolves it from there keeps
// resolving it.
//
// It lives in `canon/` because that is where the store's own columns live:
// the records table's `parts_json` and the session table's `turn_json` are the
// same kind of column, and they are read by the same rule.

import { deflateSync, inflateSync } from 'node:zlib'

/**
 * One compressed column's stored form: the deflated bytes, or the plain JSON
 * text when deflating the value would not pay.
 */
export type CompressedColumn = Uint8Array | string

/**
 * The stored form of one compressed column's value.
 *
 * <remarks>
 * The deflated bytes when deflating the JSON pays, and the plain JSON text
 * when it does not. A short value deflates to more bytes than it holds — an
 * empty parts array is 2 bytes of JSON and 10 bytes deflated — and the reader
 * would pay an inflate to get back what the column already has. So the plain
 * form is not a fallback for a broken value; it is what a small value earns.
 *
 * The two forms are told apart by the storage class SQLite gives the value: a
 * BLOB is the deflated form, TEXT is the plain one. A column therefore needs
 * no marker of its own, and a column that carries rows of both kinds reads
 * both.
 *
 * The level is zlib's default, the level the records table's own compression
 * (`compressRaw` in `canon/store.ts`) uses: the same function, so the same
 * bytes for the same value, and the measured cost of the records table's own
 * compression (+330 ms per full refresh) is the cost this level is known to
 * carry.
 */
export function compressColumn(value: unknown): CompressedColumn {
  const json = JSON.stringify(value)
  const deflated = deflateSync(Buffer.from(json, 'utf8'))
  return deflated.length < Buffer.byteLength(json, 'utf8') ? deflated : json
}

/**
 * One compressed column's value, read back from either stored form.
 *
 * <remarks>
 * A BLOB is the deflated form `compressColumn` writes; TEXT is the plain JSON
 * a row written before the compression landed still holds. Both are read, so
 * a store that carries a row of each kind reads both — the migration
 * tolerance, without which a pre-change row loses the content it holds.
 * There is no third form to guess at: a column holds one or the other.
 *
 * `undefined` when the value is neither form, or when the form it is does not
 * parse. A column holding something else is not a value, and a reader that
 * invented one would serve a part or a turn that was never written.
 */
export function decompressColumn(stored: unknown): unknown | undefined {
  if (typeof stored === 'string') {
    try {
      return JSON.parse(stored) as unknown
    } catch {
      return undefined
    }
  }
  if (stored instanceof Uint8Array) {
    try {
      return JSON.parse(inflateSync(Buffer.from(stored)).toString('utf8')) as unknown
    } catch {
      return undefined
    }
  }
  return undefined
}
