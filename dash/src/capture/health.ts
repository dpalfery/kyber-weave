// The deterministic configuration check behind the Settings drawer's
// "Check configuration" button and every `GET /api/kyber/capture`.
//
// WHY this exists beside `state`: `state` answers "are KyberDash's keys in the
// file?", and a harness can answer yes while exporting nowhere. Enable keeps an
// owner's pre-existing endpoint by design, so `~/.claude/settings.json` can hold
// every key with the endpoint on the gRPC port, and the row read "Enabled" while
// the receiver saw nothing. `health` answers the question the operator actually
// has — "will this harness's telemetry reach the receiver?" — and `issues` names
// every reason it might not, with what to do about it.
//
// The check is a pure function of the structured status and the receiver view:
// the same files and the same receiver give the same verdict, byte for byte,
// except `checkedAt`. It reads nothing, writes nothing and opens no socket of its
// own; the receiver view comes from the route's existing liveness probe.
// Verdicts live here, in the engine, so the dashboard and the tray only render
// them (ADR 0033).

import type { CaptureHarnessStatus } from './status.js'

/**
 * - `ok`: enabled, and nothing found that would stop delivery.
 * - `attention`: enabled or half-enabled, with at least one issue; or a config
 *   that could not be read. The surfaces render this yellow.
 * - `off`: not enabled. Not an issue: the operator chose it.
 * - `read-only`: a status-only harness KyberDash does not configure.
 */
export type CaptureHealth = 'ok' | 'attention' | 'off' | 'read-only'

/** `blocking` stops delivery outright; `warning` may, or degrades what arrives. */
export type CaptureIssueSeverity = 'blocking' | 'warning'

export type CaptureIssue = {
  code: string
  severity: CaptureIssueSeverity
  text: string
  /** The config file the issue is about, kept out of `text` so the wording stays machine-independent. */
  path?: string
  /** The concrete next step, when one exists. */
  remedy?: string
}

export type CaptureReceiverView = {
  hosted: boolean
  liveness: 'kyberdash' | 'foreign' | 'none' | 'unknown'
  detail: string
}

export type AssessedCaptureHarness = CaptureHarnessStatus & {
  health: CaptureHealth
  issues: CaptureIssue[]
}

export type CaptureCheckSummary = {
  /** ISO-8601 time the check ran. The only non-deterministic field. */
  checkedAt: string
  /** `attention` if any harness needs it; `ok` if at least one is enabled and none do; else `off`. */
  health: 'ok' | 'attention' | 'off'
  /** Managed harnesses whose keys are on or partly on. */
  enabled: number
  /** Harnesses whose health is `attention`. */
  attention: number
  issueCount: number
}

/** Warning codes that mean the harness cannot reach the receiver at all. */
const BLOCKING_WARNING_CODES: ReadonlySet<string> = new Set([
  'endpoint-differs',
  'protocol-differs',
  'exporter-not-delivering',
])

/** Kept-value warnings come only from single JSON files, so the first existing file is theirs. */
function warningPath(code: string, harness: CaptureHarnessStatus): string | undefined {
  if (code !== 'endpoint-differs' && code !== 'protocol-differs' && code !== 'owner-value-differs') return undefined
  return harness.files.find((file) => file.applicable && file.exists === true)?.path
}

function warningRemedy(code: string, harness: CaptureHarnessStatus, endpoint: string): string | undefined {
  const restart = `restart ${harness.displayName} so it reads the change`
  switch (code) {
    case 'endpoint-differs':
      return `Set it to ${endpoint}, then ${restart}. KyberDash never overwrites an endpoint you set.`
    case 'protocol-differs':
      return `Set it to http/protobuf, then ${restart}.`
    case 'owner-value-differs':
      return `Change it (KyberDash keeps values you set), then ${restart}.`
    case 'exporter-not-delivering':
      return 'Follow the warning text; the exporter must be OTLP/HTTP for records to arrive.'
    default:
      return undefined
  }
}

