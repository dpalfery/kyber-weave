// Read side of the KyberDash Factory (Droid) consent file.
//
// `~/.kyberdash/factory-plugin.json` = { "version": 1, "content": <boolean> }.
// Content is OFF unless that exact file says otherwise: enabling the capture plugin never turns
// content on, only the owner writing this file does. Missing, unreadable, oversized, malformed
// and unknown-version files all read off.
//
// The plugin reads this file inside somebody else's tool, so this module takes node built-ins
// only (it is bundled into the plugin file) and never throws: every hostile input answers a
// decision. The write side is the shared consent writer (dash/src/capture/consent-file.ts).

import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

/** File name inside `~/.kyberdash/`. */
export const FACTORY_CONSENT_FILE_NAME = 'factory-plugin.json'

/** Optional override: `0` forces content off, `1` forces it on; anything else defers to the file. */
export const KYBERDASH_FACTORY_CONTEXT_ENV = 'KYBERDASH_FACTORY_CONTEXT'

/** The consent file is a few dozen bytes; anything larger is not ours and reads as off. */
const MAX_CONSENT_BYTES = 4096

/** `<home>/.kyberdash/factory-plugin.json`; `home` is injectable for tests. */
export function resolveConsentPath(home: string = homedir()): string {
  return join(home, '.kyberdash', FACTORY_CONSENT_FILE_NAME)
}

/**
 * True only for a readable file holding `{ version: 1, content: true }`.
 * Missing, unreadable, oversized, malformed, or unknown-version all mean off.
 */
export function readContentConsent(
  path: string,
  read: (path: string) => string = (p) => readFileSync(p, 'utf8'),
): boolean {
  try {
    const text = read(path)
    if (Buffer.byteLength(text, 'utf8') > MAX_CONSENT_BYTES) return false
    const parsed: unknown = JSON.parse(text)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return false
    const record = parsed as Record<string, unknown>
    return record['version'] === 1 && record['content'] === true
  } catch {
    // Any failure to read or parse is "off": the reader never throws at its callers.
    return false
  }
}

/** Consent for one event: the env override first, then the file. */
export function resolveContentConsent(env: NodeJS.ProcessEnv, consentPath: string): boolean {
  const override = env[KYBERDASH_FACTORY_CONTEXT_ENV]
  if (override === '1') return true
  if (override === '0') return false
  return readContentConsent(consentPath)
}
