// Antigravity capture writer (P2.6): status-only (G1-Q2 = (b)).
//
// The user's statusline bridge already posts chat spans. `status` reads
// `~/.gemini/antigravity-cli/settings.json` `statusLine.command` and reports
// whether that script names port 4318, then prints the one-line
// `declaredContextWindow` attribute for the user to add. `enable` and
// `disable` write nothing: agents never edit that script, and KyberDash does
// not invent a window the user has not declared.

import { existsSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { parse, type ParseError } from 'jsonc-parser'

import { pjoin, type PathEnv } from '../paths.js'

import type { PendingHarnessWriter } from './registry.js'

/** The KyberDash OTLP/HTTP port the bridge is grepped for. Read-only. */
const RECEIVER_PORT = '4318'

/**
 * One line the user can add on the bridge's chat span. A token count is not
 * filled in: the window exists only when the user declares it.
 */
const DECLARED_WINDOW_SNIPPET = 'span.set_attribute("declaredContextWindow", <positive token count>)'

/** A statusline script larger than this is not read: it is a hint, not a program to slurp. */
const MAX_BRIDGE_SCRIPT_BYTES = 1024 * 1024

export const antigravityHarness: PendingHarnessWriter = {
  kind: 'pending',
  id: 'antigravity',
  displayName: 'Antigravity',
  reason: 'status-only: the user statusline bridge is left unchanged',
  // D14: resolves on win32 through the same segments so `status` reports
  // instead of throwing; describeStatus keeps the host join because its
  // reads are status-only and follow the real filesystem.
  resolvePath: (home: string, env?: PathEnv) =>
    pjoin(env?.platform ?? process.platform, home, '.gemini', 'antigravity-cli', 'settings.json'),
  format: 'json',
  describeStatus: antigravityStatusDetail,
}

function antigravityStatusDetail(home: string, options?: { redact?: boolean }): readonly string[] {
  const settings = join(home, '.gemini', 'antigravity-cli', 'settings.json')
  const command = readStatusLineCommand(settings)
  const lines: string[] = []
  if (command === undefined) {
    lines.push(
      options?.redact === true
        ? 'statusline bridge: not set'
        : 'statusline bridge: statusLine.command is not set',
    )
  } else {
    lines.push(options?.redact === true ? 'statusline bridge: configured' : `statusline bridge: ${command}`)
    const script = readBridgeScript(command)
    // A digit boundary: `4318` must not match inside `14318` or `43180`.
    lines.push(
      script !== undefined && new RegExp(`(^|[^0-9])${RECEIVER_PORT}([^0-9]|$)`).test(script)
        ? `bridge route: the statusline script posts to ${RECEIVER_PORT}`
        : 'bridge route: the statusline script does not name the receiver port',
    )
  }
  lines.push(`declaredContextWindow snippet: ${DECLARED_WINDOW_SNIPPET}`)
  return lines
}

/** `statusLine.command` from the Antigravity CLI settings file, or undefined. */
function readStatusLineCommand(settingsPath: string): string | undefined {
  if (!existsSync(settingsPath)) return undefined
  let content: string
  try {
    content = readFileSync(settingsPath, 'utf8')
  } catch {
    return undefined
  }
  const errors: ParseError[] = []
  const parsed: unknown = parse(content, errors, { allowTrailingComma: true })
  if (errors.length > 0 || parsed === null || typeof parsed !== 'object') return undefined
  const command = (parsed as { statusLine?: { command?: unknown } }).statusLine?.command
  return typeof command === 'string' && command !== '' ? command : undefined
}

/**
 * The bridge script's text. The command is a path the user configured; it is
 * read and never executed. A missing or unreadable path is absence, not an error.
 */
function readBridgeScript(command: string): string | undefined {
  try {
    const info = statSync(command)
    if (!info.isFile() || info.size > MAX_BRIDGE_SCRIPT_BYTES) return undefined
    return readFileSync(command, 'utf8')
  } catch {
    return undefined
  }
}