function receiverIssues(receiver: CaptureReceiverView): CaptureIssue[] {
  if (!receiver.hosted) {
    return [
      {
        code: 'receiver-not-hosted',
        severity: 'blocking',
        text: 'This KyberDash does not host the OTLP receiver, so nothing this harness exports is collected.',
        remedy: 'Turn on "Host the OTLP receiver in this KyberDash" below.',
      },
    ]
  }
  if (receiver.liveness === 'none') {
    return [
      {
        code: 'receiver-down',
        severity: 'blocking',
        text: `The OTLP receiver is not answering: ${receiver.detail}`,
        remedy: 'A newly started receiver can take a few seconds; check again. If it persists, restart KyberDash.',
      },
    ]
  }
  if (receiver.liveness === 'foreign') {
    return [
      {
        code: 'receiver-foreign',
        severity: 'blocking',
        text: `Another program holds the receiver port: ${receiver.detail}`,
        remedy: 'Stop the other listener, or move it to a different port, then check again.',
      },
    ]
  }
  return []
}

function configIssues(harness: CaptureHarnessStatus, endpoint: string): CaptureIssue[] {
  const issues: CaptureIssue[] = []
  for (const file of harness.files) {
    if (!file.applicable) continue
    if (file.missingKeys.length > 0) {
      issues.push({
        code: 'missing-keys',
        severity: 'blocking',
        text: `Missing ${file.missingKeys.join(', ')}`,
        path: file.path,
        remedy: 'Use Enable to write the missing keys (previewed first).',
      })
    }
    if (file.mismatchedKeys.length > 0) {
      issues.push({
        code: 'value-mismatch',
        severity: 'warning',
        text: `${file.mismatchedKeys.join(', ')} ${file.mismatchedKeys.length === 1 ? 'holds a value' : 'hold values'} other than KyberDash's`,
        path: file.path,
        remedy: 'Use Enable to reapply KyberDash\'s values (previewed first).',
      })
    }
    if (file.drift !== null && file.drift.length > 0) {
      issues.push({
        code: 'drift',
        severity: 'warning',
        text: `${file.drift.map((entry) => entry.key).join(', ')} changed after KyberDash wrote ${file.drift.length === 1 ? 'it' : 'them'}`,
        path: file.path,
        remedy: 'Review the change. Disable leaves a drifted file unchanged; Enable previews what it would reapply.',
      })
    }
  }
  for (const warning of harness.warnings) {
    const remedy = warningRemedy(warning.code, harness, endpoint)
    const path = warningPath(warning.code, harness)
    issues.push({
      code: warning.code,
      severity: BLOCKING_WARNING_CODES.has(warning.code) ? 'blocking' : 'warning',
      text: warning.text,
      ...(path === undefined ? {} : { path }),
      ...(remedy === undefined ? {} : { remedy }),
    })
  }
  return issues
}

/**
 * One harness's verdict. Issues are ordered config first, then receiver: the
 * config is what the operator edits, the receiver is shared by every row.
 */
export function assessHarness(
  harness: CaptureHarnessStatus,
  receiver: CaptureReceiverView,
  endpoint: string,
): { health: CaptureHealth; issues: CaptureIssue[] } {
  if (harness.mode !== 'managed') return { health: 'read-only', issues: [] }
  if (harness.state === 'unknown' || harness.state === 'not-applicable') {
    return {
      health: 'attention',
      issues: [
        {
          code: 'config-unreadable',
          severity: 'blocking',
          text: 'KyberDash could not read or parse this harness\'s configuration, so it cannot tell whether collection works.',
          remedy: 'Open Configuration details to see which file, fix it, then check again.',
        },
      ],
    }
  }
  if (harness.state === 'off') return { health: 'off', issues: [] }
  const issues = [...configIssues(harness, endpoint), ...receiverIssues(receiver)]
  return { health: issues.length === 0 ? 'ok' : 'attention', issues }
}

/** Every harness's verdict plus the drawer's one-line summary. */
export function assessCapture(
  harnesses: readonly CaptureHarnessStatus[],
  receiver: CaptureReceiverView,
  endpoint: string,
  now: Date = new Date(),
): { harnesses: AssessedCaptureHarness[]; check: CaptureCheckSummary } {
  const assessed = harnesses.map((harness) => ({ ...harness, ...assessHarness(harness, receiver, endpoint) }))
  const enabled = assessed.filter(
    (harness) => harness.mode === 'managed' && (harness.state === 'on' || harness.state === 'partial'),
  ).length
  const attention = assessed.filter((harness) => harness.health === 'attention').length
  return {
    harnesses: assessed,
    check: {
      checkedAt: now.toISOString(),
      health: attention > 0 ? 'attention' : enabled > 0 ? 'ok' : 'off',
      enabled,
      attention,
      issueCount: assessed.reduce((total, harness) => total + harness.issues.length, 0),
    },
  }
}
