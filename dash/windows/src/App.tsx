import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { invoke } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'

import type { MenubarPayload } from './lib/payload'
import type { CurrencyState } from './lib/currency'
import { USD, formatCurrency, trayBadgeText } from './lib/currency'
import { PayloadCache } from './lib/cache'
import { relativePast } from './lib/dates'
import { applyTheme, currentTheme, readSetting, writeSetting } from './lib/settings'
import { TRAY_BADGE_SUPPORTED } from './lib/platform'
import { AgentTabStrip, detectedProviders } from './components/AgentTabStrip'
import type { Provider } from './components/AgentTabStrip'
import { ModelsSection } from './components/ModelsSection'
import { InsightPills, INSIGHT_ORDER, isInsightMode, type InsightMode } from './components/InsightPills'
import { TrendInsight } from './components/TrendInsight'
import { ForecastInsight } from './components/ForecastInsight'
import { PulseInsight } from './components/PulseInsight'
import { StatsInsight } from './components/StatsInsight'
import { PlanInsight } from './components/PlanInsight'
import { FindingsSection } from './components/FindingsSection'
import { ActivitySection } from './components/ActivitySection'
import { LoadingOverlay } from './components/LoadingOverlay'
import { EmptyProviderState } from './components/EmptyProviderState'
import { NoDataState } from './components/NoDataState'
import { SetupState, type CliStatus } from './components/SetupState'
import { StarBanner } from './components/StarBanner'
import { HeroSection } from './components/HeroSection'
import { PeriodTabs, PERIOD_LABELS } from './components/PeriodTabs'
import type { Period } from './components/PeriodTabs'
import { FooterBar } from './components/FooterBar'
import { ErrorToast } from './components/ErrorToast'
import { SettingsPanel, type ThemeChoice } from './components/SettingsPanel'

const payloadCache = new PayloadCache<MenubarPayload>()

/// Background cadence, mirroring mac/Sources/CodeBurnMenubar/RefreshCadence.swift: every
/// fetch is a full Node process, so the popover being closed has to cost less than it being
/// open. Visible, a tick refreshes today/all plus the selected period/provider with optimize
/// findings; hidden, a slower tick refreshes only today/all and skips optimize, since the
/// tray badge and tooltip are the only things anyone can see. Entries younger than STALE_MS
/// are left alone when the popover is re-opened.
const REFRESH_ACTIVE_MS = 60_000
const REFRESH_IDLE_MS = 120_000
const STALE_MS = 60_000

type FetchOptions = {
  includeOptimize: boolean
  showOverlay: boolean
}

