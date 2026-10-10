// Claude Code capture writer (P2.2a).
//
// `~/.claude/settings.json` is strict JSON, so every `env.*` value is a
// string: a numeric `1` would be a JSON number, and `file:` would spill raw
// API bodies onto disk. Telemetry, traces, protocol and the receiver origin
// are ensure-if-absent. The core inserts those only when missing and leaves
// a present endpoint (including `http://localhost:4318`) out of the receipt.
// Beta-detailed keys are omitted from both maps, so enable cannot write them.

import { join } from 'node:path'

import type { ManagedHarnessWriter } from './registry.js'

/** Inline raw bodies. `file:` would write untruncated payloads beside the config. */
const RAW_API_BODIES_INLINE = '1'

/** Raises Claude's 61440 UTF-16 default without changing the value's JSON type. */
const CONTENT_MAX_LENGTH = '1048576'

function claudeEnv(values: Record<string, string>): Record<string, string> {
  const keys: Record<string, string> = {}
  for (const [name, value] of Object.entries(values)) keys[`env.${name}`] = value
  return keys
}

export const claudeCodeHarness: ManagedHarnessWriter = {
  kind: 'managed',
  id: 'claude-code',
  displayName: 'Claude Code',
  resolvePath: (home: string) => join(home, '.claude', 'settings.json'),
  format: 'json',
  desiredKeys: () =>
    claudeEnv({
      OTEL_LOGS_EXPORTER: 'otlp',
      OTEL_LOG_USER_PROMPTS: '1',
      OTEL_LOG_ASSISTANT_RESPONSES: '1',
      OTEL_LOG_TOOL_DETAILS: '1',
      OTEL_LOG_TOOL_CONTENT: '1',
      OTEL_LOG_RAW_API_BODIES: RAW_API_BODIES_INLINE,
      CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH: CONTENT_MAX_LENGTH,
    }),
  ensureIfAbsentKeys: (endpoint) =>
    claudeEnv({
      CLAUDE_CODE_ENABLE_TELEMETRY: '1',
      CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: '1',
      OTEL_TRACES_EXPORTER: 'otlp',
      OTEL_EXPORTER_OTLP_PROTOCOL: 'http/protobuf',
      OTEL_EXPORTER_OTLP_ENDPOINT: endpoint,
    }),
}
