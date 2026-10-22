import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { SlideOutDrawer } from '../ui/SlideOutDrawer.js'
import {
  fetchKyberCaptureStatus,
  postKyberCapture,
  updateKyberSettings,
  type KyberCaptureAction,
  type KyberCaptureCheck,
  type KyberCaptureChange,
  type KyberCaptureEvent,
  type KyberCaptureRequest,
  type KyberCaptureResult,
  type KyberCaptureValue,
} from '../../lib/kyberApi.js'
import { DashboardPortSetting } from './DashboardPortSetting.js'
import { ATTENTION_TEXT, HarnessCollectionList, type HarnessRowAlert } from './HarnessCollectionList.js'

export interface SettingsDrawerProps {
  open: boolean
  onClose: () => void
}

const CAPTURE_KEY = ['kyber-capture'] as const

const EVENT_LABEL: Record<KyberCaptureEvent['kind'], string> = {
  'would-write': 'Will write',
  wrote: 'Wrote',
  noop: 'Already set',
  'would-restore': 'Will restore',
  restored: 'Restored',
  refused: 'Refused',
  error: 'Error',
}

function formatValue(value: KyberCaptureValue | undefined): string {
  if (value === undefined) return '(absent)'
  if (value === null) return 'null'
  return typeof value === 'string' ? JSON.stringify(value) : String(value)
}

function ChangeLine({ change }: { change: KyberCaptureChange }) {
  return (
    <li className="font-mono text-[11px]">
      {change.key}: {formatValue(change.before)} → {formatValue(change.after)}
    </li>
  )
}