export function App() {
  const [period, setPeriod] = useState<Period>('today')
  const [provider, setProvider] = useState<Provider>('all')
  const [payload, setPayload] = useState<MenubarPayload | null>(null)
  const [todayPayload, setTodayPayload] = useState<MenubarPayload | null>(null)
  const [currency, setCurrency] = useState<CurrencyState>(USD)
  const [overlay, setOverlay] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [insight, setInsight] = useState<InsightMode>(() => {
    const saved = readSetting('insight')
    return isInsightMode(saved) ? saved : 'trend'
  })
  const [cliStatus, setCliStatus] = useState<CliStatus | null>(null)
  const [cliChecking, setCliChecking] = useState(false)
  const [version, setVersion] = useState('')
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null)
  const [theme, setTheme] = useState(() => currentTheme())
  const [trayBadge, setTrayBadge] = useState(() => TRAY_BADGE_SUPPORTED && readSetting('trayBadge') !== 'off')
  const [showSettings, setShowSettings] = useState(false)
  // The window starts hidden and is shown by a tray click, which emits `codeburn://shown`.
  const [popoverVisible, setPopoverVisible] = useState(false)
  const [themeChoice, setThemeChoice] = useState<ThemeChoice>(() => {
    const saved = readSetting('theme')
    return saved === 'dark' || saved === 'light' ? saved : 'system'
  })

  const selection = useRef({ period, provider })
  selection.current = { period, provider }

  const fetchKey = useCallback(async (p: Period, prov: Provider, opts: FetchOptions) => {
    if (payloadCache.isInFlight(p, prov)) return
    payloadCache.markInFlight(p, prov)
    const isSelected = () => selection.current.period === p && selection.current.provider === prov
    if (opts.showOverlay && isSelected()) setOverlay(true)
    try {
      const json = await invoke<MenubarPayload>('fetch_payload', {
        period: p,
        provider: prov,
        includeOptimize: opts.includeOptimize,
      })
      // A quiet (no-optimize) refresh must not wipe findings a previous full fetch had.
      if (!opts.includeOptimize) {
        const previous = payloadCache.get(p, prov)
        if (previous) json.optimize = previous.optimize
      }
      payloadCache.set(p, prov, json)
      if (isSelected()) {
        setPayload(json)
        // "updated Xs ago" describes what the user is looking at, so only a fetch of the
        // visible key may stamp it - a background today/all tick must not.
        setLastUpdated(new Date())
      }
      if (p === 'today' && prov === 'all') setTodayPayload(json)
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err)
      if (message.includes('CLI not found')) {
        const status = await invoke<CliStatus>('cli_status').catch(() => null)
        if (status) setCliStatus(status)
      } else if (isSelected()) {
        setError(message)
      }
    } finally {
      payloadCache.clearInFlight(p, prov)
      if (isSelected()) setOverlay(false)
    }
  }, [])

  const refreshAll = useCallback(async (opts: FetchOptions) => {
    const { period: p, provider: prov } = selection.current
    if (!(p === 'today' && prov === 'all')) {
      fetchKey('today', 'all', { includeOptimize: false, showOverlay: false })
    }
    await fetchKey(p, prov, opts)
  }, [fetchKey])

  /// The single source of truth for the CLI gate. Nothing else writes a "compatible"
  /// verdict: a payload that happens to parse does not prove the CLI is new enough, and a
  /// probe from the settings panel must not be able to invent one either.
  const checkCli = useCallback(async () => {
    setCliChecking(true)
    try {
      const status = await invoke<CliStatus>('cli_status')
      setCliStatus(status)
      if (status.found && status.compatible) {
        refreshAll({ includeOptimize: true, showOverlay: true })
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setCliChecking(false)
    }
  }, [refreshAll])

  const cliReady = cliStatus !== null && cliStatus.found && cliStatus.compatible

  // Probe the gate before the first fetch: an old CLI emits a payload missing fields the
  // popover reads, which used to blank the whole window instead of showing the setup screen.
  useEffect(() => {
    invoke<string>('app_version').then(setVersion).catch(() => {})
    checkCli()
    // Startup only; checkCli is re-run from the setup screen and settings on demand.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!cliReady) return
    const tick = popoverVisible
      ? () => refreshAll({ includeOptimize: true, showOverlay: false })
      : () => fetchKey('today', 'all', { includeOptimize: false, showOverlay: false })
    const id = setInterval(tick, popoverVisible ? REFRESH_ACTIVE_MS : REFRESH_IDLE_MS)
    return () => clearInterval(id)
  }, [cliReady, popoverVisible, refreshAll, fetchKey])

  useEffect(() => {
    const cached = payloadCache.get(period, provider)
    setPayload(cached)
    if (!cliReady) return
    if (!cached) {
      fetchKey(period, provider, { includeOptimize: true, showOverlay: true })
    } else if (payloadCache.age(period, provider) > STALE_MS) {
      fetchKey(period, provider, { includeOptimize: true, showOverlay: false })
    }
  }, [period, provider, cliReady, fetchKey])

  useEffect(() => {
    const unlistenRefresh = listen('codeburn://refresh', () => refreshAll({ includeOptimize: true, showOverlay: true }))
    const unlistenShown = listen('codeburn://shown', () => {
      setPopoverVisible(true)
      const { period: p, provider: prov } = selection.current
      if (payloadCache.age(p, prov) > STALE_MS) refreshAll({ includeOptimize: true, showOverlay: false })
    })
    const unlistenHidden = listen('codeburn://hidden', () => setPopoverVisible(false))
    const unlistenTheme = listen('codeburn://toggle-theme', () => toggleTheme())
    return () => {
      unlistenRefresh.then(fn => fn())
      unlistenShown.then(fn => fn())
      unlistenHidden.then(fn => fn())
      unlistenTheme.then(fn => fn())
    }
  }, [refreshAll])

  useEffect(() => {
    const saved = readSetting('theme')
    if (saved === 'dark' || saved === 'light') applyTheme(saved)
    setTheme(currentTheme())
    const media = window.matchMedia('(prefers-color-scheme: dark)')
    const onChange = () => setTheme(currentTheme())
    media.addEventListener('change', onChange)
    return () => media.removeEventListener('change', onChange)
  }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') invoke('hide_popover').catch(() => {})
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const todayCost = todayPayload?.current?.cost ?? null

  useEffect(() => {
    if (todayCost === null) return
    const text = `CodeBurn · ${formatCurrency(todayCost, currency)} today`
    invoke('set_tray_tooltip', { text }).catch(() => {})
  }, [todayCost, currency])

  useEffect(() => {
    if (!TRAY_BADGE_SUPPORTED) return
    const text = trayBadge && todayCost !== null ? trayBadgeText(todayCost, currency) : null
    invoke('set_tray_badge', { text }).catch(err => setError(`Tray badge: ${String(err)}`))
  }, [todayCost, currency, trayBadge])


  const chooseTheme = (choice: ThemeChoice) => {
    applyTheme(choice === 'system' ? null : choice)
    setThemeChoice(choice)
    setTheme(currentTheme())
  }

  const toggleTheme = () => {
    chooseTheme(currentTheme() === 'dark' ? 'light' : 'dark')
  }

  const setTrayBadgePref = (on: boolean) => {
    setTrayBadge(on)
    writeSetting('trayBadge', on ? 'on' : 'off')
  }

  const applyCurrency = async (code: string) => {
    try {
      setCurrency(await invoke<CurrencyState>('set_currency', { code }))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  const openTerminal = (args: string[]) => {
    invoke('open_terminal_command', { args }).catch(err => setError(String(err)))
  }
  const connectClaude = () => {
    invoke('open_claude_login').catch(err => setError(String(err)))
  }

  const selectInsight = (mode: InsightMode) => {
    setInsight(mode)
    writeSetting('insight', mode)
  }

  const providers = detectedProviders(todayPayload)
  const planVisible = provider === 'claude' || (provider === 'all' && providers.length === 1 && providers[0] === 'claude')
  const visibleModes = useMemo(
    () => INSIGHT_ORDER.filter(m => m !== 'plan' || planVisible),
    [planVisible],
  )
  const activeInsight = visibleModes.includes(insight) ? insight : 'trend'

  const cliBlocked = cliStatus !== null && (!cliStatus.found || !cliStatus.compatible)
  // The version gate above is what keeps these fields present; the optional reads are the
  // backstop that turns a surprising payload into an empty state rather than a blank window.
  const isFilteredEmpty = payload !== null && provider !== 'all'
    && (payload.current?.cost ?? 0) <= 0 && (payload.current?.calls ?? 0) === 0
  const neverAnyData = payload !== null && provider === 'all'
    && (payload.current?.calls ?? 0) === 0 && (payload.current?.sessions ?? 0) === 0
    && (payload.history?.daily?.length ?? 0) === 0

  const footnote = [version ? `CodeBurn v${version}` : 'CodeBurn', lastUpdated ? `updated ${relativePast(lastUpdated)}` : null]
    .filter(Boolean)
    .join(' · ')

  return (
    <div className="popover">
      <header className="header">
        <div className="brand">
          <span className="brand-primary">Code</span>
          <span className="brand-accent">Burn</span>
        </div>
        <div className="subhead">AI Coding Cost Tracker</div>
      </header>

      {!cliBlocked && !showSettings && (
        <AgentTabStrip selected={provider} onSelect={setProvider} payload={todayPayload} currency={currency} />
      )}

      <div className="main-content">
        {showSettings ? (
          <SettingsPanel
            onBack={() => setShowSettings(false)}
            version={version}
            currency={currency}
            onCurrency={applyCurrency}
            themeChoice={themeChoice}
            onThemeChoice={chooseTheme}
            trayBadge={trayBadge}
            onTrayBadge={setTrayBadgePref}
            cliStatus={cliStatus}
            onCheckCli={checkCli}
            cliChecking={cliChecking}
            onQuit={() => invoke('quit_app').catch(() => {})}
          />
        ) : cliBlocked && cliStatus ? (
          <SetupState status={cliStatus} checking={cliChecking} onCheckAgain={checkCli} />
        ) : (
          <>
            <HeroSection payload={payload} currency={currency} periodLabel={PERIOD_LABELS[period]} isToday={period === 'today'} />
            <PeriodTabs selected={period} onSelect={setPeriod} />

            {isFilteredEmpty ? (
              <EmptyProviderState provider={provider} period={period} />
            ) : neverAnyData ? (
              <NoDataState onRefresh={() => refreshAll({ includeOptimize: true, showOverlay: true })} />
            ) : (
              <>
                <div className="insight-area">
                  <InsightPills selected={activeInsight} onSelect={selectInsight} modes={visibleModes} />
                  {activeInsight === 'plan' && (
                    <PlanInsight payload={payload} currency={currency} onOpenTerminal={openTerminal} onConnectClaude={connectClaude} />
                  )}
                  {activeInsight === 'trend' && <TrendInsight days={payload?.history?.daily ?? []} currency={currency} />}
                  {activeInsight === 'forecast' && <ForecastInsight days={payload?.history?.daily ?? []} currency={currency} />}
                  {activeInsight === 'pulse' && payload && <PulseInsight payload={payload} currency={currency} />}
                  {activeInsight === 'stats' && payload && <StatsInsight payload={payload} currency={currency} period={period} />}
                </div>
                {payload?.current && (
                  <>
                    <ActivitySection payload={payload} currency={currency} />
                    <ModelsSection
                      models={payload.current.topModels}
                      inputTokens={payload.current.inputTokens}
                      outputTokens={payload.current.outputTokens}
                      cacheHitPercent={payload.current.cacheHitPercent}
                      currency={currency}
                    />
                    <FindingsSection payload={payload} currency={currency} onOpenTerminal={openTerminal} />
                  </>
                )}
              </>
            )}
            {overlay && <LoadingOverlay periodLabel={PERIOD_LABELS[period]} />}
          </>
        )}
      </div>

      <FooterBar
        currency={currency}
        onCurrency={applyCurrency}
        loading={overlay}
        onRefresh={() => refreshAll({ includeOptimize: true, showOverlay: true })}
        onExport={format => openTerminal(['export', '-f', format])}
        onOpenReport={() => openTerminal(['report'])}
        onToggleTheme={toggleTheme}
        onQuit={() => invoke('quit_app').catch(() => {})}
        themeLabel={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
        trayBadge={trayBadge}
        onToggleTrayBadge={() => setTrayBadgePref(!trayBadge)}
        onOpenSettings={() => setShowSettings(s => !s)}
        settingsOpen={showSettings}
        footnote={footnote}
      />

      <StarBanner />

      {error && <ErrorToast message={error} onDismiss={() => setError(null)} />}
    </div>
  )
}
