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
  /** Reapplies KyberDash settings for an enabled harness via the shared enable path. */
  onReset: (harnessId: string) => void
}

export interface RowDecision {
  /** The actions a click may request; empty when the row cannot be switched. */
  actions: KyberCaptureAction[]
  /** Why the row offers no action; absent when it is switchable. */
  reason?: string
}

/**
 * Which actions a managed row offers, derived only from what the server
 * reported. WHY the rules: `on` offers Disable (receipt-gated restore, or the
 * user-authorized manual-key removal with no receipt) plus Reset elsewhere;
 * `partial` offers Enable to complete setup and Disable to turn collection
 * off under the same receipt/manual-key rules; an unknown state is not "off"
 * (switching it would write on a guess); a harness the server did not detect,
 * or could not inspect, is not offered an action unless KyberDash recorded a
 * receipt, in which case switching it stays possible.
 */
export function decideRow(harness: KyberCaptureHarness): RowDecision {
  if (harness.mode !== 'managed') return { actions: [] }
  if (harness.state === 'unknown' || harness.state === 'not-applicable') {
    return {
      actions: [],
      reason: 'State unknown: KyberDash could not read this harness\'s configuration, so it will not change it.',
    }
  }
  if (harness.state === 'on') return { actions: ['disable'] }
  if (harness.state === 'partial') return { actions: ['enable', 'disable'] }
  if (harness.installed === 'not-detected' && harness.receipt === 'absent') {
    return { actions: [], reason: 'Not detected on this machine.' }
  }
  if (harness.installed === 'unknown' && harness.receipt === 'absent') {
    return { actions: [], reason: 'Install state unknown: KyberDash could not inspect this harness\'s files.' }
  }
  return { actions: ['enable'] }
}

/**
 * Reset is shown only while managed collection is fully enabled. It re-runs
 * the shared engine enable path for that harness: missing or drifted managed
 * keys are rewritten, unrelated keys are left alone, and the engine's own
 * refusals (conflict, unparseable, unreadable) still apply.
 */
export function canReset(harness: KyberCaptureHarness): boolean {
  return harness.mode === 'managed' && harness.state === 'on'
}

const STATE_LABEL: Record<KyberCaptureState, string> = {
  on: 'Enabled',
  off: 'Disabled',
  partial: 'Needs setup',
  unknown: 'Unknown',
  'not-applicable': 'Read-only',
}

const INSTALLED_LABEL = {
  detected: 'Detected',
  'not-detected': 'Not detected',
  unknown: 'Install state unknown',
} as const

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
          <p>{alert.refusal.error || `Refused (${alert.refusal.reason}).`}</p>
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

function RowDetails({ harness }: { harness: KyberCaptureHarness }) {
  const applicableFiles = harness.files.filter((file) => file.applicable)
  return (
    <details data-testid={`settings-harness-details-${harness.id}`} className="mt-1.5 text-xs text-muted-foreground">
      <summary className="cursor-pointer text-[11px]">Configuration details</summary>
      <div className="mt-1 space-y-1.5">
        <p className="text-[11px]">{INSTALLED_LABEL[harness.installed]}</p>

        {applicableFiles.length > 0 && (
          <ul className="space-y-0.5 text-[11px]">
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

        {harness.warnings.map((warning, index) => (
          <p key={`${warning.code}-${warning.text}-${index}`} className="text-xs text-foreground">
            <span className="font-semibold">Warning ({warning.code}):</span> {warning.text}
          </p>
        ))}

        {harness.statusLines.length > 0 && (
          <ul data-testid={`settings-harness-status-lines-${harness.id}`} className="space-y-0.5">
            {harness.statusLines.map((line, index) => (
              <li key={`${line}-${index}`}>{line}</li>
            ))}
          </ul>
        )}

        {harness.manualSteps.length > 0 && (
          <div data-testid={`settings-harness-manual-${harness.id}`} className="space-y-1">
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
      </div>
    </details>
  )
}

function ToggleButton({
  harnessId,
  action,
  busy,
  describedBy,
  onToggle,
  testId,
}: {
  harnessId: string
  action: KyberCaptureAction
  busy: boolean
  describedBy?: string
  onToggle: HarnessCollectionListProps['onToggle']
  testId: string
}) {
  return (
    <button
      type="button"
      data-testid={testId}
      className="rounded-md border border-border px-2.5 py-1 text-xs font-medium text-foreground hover:bg-interactive-secondary disabled:cursor-not-allowed disabled:opacity-60"
      disabled={busy}
      aria-describedby={describedBy}
      onClick={() => {
        if (!busy) onToggle(harnessId, action)
      }}
    >
      {action === 'enable' ? 'Enable' : 'Disable'}
    </button>
  )
}

function HarnessRow({
  harness,
  busy,
  alert,
  onToggle,
  onReset,
}: {
  harness: KyberCaptureHarness
  busy: boolean
  alert: HarnessRowAlert | undefined
  onToggle: HarnessCollectionListProps['onToggle']
  onReset: HarnessCollectionListProps['onReset']
}) {
  const decision = decideRow(harness)
  const managed = harness.mode === 'managed'
  const reasonId = `settings-harness-reason-${harness.id}`
  const resettable = canReset(harness)

  return (
    <li
      data-testid={`settings-harness-row-${harness.id}`}
      className="rounded-md border border-border bg-card px-3 py-2.5"
    >
      <div className="flex items-center gap-2.5">
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm font-medium text-foreground">{harness.displayName}</span>
          <span
            data-testid={`settings-harness-state-${harness.id}`}
            className={harness.state === 'on' ? 'mt-0.5 block text-[11px] text-primary' : 'mt-0.5 block text-[11px] text-muted-foreground'}
          >
            {managed ? STATE_LABEL[harness.state] : 'Read-only'}
          </span>
        </span>
        <span className="flex shrink-0 gap-2">
          {resettable && (
            <button
              type="button"
              data-testid={`settings-harness-reset-${harness.id}`}
              className="rounded-md border border-border px-2.5 py-1 text-xs font-medium text-muted-foreground hover:bg-interactive-secondary disabled:cursor-not-allowed disabled:opacity-60"
              disabled={busy}
              onClick={() => {
                if (!busy) onReset(harness.id)
              }}
            >
              Reset
            </button>
          )}
          {decision.actions.map((action) => (
            <ToggleButton
              key={action}
              harnessId={harness.id}
              action={action}
              busy={busy}
              describedBy={decision.reason ? reasonId : undefined}
              onToggle={onToggle}
              testId={action === 'enable' ? `settings-harness-enable-${harness.id}` : `settings-harness-disable-${harness.id}`}
            />
          ))}
        </span>
      </div>
      {decision.reason && (
        <p id={reasonId} data-testid={reasonId} className="mt-1 text-xs text-muted-foreground">
          {decision.reason}
        </p>
      )}

      <RowDetails harness={harness} />

      {alert && <RowAlert harnessId={harness.id} alert={alert} />}
    </li>
  )
}

/**
 * One row per harness, in the order the server sent. Rendering only: which
 * actions a row offers is `decideRow`'s reading of the server's own report,
 * and the click is handed upward. Reset re-runs enable for an `on` row
 * through the same preview-then-confirm path.
 */
export function HarnessCollectionList({ harnesses, busy, alerts, onToggle, onReset }: HarnessCollectionListProps) {
  return (
    <ul data-testid="settings-harness-list" className="space-y-2">
      {harnesses.map((harness) => (
        <HarnessRow key={harness.id} harness={harness} busy={busy} alert={alerts[harness.id]} onToggle={onToggle} onReset={onReset} />
      ))}
    </ul>
  )
}