/** The dry-run answer: every file and key the write would touch, before anything is written. */
function PreviewPanel({
  request,
  result,
  displayName,
  reset,
  busy,
  onConfirm,
  onCancel,
}: {
  request: KyberCaptureRequest
  result: KyberCaptureResult
  displayName: string
  /** A reset re-runs enable to reapply KyberDash settings; unrelated keys are preserved. */
  reset: boolean
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const verb = reset ? 'Reset' : request.action === 'enable' ? 'Turn on' : 'Turn off'
  const changed = result.events.some((event) => event.changes && event.changes.length > 0)
  return (
    <section
      data-testid="settings-preview"
      aria-label={`Preview: ${verb.toLowerCase()} collection for ${displayName}`}
      className="rounded-md border border-primary/50 bg-card p-3"
    >
      <h3 className="text-sm font-semibold text-foreground">
        {verb} collection for {displayName}
      </h3>
      <p className="mt-0.5 text-xs text-muted-foreground">Preview only. Nothing has been written yet.</p>
      {result.events.map((event, index) => (
        <div key={`${event.fileId}-${index}`} className="mt-2">
          <p className="text-xs font-medium text-foreground">
            {EVENT_LABEL[event.kind]}
            {event.code ? ` (${event.code})` : ''}
          </p>
          <p className="break-all text-[11px] text-muted-foreground">{event.path}</p>
          {event.changes && event.changes.length > 0 && (
            <ul className="mt-1 space-y-0.5">
              {event.changes.map((change) => (
                <ChangeLine key={change.key} change={change} />
              ))}
            </ul>
          )}
        </div>
      ))}
      {!changed && <p className="mt-2 text-xs text-muted-foreground">No keys would change.</p>}
      {result.receiver.willEnable && (
        <p data-testid="settings-preview-receiver-note" className="mt-2 text-xs text-foreground">
          {result.receiver.hosted
            ? 'The OTLP receiver is already on.'
            : 'This will also turn on the OTLP receiver, which is currently off.'}
        </p>
      )}
      <div className="mt-3 flex gap-2">
        <button
          type="button"
          data-testid="settings-preview-confirm"
          className="rounded-md border border-primary bg-primary px-3 py-1 text-xs font-medium text-primary-foreground disabled:cursor-not-allowed disabled:opacity-60"
          disabled={busy}
          onClick={onConfirm}
        >
          Confirm
        </button>
        <button
          type="button"
          data-testid="settings-preview-cancel"
          className="rounded-md border border-border px-3 py-1 text-xs font-medium text-foreground hover:bg-interactive-secondary disabled:cursor-not-allowed disabled:opacity-60"
          disabled={busy}
          onClick={onCancel}
        >
          Cancel
        </button>
      </div>
    </section>
  )
}

interface PendingPreview {
  request: KyberCaptureRequest
  result: KyberCaptureResult
  /** A reset re-runs enable; the confirm wording says so. */
  reset: boolean
}

/**
 * The check's one-line verdict. Every count and the verdict itself come from
 * the server's `check` block; this only words and colours them.
 */
function CheckSummary({ check }: { check: KyberCaptureCheck }) {
  const at = new Date(check.checkedAt)
  const when = Number.isNaN(at.getTime()) ? '' : `Checked ${at.toLocaleTimeString()} · `
  const text =
    check.health === 'attention'
      ? `${check.attention} ${check.attention === 1 ? 'harness needs' : 'harnesses need'} attention (${check.issueCount} ${check.issueCount === 1 ? 'issue' : 'issues'})`
      : check.health === 'ok'
        ? `${check.enabled} enabled ${check.enabled === 1 ? 'harness' : 'harnesses'}, no issues found`
        : 'No harness collection is enabled'
  return (
    <p
      role="status"
      data-testid="settings-check-summary"
      data-health={check.health}
      className={`text-xs ${check.health === 'attention' ? `font-semibold ${ATTENTION_TEXT}` : check.health === 'ok' ? 'text-primary' : 'text-muted-foreground'}`}
    >
      {when}
      {text}
    </p>
  )
}

/**
 * The drawer's content. It exists only while the drawer is open, so closing
 * drops its preview and alerts and opening fetches a fresh status. Display
 * state only: every enabled/disabled/checked value below is read from the
 * server's report, and every change is a request to `/api/kyber/*`.
 */
function SettingsContent() {
  const queryClient = useQueryClient()
  const status = useQuery({
    queryKey: CAPTURE_KEY,
    queryFn: fetchKyberCaptureStatus,
    retry: false,
    refetchOnWindowFocus: false,
  })
  const [preview, setPreview] = useState<PendingPreview | null>(null)
  const [alerts, setAlerts] = useState<Record<string, HarnessRowAlert>>({})
  const [resultText, setResultText] = useState<{ text: string; error: boolean } | null>(null)
  const [receiverStays, setReceiverStays] = useState(false)

  const capture = useMutation({ mutationFn: postKyberCapture })
  const receiver = useMutation({
    mutationFn: (hosted: boolean) => updateKyberSettings({ receiverHosted: hosted }),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: CAPTURE_KEY })
      void queryClient.invalidateQueries({ queryKey: ['kyber-settings'] })
    },
  })
  const busy = capture.isPending || receiver.isPending

  const harnesses = status.data?.harnesses ?? []
  const nameOf = (id: string): string => harnesses.find((h) => h.id === id)?.displayName ?? id

  const clearAlert = (id: string): void =>
    setAlerts((current) => {
      const { [id]: _removed, ...rest } = current
      return rest
    })

  const requestPreview = (harnessId: string, action: KyberCaptureAction, reset = false): void => {
    if (busy) return
    // The user authorized the Settings Disable to remove manually-set OTEL
    // keys when no KyberDash receipt exists; the opt-in rides both the
    // dry-run preview and the confirmed write, scoped to that harness's
    // declared keys by the engine. Every other action omits it.
    const request: KyberCaptureRequest =
      action === 'disable'
        ? { action, harness: harnessId, dryRun: true, removeUnreceiptedKeys: true }
        : { action, harness: harnessId, dryRun: true }
    clearAlert(harnessId)
    setResultText(null)
    setReceiverStays(false)
    capture.mutate(request, {
      onSuccess: (outcome) => {
        if (outcome.outcome === 'refused') {
          setPreview(null)
          setAlerts((current) => ({ ...current, [harnessId]: { kind: 'refused', refusal: outcome } }))
          return
        }
        setPreview({ request, result: outcome, reset })
      },
      onError: () => {
        setPreview(null)
        setAlerts((current) => ({
          ...current,
          [harnessId]: { kind: 'failed', message: 'The preview could not be completed. Nothing was changed.' },
        }))
      },
    })
  }

  /** Reset re-runs the shared enable path for one enabled harness, previewed first like any write. */
  const requestReset = (harnessId: string): void => {
    requestPreview(harnessId, 'enable', true)
  }

  const confirm = (): void => {
    if (busy || preview === null) return
    const request: KyberCaptureRequest = {
      ...preview.request,
      dryRun: false,
      ...(preview.request.removeUnreceiptedKeys === true ? { expectedPreviewEvents: preview.result.events } : {}),
    }
    const harnessId = request.harness
    capture.mutate(request, {
      onSuccess: (outcome) => {
        setPreview(null)
        if (outcome.outcome === 'refused') {
          setAlerts((current) => ({ ...current, [harnessId]: { kind: 'refused', refusal: outcome } }))
        } else {
          const verb = preview.reset ? 'reapplied' : request.action === 'enable' ? 'turned on' : 'turned off'
          setResultText(
            outcome.receiver.enableFailed
              ? {
                  text: `Collection was ${verb} for ${nameOf(harnessId)}, but the OTLP receiver could not be turned on. Turn it on in the receiver section.`,
                  error: true,
                }
              : outcome.outcome === 'noop'
                ? { text: `${nameOf(harnessId)} already had these settings; nothing changed.`, error: false }
                : { text: `Collection ${verb} for ${nameOf(harnessId)}.`, error: false },
          )
          setReceiverStays(
            request.action === 'disable' && outcome.receiver.lastManagedHarnessDisabled && outcome.receiver.hosted,
          )
        }
        void queryClient.invalidateQueries({ queryKey: CAPTURE_KEY })
      },
      onError: () => {
        setPreview(null)
        setAlerts((current) => ({
          ...current,
          [harnessId]: {
            kind: 'failed',
            message: 'The change could not be completed. Refresh the status to see what is on disk.',
          },
        }))
        void queryClient.invalidateQueries({ queryKey: CAPTURE_KEY })
      },
    })
  }

  const managedOn = harnesses.filter((h) => h.mode === 'managed' && h.state === 'on').length
  const check = status.data?.check ?? null

  return (
    <>
      <details data-testid="settings-harness-group" open className="rounded-md border border-border bg-card px-3 py-2.5">
        <summary className="cursor-pointer text-sm font-medium text-foreground">
          Harness collection{' '}
          <span data-testid="settings-harness-enabled-count" className="font-normal text-muted-foreground">
            {managedOn} enabled
          </span>
        </summary>
        <div className="mt-2 space-y-2">
          <div className="flex items-start justify-between gap-3">
            <p className="text-xs text-muted-foreground">
              OpenTelemetry collection per harness. Reset reapplies KyberDash settings for an enabled harness and
              preserves unrelated preferences. Each change is previewed before anything is written.
            </p>
            <button
              type="button"
              data-testid="settings-refresh-status-button"
              className="shrink-0 rounded-md border border-border px-2.5 py-1 text-xs font-medium text-foreground hover:bg-interactive-secondary disabled:cursor-not-allowed disabled:opacity-60"
              disabled={status.isFetching}
              onClick={() => void status.refetch()}
            >
              {status.isFetching && !status.isPending ? 'Checking…' : 'Check configuration'}
            </button>
          </div>

          {check && <CheckSummary check={check} />}

          {status.isError && (
            <p role="alert" data-testid="settings-load-error" className="text-xs text-foreground">
              The collection status could not be read, so no controls are shown. Use Check configuration to try again.
            </p>
          )}
          {status.isPending && <p className="text-xs text-muted-foreground">Reading collection status…</p>}

          {status.data && (
            <HarnessCollectionList
              harnesses={harnesses}
              busy={busy}
              alerts={alerts}
              onToggle={requestPreview}
              onReset={requestReset}
            />
          )}

          {preview && (
            <PreviewPanel
              request={preview.request}
              result={preview.result}
              displayName={nameOf(preview.request.harness)}
              reset={preview.reset}
              busy={busy}
              onConfirm={confirm}
              onCancel={() => setPreview(null)}
            />
          )}

          {resultText && (
            <p
              data-testid="settings-result"
              role={resultText.error ? 'alert' : 'status'}
              className="text-xs text-foreground"
            >
              {resultText.text}
            </p>
          )}
        </div>
      </details>

      {status.data && (
        <section
          data-testid="settings-receiver"
          aria-label="OTLP receiver"
          className="rounded-md border border-border bg-card px-3 py-2.5"
        >
          <label className="flex items-center gap-2.5 text-sm font-medium text-foreground">
            <input
              type="checkbox"
              data-testid="settings-receiver-toggle"
              className="h-4 w-4 accent-primary disabled:cursor-not-allowed"
              checked={status.data.receiver.hosted}
              disabled={busy}
              onChange={() => {
                if (!busy) receiver.mutate(!status.data.receiver.hosted)
              }}
            />
            Host the OTLP receiver in this KyberDash
          </label>
          <p data-testid="settings-receiver-liveness" className="mt-1 text-xs text-muted-foreground">
            {status.data.receiver.detail}
          </p>
          {receiver.isError && (
            <p role="alert" className="mt-1 text-xs text-foreground">
              The receiver setting could not be saved.
            </p>
          )}
          {receiverStays && (
            <p data-testid="settings-receiver-note" className="mt-1.5 text-xs text-foreground">
              The OTLP receiver is still hosted, because other exporters may post to it. Switch it off in the
              receiver section below if you no longer want it.
            </p>
          )}
          <p className="mt-1 text-[11px] text-muted-foreground">
            A newly started receiver can take several seconds to answer; use Check configuration to check again.
          </p>
        </section>
      )}

      <DashboardPortSetting />
    </>
  )
}

/**
 * The Settings drawer: a right slide-out over the shared `SlideOutDrawer`
 * shell, with focus management on because it is a dialog the operator opens
 * from a header button and must be returned to.
 */
export function SettingsDrawer({ open, onClose }: SettingsDrawerProps) {
  return (
    <SlideOutDrawer
      open={open}
      onClose={onClose}
      label="Settings"
      testIdPrefix="settings-drawer"
      manageFocus
      title="Settings"
      subtitle="Collection, receiver and dashboard port"
    >
      <SettingsContent />
    </SlideOutDrawer>
  )
}
