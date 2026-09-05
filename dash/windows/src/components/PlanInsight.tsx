import { useEffect, useState } from 'react'
import { invoke } from '@tauri-apps/api/core'
import type { MenubarPayload } from '../lib/payload'
import type { CurrencyState } from '../lib/currency'
import { formatCompactCurrency, formatTokens, plural } from '../lib/currency'
import { relativeFuture } from '../lib/dates'
import type { PlanUsage, PlanWindow } from '../lib/plan'
import { projectWindow, earliestReset } from '../lib/plan'
import { BulbIcon, ChevronRight, KeySlashIcon, PersonDashedIcon, WarningIcon, ArrowUpRight } from './Icons'

/// Sonnet-weighted approximation the mac app uses to turn a dollar saving into tokens.
const USD_PER_MILLION_EFFECTIVE_TOKENS = 9
const MILLION = 1_000_000
const PLAN_REFRESH_MS = 5 * 60_000

type LoadState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'loaded'; usage: Extract<PlanUsage, { state: 'ok' }> }
  | { kind: 'no_credentials' }
  | { kind: 'failed'; message: string }

type Props = {
  payload: MenubarPayload | null
  currency: CurrencyState
  onOpenTerminal: (args: string[]) => void
  onConnectClaude: () => void
}

export function PlanInsight({ payload, currency, onOpenTerminal, onConnectClaude }: Props) {
  const [state, setState] = useState<LoadState>({ kind: 'idle' })
  const [now, setNow] = useState(() => new Date())

  const load = async () => {
    setState(prev => (prev.kind === 'loaded' ? prev : { kind: 'loading' }))
    try {
      const usage = await invoke<PlanUsage>('plan_usage')
      if (usage.state === 'ok') setState({ kind: 'loaded', usage })
      else if (usage.state === 'no_credentials') setState({ kind: 'no_credentials' })
      else setState({ kind: 'failed', message: usage.message })
    } catch (err) {
      setState({ kind: 'failed', message: err instanceof Error ? err.message : String(err) })
    }
    setNow(new Date())
  }

  useEffect(() => {
    load()
    const id = setInterval(load, PLAN_REFRESH_MS)
    return () => clearInterval(id)
  }, [])

  switch (state.kind) {
    case 'idle':
    case 'loading':
      return (
        <div className="plan-state">
          <PersonDashedIcon size={22} className="plan-state-icon" />
          <div className="plan-state-title-muted">Loading your plan...</div>
          <div className="plan-state-note">Reading Claude Code credentials from this machine.</div>
        </div>
      )
    case 'no_credentials':
      return (
        <div className="plan-state">
          <KeySlashIcon size={20} className="plan-state-icon" />
          <div className="plan-state-title">No Claude subscription connected</div>
          <div className="plan-state-note">Click Connect to sign in with Claude in a terminal, then return here.</div>
          <div className="plan-actions">
            <button type="button" className="btn btn-prominent" onClick={() => onConnectClaude()}>Connect Claude</button>
            <button type="button" className="btn" onClick={load}>Retry</button>
          </div>
        </div>
      )
    case 'failed':
      return (
        <div className="plan-state">
          <WarningIcon size={18} filled={false} className="plan-state-icon plan-state-icon-accent" />
          <div className="plan-state-title">Couldn't load plan data</div>
          <div className="plan-state-error">{state.message}</div>
          <div className="plan-actions">
            <button type="button" className="btn btn-prominent" onClick={() => onConnectClaude()}>Reconnect Claude</button>
            <button type="button" className="btn" onClick={load}>Retry</button>
          </div>
        </div>
      )
    case 'loaded': {
      const { usage } = state
      const reset = earliestReset(usage.windows)
      return (
        <div className="plan-insight">
          <div className="plan-header">
            <span className="plan-tier">{usage.tier}</span>
            {reset && <span className="plan-reset">Resets {relativeFuture(reset, now)}</span>}
          </div>
          <div className="plan-rows">
            {usage.windows.map(w => <UtilizationRow key={w.key} window={w} now={now} />)}
          </div>
          {payload && payload.optimize.findingCount > 0 && payload.optimize.savingsUSD > 0 && (
            <button type="button" className="savings-badge" onClick={() => onOpenTerminal(['optimize'])}>
              <BulbIcon size={10} className="savings-badge-icon" />
              <span>
                Save ~{formatCompactCurrency(payload.optimize.savingsUSD, currency)} / ~
                {formatTokens((payload.optimize.savingsUSD / USD_PER_MILLION_EFFECTIVE_TOKENS) * MILLION)} tokens
                {' · '}{plural(payload.optimize.findingCount, 'finding')}
              </span>
              <ChevronRight size={8} className="savings-badge-chevron" />
            </button>
          )}
        </div>
      )
    }
  }
}

function UtilizationRow({ window, now }: { window: PlanWindow; now: Date }) {
  const projection = projectWindow(window, now)
  const clamped = Math.min(Math.max(window.percent, 0), 100)
  const marker = projection ? Math.min(Math.max(projection.percent, 0), 100) : null

  let caption: string | null = null
  if (projection) {
    const pct = Math.round(projection.percent)
    if (projection.source === 'historical') caption = `Based on last cycle: ${pct}%`
    else if (projection.willOverflow && projection.hitsLimitAt) caption = `On pace: ${pct}% at reset · hits 100% ${relativeFuture(projection.hitsLimitAt, now)}`
    else caption = `On pace: ${pct}% at reset`
  }

  return (
    <div className="util-row">
      <div className="util-row-head">
        <span className="util-label">{window.label}</span>
        <span className="util-percent">{Math.round(clamped)}%</span>
      </div>
      <div className="util-bar">
        <div className="util-bar-fill" style={{ width: `${clamped}%` }} />
        {marker !== null && <div className="util-bar-marker" style={{ left: `calc(${marker}% - 0.75px)` }} />}
      </div>
      {caption && (
        <div className={`util-caption ${projection?.willOverflow ? 'util-caption-warn' : ''}`}>
          {projection?.willOverflow ? <WarningIcon size={8} /> : <ArrowUpRight size={8} />}
          <span>{caption}</span>
        </div>
      )}
    </div>
  )
}
