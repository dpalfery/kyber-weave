// The capture receipt: KyberDash's own record of what `enable` wrote (D11).
//
// Kept at `~/.kyberdash/capture-receipt.json`, separate from Kyber
// Utilities' and Kyber-Squad's receipts. Per file it records the path, the
// format, the keys written, each key's prior value (or absence), and the
// pre- and post-edit SHA-256 digests — everything `disable` needs to restore
// per key and everything `status` needs to report drift.

import { createHash, randomBytes } from 'node:crypto'
import { existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'

/** Hex SHA-256 of a document, for the receipt's pre- and post-edit digests. */
export function sha256Hex(data: string | Uint8Array): string {
  return createHash('sha256').update(data).digest('hex')
}

export type ReceiptPrior = { present: boolean; value?: unknown }

export type ReceiptKeyRecord = {
  /** The exact value KyberDash wrote. */
  written: unknown
  /** The value before `enable`, or `{ present: false }` when the key was absent. */
  prior: ReceiptPrior
}

export type ReceiptFileRecord = {
  path: string
  format: string
  harnessId: string
  keys: Record<string, ReceiptKeyRecord>
  preSha256: string
  postSha256: string
}

export type CaptureReceipt = {
  version: 1
  files: ReceiptFileRecord[]
}

/** The receipt path for a HOME directory, resolved at call time. */
export function receiptPathForHome(home: string): string {
  return join(home, '.kyberdash', 'capture-receipt.json')
}

/** Load the receipt, or null when `enable` has recorded nothing yet. */
export function loadCaptureReceipt(home: string): CaptureReceipt | null {
  const path = receiptPathForHome(home)
  if (!existsSync(path)) return null
  const parsed: unknown = JSON.parse(readFileSync(path, 'utf8'))
  if (
    parsed === null ||
    typeof parsed !== 'object' ||
    (parsed as { version?: unknown }).version !== 1 ||
    !Array.isArray((parsed as { files?: unknown }).files)
  ) {
    throw new Error(`kyberdash capture: receipt at ${path} is not a version-1 capture receipt`)
  }
  return parsed as CaptureReceipt
}

/** Persist the receipt, creating `~/.kyberdash` when needed. */
export function saveCaptureReceipt(home: string, receipt: CaptureReceipt): void {
  const path = receiptPathForHome(home)
  mkdirSync(dirname(path), { recursive: true })
  // Temp file + rename: a crash mid-write must never leave a truncated receipt.
  // Unguessable name, created exclusively (`wx`): a planted symlink or a concurrent writer
  // makes the open fail instead of redirecting the write.
  const temp = `${path}.${process.pid}.${randomBytes(8).toString('hex')}.tmp`
  try {
    writeFileSync(temp, `${JSON.stringify(receipt, null, 2)}\n`, { flag: 'wx' })
    renameSync(temp, path)
  } catch (error) {
    rmSync(temp, { force: true })
    throw error
  }
}
