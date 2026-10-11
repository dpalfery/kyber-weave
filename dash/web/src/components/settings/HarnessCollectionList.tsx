import { useEffect, useRef } from 'react'

import { cn } from '../../lib/utils.js'
import type {
  KyberCaptureAction,
  KyberCaptureHarness,
  KyberCaptureRefusal,
  KyberCaptureState,
} from '../../lib/kyberApi.js'

/** What a row's last request left behind for the operator to read. */
export type HarnessRowAlert =
  | { kind: 'refused'; refusal: KyberCaptureRefusal }
  | { kind: 'failed'; message: string }

export interface HarnessCollectionListProps {
  harnesses: readonly KyberCaptureHarness[]
  /** A request is in flight anywhere in the drawer: every control waits. */
  busy: boolean
  alerts: Readonly<Record<string, HarnessRowAlert>>
  /** The row's own harness id and the action the click means. */
  onToggle: (harnessId: string, action: KyberCaptureAction) => void
}

export interface RowDecision {
  /** The action a click requests, or `null` when the row cannot be switched. */
  action: KyberCaptureAction | null
  /** Why the row is disabled; absent when it is switchable. */
  reason?: string
}

/**
 * Whether a managed row can be switched, derived only from what the server
 * reported. WHY these refusals: an unknown state is not "off" (switching it
 * would write on a guess); an `on` state without a receipt means the keys were
 * set outside KyberDash, so a disable would have nothing to restore; and a
 * harness the server did not detect is not offered, unless KyberDash recorded a
 * receipt, in which case switching it OFF must stay possible.
 */
export function decideRow(harness: KyberCaptureHarness): RowDecision {
  if (harness.mode !== 'managed') return { action: null }
  if (harness.state === 'unknown' || harness.state === 'not-applicable') {
    return {
      action: null,
      reason: 'State unknown: KyberDash could not read this harness\'s configuration, so it will not change it.',
    }
  }
  if (harness.state === 'on') {
    return harness.receipt === 'recorded'
      ? { action: 'disable' }
      : {
          action: null,
          reason:
            'Collection keys are set, but not by KyberDash (no receipt was recorded), so there is nothing to restore.',
        }
  }
  if (harness.installed === 'not-detected' && harness.receipt === 'absent') {
    return { action: null, reason: 'Not detected on this machine.' }
  }
  if (harness.installed === 'unknown' && harness.receipt === 'absent') {
    return { action: null, reason: 'Install state unknown: KyberDash could not inspect this harness\'s files.' }
  }
  return { action: 'enable' }
}

const STATE_LABEL: Record<KyberCaptureState, string> = {
  on: 'On',
  off: 'Off',
  partial: 'Partly on',
  unknown: 'Unknown',
  'not-applicable': 'Read-only',
}

const INSTALLED_LABEL = {
  detected: 'Detected',
  'not-detected': 'Not detected',
  unknown: 'Install state unknown',
} as const

/**
 * A checkbox that can show the third, mixed state. `indeterminate` is a DOM
 * property with no JSX attribute, so it is applied after render.
 */
function CollectionCheckbox({
  harness,
  checked,
  mixed,
  disabled,
  describedBy,
  onChange,
}: {
  harness: KyberCaptureHarness
  checked: boolean
  mixed: boolean
  disabled: boolean
  describedBy?: string
  onChange: () => void
}) {
  const ref = useRef<HTMLInputElement | null>(null)
  useEffect(() => {
    if (ref.current !== null) ref.current.indeterminate = mixed
  }, [mixed])
  return (
    <input
      ref={ref}
      type="checkbox"
      data-testid={`settings-harness-checkbox-${harness.id}`}
      className="h-4 w-4 shrink-0 accent-primary disabled:cursor-not-allowed"
      checked={checked}
      aria-checked={mixed ? 'mixed' : checked}
      aria-describedby={describedBy}
      disabled={disabled}
      onChange={onChange}
    />
  )
}

function DriftList({ drift }: { drift: ReadonlyArray<{ key: string; written: unknown; current: unknown }> }) {
  return (
    <ul className="mt-1 space-y-0.5 font-mono text-[11px]">
      {drift.map((entry) => (
        <li key={entry.key}>
          {entry.key}: written {String(entry.written)}, now {entry.current === null ? '(absent)' : String(entry.current)}
        </li>
      ))}
    </ul>
  )
}

