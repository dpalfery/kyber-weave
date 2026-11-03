// Read side of the KyberDash OpenCode consent file (plan §3.4, Q1/Q2).
//
// `~/.kyberdash/opencode-plugin.json` = { "version": 1, "content": <boolean> }.
// The plugin reads it on every event or hook that carries content, so this module takes node
// built-ins only (it is bundled into the plugin file) and treats any failure
// as "off". The write side lands with the installer (T4).

import { readFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

/** File name inside `~/.kyberdash/`. */
export const OPENCODE_CONSENT_FILE_NAME = 'opencode-plugin.json'

/** Optional override: `0` forces content off, `1` forces it on; anything else defers to the file. */
export const KYBERDASH_OPENCODE_CONTEXT_ENV = 'KYBERDASH_OPENCODE_CONTEXT'

/** The consent file is a few dozen bytes; anything larger is not ours and reads as off. */
const MAX_CONSENT_BYTES = 4096

/** `<home>/.kyberdash/opencode-plugin.json`; `home` is injectable for tests. */
export function resolveConsentPath(home: string = homedir()): string {
  return join(home, '.kyberdash', OPENCODE_CONSENT_FILE_NAME)
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
    if (text.length > MAX_CONSENT_BYTES) return false
    const parsed: unknown = JSON.parse(text)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return false
    const record = parsed as Record<string, unknown>
    return record['version'] === 1 && record['content'] === true
  } catch {
    return false
  }
}

/** Consent for one event: the env override first, then the file. */
export function resolveContentConsent(env: NodeJS.ProcessEnv, consentPath: string): boolean {
  const override = env[KYBERDASH_OPENCODE_CONTEXT_ENV]
  if (override === '1') return true
  if (override === '0') return false
  return readContentConsent(consentPath)
}
