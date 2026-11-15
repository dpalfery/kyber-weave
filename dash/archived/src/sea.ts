/**
 * Single point of contact with Node's `node:sea` builtin.
 *
 * This module isolates the static ESM import so that tests can mock the
 * builtin with `vi.mock('node:sea', …)` (if Vitest supports that), or
 * alternatively mock this seam instead.
 *
 * The module is used by:
 * - `dash/src/install/node-deps.ts` (T2): to detect SEA and resolve the CLI path.
 * - `dash/src/cli/web.ts` (T6): to embed and serve the SPA.
 */

import { isSea as seaIsSea, getAsset } from 'node:sea'

/**
 * Returns true if the process is running as a Node.js Single Executable Application.
 *
 * @remarks
 * When true, `process.execPath` is the absolute path of the SEA binary, and
 * `getAsset()` can retrieve embedded assets by name.
 * When false, the process is running as an ordinary Node.js program, and
 * `getAsset()` will throw.
 */
export function runningAsSea(): boolean {
  return seaIsSea()
}

/**
 * Returns the text content of an embedded SEA asset, or undefined if the asset
 * does not exist or the process is not running as a SEA.
 *
 * @param key - The asset name, typically a relative path like `"web.json"`.
 * @returns The asset content as a UTF-8 string, or undefined if absent or not a SEA.
 *
 * @remarks
 * Node's `getAsset(key)` throws when the key is not found. This function catches
 * that error and returns undefined instead, making it safe to probe for assets
 * that may not exist. If the process is not a SEA, the error is also caught.
 */
export function embeddedTextAsset(key: string): string | undefined {
  if (!seaIsSea()) {
    return undefined
  }

  try {
    const buffer = getAsset(key)
    // `getAsset` returns a Buffer or Uint8Array. Decode as UTF-8.
    if (buffer instanceof ArrayBuffer) {
      return new TextDecoder().decode(buffer)
    }
    return new TextDecoder().decode(new Uint8Array(buffer))
  } catch {
    // The key does not exist, or getAsset failed for another reason.
    return undefined
  }
}