function RowAlert({ harnessId, alert }: { harnessId: string; alert: HarnessRowAlert }) {
  return (
    <div
      role="alert"
      data-testid={`settings-harness-alert-${harnessId}`}
      className="mt-2 rounded-md border border-destructive/50 bg-destructive/10 px-2.5 py-2 text-xs text-foreground"
    >
      {alert.kind === 'failed' ? (
        <p>{alert.message}</p>
      ) : (
        <>
          <p>{alert.refusal.error || `Refused (${alert.refusal.reason}); no files were changed.`}</p>
          {alert.refusal.events.map((event, index) => (
            <div key={`${event.fileId}-${index}`} className="mt-1">
              {event.path && <p className="break-all text-muted-foreground">{event.path}</p>}
              {event.drift && event.drift.length > 0 && <DriftList drift={event.drift} />}
            </div>
          ))}
        </>
      )}
    </div>
  )
}

function HarnessRow({
  harness,
  busy,
  alert,
  onToggle,
}: {
  harness: KyberCaptureHarness
  busy: boolean
  alert: HarnessRowAlert | undefined
  onToggle: HarnessCollectionListProps['onToggle']
}) {
  const decision = decideRow(harness)
  const managed = harness.mode === 'managed'
  const reasonId = `settings-harness-reason-${harness.id}`
  const applicableFiles = harness.files.filter((file) => file.applicable)

  return (
    <li
      data-testid={`settings-harness-row-${harness.id}`}
      className="rounded-md border border-border bg-card px-3 py-2.5"
    >
      <div className="flex items-center gap-2.5">
        {managed && (
          <CollectionCheckbox
            harness={harness}
            checked={harness.state === 'on'}
            mixed={harness.state === 'partial'}
            disabled={busy || decision.action === null}
            describedBy={decision.reason ? reasonId : undefined}
            onChange={() => {
              if (!busy && decision.action !== null) onToggle(harness.id, decision.action)
            }}
          />
        )}
        <span className="min-w-0 flex-1 text-sm font-medium text-foreground">{harness.displayName}</span>
        <span
          data-testid={`settings-harness-state-${harness.id}`}
          className={cn(
            'rounded-full border border-border px-2 py-0.5 text-[11px]',
            harness.state === 'on' ? 'text-primary' : 'text-muted-foreground',
          )}
        >
          {managed ? STATE_LABEL[harness.state] : 'Read-only'}
        </span>
      </div>
      <p className="mt-1 text-[11px] text-muted-foreground">{INSTALLED_LABEL[harness.installed]}</p>
      {decision.reason && (
        <p id={reasonId} data-testid={reasonId} className="mt-1 text-xs text-muted-foreground">
          {decision.reason}
        </p>
      )}

      {managed && applicableFiles.length > 0 && (
        <ul className="mt-1.5 space-y-0.5 text-[11px] text-muted-foreground">
          {applicableFiles.map((file) => (
            <li key={file.id} className="break-all">
              {file.path}
              {file.exists === null
                ? ' (could not be inspected)'
                : ` (${file.presentKeys.length} of ${file.expectedKeys.length} keys set)`}
              {file.drift && file.drift.length > 0 && <DriftList drift={file.drift} />}
            </li>
          ))}
        </ul>
      )}

      {harness.warnings.map((warning) => (
        <p key={`${warning.code}-${warning.text}`} className="mt-1.5 text-xs text-foreground">
          <span className="font-semibold">Warning ({warning.code}):</span> {warning.text}
        </p>
      ))}

      {harness.statusLines.length > 0 && (
        <ul data-testid={`settings-harness-status-lines-${harness.id}`} className="mt-1.5 space-y-0.5 text-xs text-muted-foreground">
          {harness.statusLines.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      )}

      {harness.manualSteps.length > 0 && (
        <div data-testid={`settings-harness-manual-${harness.id}`} className="mt-1.5 space-y-1 text-xs text-muted-foreground">
          {harness.manualSteps.map((step) => (
            <div key={step.id}>
              <p>{step.text}</p>
              {step.env && (
                <p className="mt-0.5">
                  Variable <code>{step.env.name}</code>; value to use: <code>{step.env.value}</code>. KyberDash cannot
                  see the environment this harness is launched from, so it does not know whether this has been done.
                </p>
              )}
            </div>
          ))}
        </div>
      )}

      {alert && <RowAlert harnessId={harness.id} alert={alert} />}
    </li>
  )
}

/**
 * One row per harness, in the order the server sent. Rendering only: which row
 * is switchable, and in which direction, is `decideRow`'s reading of the
 * server's own report, and the click is handed upward.
 */
export function HarnessCollectionList({ harnesses, busy, alerts, onToggle }: HarnessCollectionListProps) {
  return (
    <ul data-testid="settings-harness-list" className="space-y-2">
      {harnesses.map((harness) => (
        <HarnessRow key={harness.id} harness={harness} busy={busy} alert={alerts[harness.id]} onToggle={onToggle} />
      ))}
    </ul>
  )
}
