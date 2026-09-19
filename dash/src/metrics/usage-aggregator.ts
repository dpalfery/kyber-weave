import { CATEGORY_LABELS, type ProjectSummary, type TaskCategory, type DateRange } from '../types.js'
import type { PeriodData } from './period.js'
import { parseAllSessions, filterProjectsByName, filterProjectsByDays, filterProjectsByDateRange, isSessionHydrationComplete } from '../ingest/parser.js'
import { findUnpricedModels, getFlatRateModelsConfigHash, getLocalModelSavingsConfigHash, getPriceOverridesConfigHash, isExpectedFreeModel } from '../pricing/models.js'
import { aggregateProjectsIntoDays, buildPeriodDataFromDays, dateKeyInTz } from './day-aggregator.js'
import { scanUserCorrections, medianTimeToFirstEditMs, aggregateFileChurn, computePricingCoverage } from './workflow-insights.js'
import { sessionBillableOutputTokens } from './session-output.js'
import { getDaysInRange, ensureCacheHydrated, emptyCache, toDateString, type DailyCache, type DailyEntry, type ProjectDayStats, type ProviderDaySlice } from '../ingest/daily-cache.js'


export function buildPeriodData(label: string, projects: ProjectSummary[]): PeriodData {
  const sessions = projects.flatMap(p => p.sessions)
  const catTotals: Record<string, { turns: number; cost: number; savingsUSD: number; editTurns: number; oneShotTurns: number }> = {}
  const modelTotals: Record<string, { calls: number; cost: number; savingsUSD: number; estimatedCostUSD: number; tokens: number }> = {}
  let inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0

  for (const sess of sessions) {
    inputTokens += sess.totalInputTokens
    outputTokens += sessionBillableOutputTokens(sess)
    cacheReadTokens += sess.totalCacheReadTokens
    cacheWriteTokens += sess.totalCacheWriteTokens
    for (const [cat, d] of Object.entries(sess.categoryBreakdown)) {
      if (!catTotals[cat]) catTotals[cat] = { turns: 0, cost: 0, savingsUSD: 0, editTurns: 0, oneShotTurns: 0 }
      catTotals[cat].turns += d.turns
      catTotals[cat].cost += d.costUSD
      catTotals[cat].savingsUSD += d.savingsUSD
      catTotals[cat].editTurns += d.editTurns
      catTotals[cat].oneShotTurns += d.oneShotTurns
    }
    for (const [model, d] of Object.entries(sess.modelBreakdown)) {
      if (!modelTotals[model]) modelTotals[model] = { calls: 0, cost: 0, savingsUSD: 0, estimatedCostUSD: 0, tokens: 0 }
      modelTotals[model].calls += d.calls
      modelTotals[model].cost += d.costUSD
      modelTotals[model].savingsUSD += d.savingsUSD
      modelTotals[model].estimatedCostUSD += d.estimatedCostUSD ?? 0
      modelTotals[model].tokens += d.tokens.inputTokens + d.tokens.outputTokens + d.tokens.cacheReadInputTokens + d.tokens.cacheCreationInputTokens
    }
  }

  const unpricedModels = findUnpricedModels(Object.entries(modelTotals)
    .map(([model, d]) => ({ model, calls: d.calls, cost: d.cost, tokens: d.tokens })))
  const costBearingCalls = Object.entries(modelTotals)
    .reduce((s, [model, d]) => s + (model === '<synthetic>' || isExpectedFreeModel(model) ? 0 : d.calls), 0)
  const unpricedCalls = unpricedModels.reduce((s, m) => s + m.calls, 0)
  const corrections = scanUserCorrections(projects)

  return {
    label,
    cost: projects.reduce((s, p) => s + p.totalCostUSD, 0),
    savingsUSD: projects.reduce((s, p) => s + p.totalSavingsUSD, 0),
    estimatedCostUSD: projects.reduce((s, p) => s + (p.totalEstimatedCostUSD ?? 0), 0),
    calls: projects.reduce((s, p) => s + p.totalApiCalls, 0),
    sessions: projects.reduce((s, p) => s + p.sessions.length, 0),
    inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens,
    categories: Object.entries(catTotals)
      .sort(([, a], [, b]) => b.cost - a.cost)
      .map(([cat, d]) => ({ name: CATEGORY_LABELS[cat as TaskCategory] ?? cat, ...d })),
    models: Object.entries(modelTotals)
      .sort(([, a], [, b]) => b.cost - a.cost)
      .map(([name, d]) => ({ name, calls: d.calls, cost: d.cost, savingsUSD: d.savingsUSD, estimatedCostUSD: d.estimatedCostUSD })),
    unpricedModels,
    workflow: {
      corrections: corrections.corrections,
      correctionRate: corrections.correctionRate,
      medianTimeToFirstEditMs: medianTimeToFirstEditMs(projects),
    },
    topReworkedFiles: aggregateFileChurn(projects),
    pricingCoverage: computePricingCoverage(costBearingCalls, unpricedCalls),
  }
}

export function getDailyCacheConfigHash(): string {
  const savingsHash = getLocalModelSavingsConfigHash()
  const overridesHash = getPriceOverridesConfigHash()
  const flatRateHash = getFlatRateModelsConfigHash()
  return `localModelSavings=${savingsHash}\u0002priceOverrides=${overridesHash}\u0002flatRateModels=${flatRateHash}`
}

async function hydrateCache(): Promise<DailyCache> {
  try {
    return await ensureCacheHydrated(
      (range) => parseAllSessions(range, 'all'),
      aggregateProjectsIntoDays,
      getDailyCacheConfigHash(),
      // Never finalize the daily history off a partial (interrupted) session
      // hydration — that is what froze empty older days into the chart.
      isSessionHydrationComplete,
      // On a tz-change re-derive the same parse is re-aggregated under the old
      // tzKey so carried slices can be reduced by the turns that re-bucketed
      // across local midnight (issue #770).
      (projects, tz) => aggregateProjectsIntoDays(projects, (iso) => dateKeyInTz(iso, tz)),
    )
  } catch (err) {
    // Previously swallowed silently, which turned any backfill failure into an
    // empty trend/history with no signal (issue #441). Per-file parse errors no
    // longer reach here (they're isolated in parseProviderSources), so anything
    // that does is exceptional and worth surfacing.
    process.stderr.write(
      `codeburn: daily history backfill failed; the trend chart may be incomplete. ` +
      `${err instanceof Error ? err.message : String(err)}\n`
    )
    return emptyCache()
  }
}

/**
 * Finish the existing durable day cache from an already-normalized lifetime
 * session index. The parser callback is only a range projection of `projects`:
 * no source discovery, transcript read, or session-cache parse is repeated.
 */
export async function hydrateDailyCacheFromNormalizedProjects(projects: ProjectSummary[], complete = true): Promise<DailyCache> {
  return ensureCacheHydrated(
    (range) => Promise.resolve(filterProjectsByDateRange(projects, range)),
    aggregateProjectsIntoDays,
    getDailyCacheConfigHash(),
    () => complete,
    (rangeProjects, tz) => aggregateProjectsIntoDays(rangeProjects, (iso) => dateKeyInTz(iso, tz)),
  )
}

export type PeriodInfo = { range: DateRange; label: string }
export type AggregateOpts = {
  provider?: string
  project?: string[]
  exclude?: string[]
  daysSelection?: { range: DateRange; label: string; days: Set<string> } | null
  optimize?: boolean
  claudeConfigSourceId?: string | null
  /// Build the granular per-bucket timeline (`history.timeline`). Defaults to true.
  timeline?: boolean
}


/// account for expired-source days.
function sliceDayToProvider(day: DailyEntry, provider: string): DailyEntry {
  const s = Object.hasOwn(day.providers, provider) ? day.providers[provider] : undefined
  if (!s) {
    return {
      date: day.date, cost: 0, savingsUSD: 0, calls: 0, sessions: 0,
      inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
      editTurns: 0, oneShotTurns: 0, models: {}, categories: {}, providers: {},
      ...(day.carried ? { carried: true as const } : {}),
    }
  }
  return {
    date: day.date,
    cost: s.cost,
    savingsUSD: s.savingsUSD ?? 0,
    calls: s.calls,
    sessions: s.sessions ?? 0,
    inputTokens: s.inputTokens ?? 0,
    outputTokens: s.outputTokens ?? 0,
    cacheReadTokens: s.cacheReadTokens ?? 0,
    cacheWriteTokens: s.cacheWriteTokens ?? 0,
    editTurns: s.editTurns ?? 0,
    oneShotTurns: s.oneShotTurns ?? 0,
    models: s.models ?? {},
    categories: s.categories ?? {},
    providers: { [provider]: s },
    ...(s.projects ? { projects: s.projects } : {}),
    ...(day.carried ? { carried: true as const } : {}),
  }
}

/// Does a cached day's project entry pass the active name filters? Mirrors
/// parser.filterProjectsByName exactly — case-insensitive substring match
/// against the project name OR its filesystem path, include first then exclude —
/// so a filter selects the same projects whether it is resolved against a fresh
/// parse or against the day cache. Patterns arrive pre-lowercased. `path` is
/// absent on entries whose sessions were gone before it could be recorded; the
/// name is then all there is to match on, as it is for the display layers.
function dayProjectMatches(name: string, path: string | undefined, include: string[], exclude: string[]): boolean {
  const n = name.toLowerCase()
  const p = (path ?? '').toLowerCase()
  const hit = (pattern: string): boolean => n.includes(pattern) || (p !== '' && p.includes(pattern))
  if (include.length > 0 && !include.some(hit)) return false
  if (exclude.length > 0 && exclude.some(hit)) return false
  return true
}

/// Sum the per-project day stats that pass the filters. `defineProperty` so a
/// project directory named "__proto__" stays an own key instead of mutating the
/// prototype link (same reason day-aggregator does it when writing them).
function sumMatchingProjects(
  projects: Record<string, ProjectDayStats>,
  include: string[],
  exclude: string[],
): { cost: number; calls: number; savingsUSD: number; sessions: number; projects: Record<string, ProjectDayStats>; matched: number } {
  const out = { cost: 0, calls: 0, savingsUSD: 0, sessions: 0, projects: {} as Record<string, ProjectDayStats>, matched: 0 }
  for (const [name, p] of Object.entries(projects)) {
    if (!dayProjectMatches(name, p.path, include, exclude)) continue
    out.cost += p.cost
    out.calls += p.calls
    out.savingsUSD += p.savingsUSD ?? 0
    out.sessions += p.sessions ?? 0
    out.matched += 1
    Object.defineProperty(out.projects, name, { value: p, enumerable: true, writable: true, configurable: true })
  }
  return out
}

/// Collapse a day to the slice matching the active --project/--exclude filters,
/// the project-level counterpart of sliceDayToProvider. Without this a
/// project-filtered headline counted every historical day WHOLE — excluded
/// projects included — while every detail panel (By Project / By Activity / By
/// Model, all built from the name-filtered live parse) left them out, so the two
/// could not be reconciled.
///
/// `DailyEntry.projects` (cache v15+) carries cost/calls/savingsUSD/sessions per
/// project, so those four are recomputed EXACTLY. The day's tokens, models and
/// categories have no per-project split to slice, so they are dropped here
/// rather than reported as if they belonged to the surviving projects;
/// buildDurablePeriod refills them from the project-filtered live parse, which
/// is exact for every session that still exists.
///
/// A day recorded before v15 has no `projects` at all and nothing can
/// reconstruct one once the sources are gone. Such a day cannot be attributed to
/// any project, so it contributes nothing to a project-filtered total and its
/// cost is surfaced as `unattributedCostUSD` instead of being silently folded in
/// (understating with a stated figure beats overstating with excluded spend).
function sliceDayToProject(day: DailyEntry, include: string[], exclude: string[]): DailyEntry {
  const zeroDay = (): DailyEntry => ({
    date: day.date, cost: 0, savingsUSD: 0, calls: 0, sessions: 0,
    inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
    editTurns: 0, oneShotTurns: 0, models: {}, categories: {}, providers: {},
    ...(day.carried ? { carried: true as const } : {}),
  })
  if (!day.projects) return zeroDay()
  const totals = sumMatchingProjects(day.projects, include, exclude)
  if (totals.matched === 0) return zeroDay()

  // Provider slices carry their own per-project split, so `--provider X` on top
  // of a project filter stays consistent with the day-level slice. A slice
  // adopted from a pre-v15 cache has no split and is dropped for the same
  // reason the day-level one is.
  const providers: Record<string, ProviderDaySlice> = {}
  for (const [name, slice] of Object.entries(day.providers)) {
    if (!slice.projects) continue
    const sliced = sumMatchingProjects(slice.projects, include, exclude)
    if (sliced.matched === 0) continue
    Object.defineProperty(providers, name, {
      value: { cost: sliced.cost, calls: sliced.calls, savingsUSD: sliced.savingsUSD, sessions: sliced.sessions, projects: sliced.projects },
      enumerable: true, writable: true, configurable: true,
    })
  }

  return {
    date: day.date,
    cost: totals.cost,
    savingsUSD: totals.savingsUSD,
    calls: totals.calls,
    sessions: totals.sessions,
    inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0,
    editTurns: 0, oneShotTurns: 0, models: {}, categories: {},
    providers,
    projects: totals.projects,
    ...(day.carried ? { carried: true as const } : {}),
  }
}

/// The durable day set behind a period's headline: historical days from the
/// carry-forward cache (up to yesterday, INCLUDING days whose session files have
/// expired) unioned with today parsed live, then narrowed to the requested range
/// and (when given) the heatmap day selection. Identical construction to the
/// menubar's all-provider headline — this IS that construction, extracted.
///
/// `sliceHistorical` narrows the cache-sourced days only. Today's days come from
/// a parse the caller already name-filtered, so re-slicing them would be a no-op
/// at best and could only lose data the filter meant to keep.
function unionDaysForPeriod(
  cache: DailyCache,
  todayAllDays: DailyEntry[],
  periodInfo: PeriodInfo,
  daysSelection: Set<string> | null,
  sliceHistorical?: (day: DailyEntry) => DailyEntry,
): DailyEntry[] {
  const now = new Date()
  const yesterdayStr = toDateString(new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1))
  const rangeStartStr = toDateString(periodInfo.range.start)
  const rangeEndStr = toDateString(periodInfo.range.end)
  const historicalRangeEndStr = rangeEndStr < yesterdayStr ? rangeEndStr : yesterdayStr
  const cacheDays = rangeStartStr <= historicalRangeEndStr
    ? getDaysInRange(cache, rangeStartStr, historicalRangeEndStr)
    : []
  // Apply the day selection BEFORE slicing so a day the heatmap filtered out
  // never reaches the slicer (which tallies what it could not attribute).
  const selectedCacheDays = daysSelection ? cacheDays.filter(d => daysSelection.has(d.date)) : cacheDays
  const historicalDays = sliceHistorical ? selectedCacheDays.map(d => sliceHistorical(d)) : selectedCacheDays
  const todayInRange = todayAllDays.filter(d => d.date >= rangeStartStr && d.date <= rangeEndStr)
  const unfiltered = [...historicalDays, ...todayInRange].sort((a, b) => a.date.localeCompare(b.date))
  return daysSelection ? unfiltered.filter(d => daysSelection.has(d.date)) : unfiltered
}

export type IndexedDurableOverview = {
  cost: number
  savingsUSD: number
  calls: number
  sessions: number
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  carriedCostUSD: number
}

/**
 * Project a dashboard headline from one normalized lifetime index. Existing
 * durable cache days remain authoritative (so expired transcripts do not
 * disappear); normalized days fill only dates the durable cache does not yet
 * contain, which is the cold first-index case. Today always comes from the
 * live normalized index. No parser is called here.
 */
export function buildDurableOverviewFromNormalizedIndex(
  periodInfo: PeriodInfo,
  normalizedProjects: ProjectSummary[],
  cache: DailyCache,
  opts: AggregateOpts = {},
): IndexedDurableOverview {
  const pf = opts.provider ?? 'all'
  const include = (opts.project ?? []).map(value => value.toLowerCase())
  const exclude = (opts.exclude ?? []).map(value => value.toLowerCase())
  const hasProjectFilter = include.length > 0 || exclude.length > 0
  const filteredProjects = filterProjectsByName(normalizedProjects, opts.project ?? [], opts.exclude ?? [])
  const scanProjects = filterProjectsByDateRange(filteredProjects, periodInfo.range)
  const now = new Date()
  const todayStr = toDateString(now)
  const normalizedDays = aggregateProjectsIntoDays(filteredProjects)
  const todayDays = normalizedDays
    .filter(day => day.date === todayStr)
  const historicalSlice = hasProjectFilter
    ? (day: DailyEntry): DailyEntry => sliceDayToProject(day, include, exclude)
    : undefined
  const cachedAllDays = unionDaysForPeriod(cache, todayDays, periodInfo, null, historicalSlice)
  const cachedDates = new Set(cache.days.map(day => day.date))
  const rangeStartStr = toDateString(periodInfo.range.start)
  const rangeEndStr = toDateString(periodInfo.range.end)
  // A provider-scoped index deliberately does not rewrite the shared all-
  // provider durable cache. When that cache has no row for a surviving
  // historical source, fill the missing date from this same normalized index;
  // existing durable rows stay authoritative so expired history is preserved.
  const canFillMissingDates = cache.complete !== true || cache.days.length === 0
  const normalizedHistoricalDays = canFillMissingDates
    ? normalizedDays.filter(day =>
        day.date !== todayStr
        && day.date >= rangeStartStr
        && day.date <= rangeEndStr
        && !cachedDates.has(day.date)
      )
    : []
  const allDays = [...cachedAllDays, ...normalizedHistoricalDays].sort((a, b) => a.date.localeCompare(b.date))
  const normalizedByDate = new Map(normalizedDays.map(day => [day.date, day]))
  const days = pf === 'all' ? allDays : allDays.map(day => {
    if (Object.hasOwn(day.providers, pf)) return sliceDayToProvider(day, pf)
    const normalized = normalizedByDate.get(day.date)
    // The shared cache can be complete for a date while lacking this selected
    // provider's slice (for example, Claude was cached before Codex appeared).
    // Fill only that absent slice from the provider-scoped normalized index.
    // An existing slice remains authoritative, retaining carried/expired money
    // and preventing the surviving source from being counted twice.
    return normalized && Object.hasOwn(normalized.providers, pf)
      ? sliceDayToProvider(normalized, pf)
      : sliceDayToProvider(day, pf)
  })
  const data = buildPeriodDataFromDays(days, periodInfo.label)

  // Fields whose durable day rows cannot project under a project filter come
  // from the same normalized period slice that feeds the visible detail panels.
  const scan = buildPeriodData(periodInfo.label, scanProjects)
  data.sessions = Math.max(data.sessions, scan.sessions)
  if (hasProjectFilter) {
    data.inputTokens = scan.inputTokens
    data.outputTokens = scan.outputTokens
    data.cacheReadTokens = scan.cacheReadTokens
    data.cacheWriteTokens = scan.cacheWriteTokens
  }

  return {
    cost: data.cost,
    savingsUSD: data.savingsUSD,
    calls: data.calls,
    sessions: data.sessions,
    inputTokens: data.inputTokens,
    outputTokens: data.outputTokens,
    cacheReadTokens: data.cacheReadTokens,
    cacheWriteTokens: data.cacheWriteTokens,
    carriedCostUSD: days.reduce((sum, day) => sum + (day.carried ? day.cost : 0), 0),
  }
}

/// The single durable-totals builder every CLI/TUI surface and the menubar share.
/// Headline totals (cost/calls/sessions/tokens/models/categories/savings) come
/// from the carry-forward daily cache unioned with today's live parse and sliced
/// to the requested provider, so a period that includes days whose session files
/// have expired still counts them — the invariant the menubar already relies on.
/// Detail-only fields that day entries can't carry (estimatedCost, unpriced
/// models, workflow intelligence, per-session drill-down) are enriched from a
/// fresh parse of the surviving sessions.
export type DurablePeriod = {
  /// Durable headline totals for the period.
  data: PeriodData
  /// The exact provider-sliced, day-filtered day set behind `data`. Daily rows
  /// rendered by report/overview come from here so they reconcile to `data`.
  days: DailyEntry[]
  /// Sum of `cost` on `carried` days included in the period (footnote source).
  carriedCostUSD: number
  /// Cost the active --project/--exclude filter had to set aside: cached days
  /// recorded before per-project day stats existed (v15) carry no project split,
  /// so they cannot be attributed to the filtered projects. Always 0 when no
  /// project filter is active. Reported so a filtered total that is short of the
  /// unfiltered one says so instead of just looking wrong.
  unattributedCostUSD: number
  /// Fresh per-period parse (provider + name filtered) for detail views that
  /// still need surviving session files.
  liveProjects: ProjectSummary[]
  /// Hydrated all-provider cache (reused by the menubar's provider list + daily
  /// history sections).
  cache: DailyCache
  /// Today-only slice, all providers, name-filtered (memo seed for the menubar).
  todayAllDays: DailyEntry[]
  /// The scan range the live parse covered (today-only when the period is today).
  scanRange: DateRange
}

export async function buildDurablePeriod(periodInfo: PeriodInfo, opts: AggregateOpts = {}): Promise<DurablePeriod> {
  const pf = opts.provider ?? 'all'
  const daysSelection = opts.daysSelection ?? null
  const fp = (p: ProjectSummary[]) => filterProjectsByName(p, opts.project ?? [], opts.exclude ?? [])

  const now = new Date()
  const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  const todayRange: DateRange = { start: todayStart, end: now }
  const todayStr = toDateString(todayStart)
  const rangeStartStr = toDateString(periodInfo.range.start)
  const rangeEndStr = toDateString(periodInfo.range.end)
  const isTodayOnly = rangeStartStr === todayStr && rangeEndStr === todayStr

  const cache = await hydrateCache()

  // Today's live data always comes from an all-provider parse so the union (and
  // any per-provider slice of it) sees every provider's today. `todayAllDays` is
  // the today bucket only — the union filters the historical remainder out of the
  // cache.
  let liveProjects: ProjectSummary[]
  let todayAllDays: DailyEntry[]
  let scanRange: DateRange
  if (pf === 'all') {
    if (isTodayOnly) {
      const raw = fp(await parseAllSessions(todayRange, 'all'))
      liveProjects = raw
      scanRange = todayRange
      todayAllDays = aggregateProjectsIntoDays(raw).filter(d => d.date === todayStr)
    } else {
      const raw = fp(await parseAllSessions(periodInfo.range, 'all'))
      liveProjects = daysSelection ? filterProjectsByDays(raw, daysSelection.days) : raw
      scanRange = periodInfo.range
      // A period that reaches today contains today's turns already, so derive the
      // today slice from the same parse instead of scanning today again. Slice it
      // to today first (filterProjectsByDays re-anchors a midnight-straddling
      // turn to its surviving today calls), so today's category / turn count
      // lands on today rather than staying anchored on the turn's yesterday
      // start. Otherwise the post-midnight half vanishes from By Activity and the
      // JSON daily turn count while the per-call cost/calls still bucket to today.
      todayAllDays = rangeEndStr >= todayStr
        ? aggregateProjectsIntoDays(filterProjectsByDays(raw, new Set([todayStr]))).filter(d => d.date === todayStr)
        : aggregateProjectsIntoDays(fp(await parseAllSessions(todayRange, 'all'))).filter(d => d.date === todayStr)
    }
  } else {
    // Provider-filtered: today's all-provider parse feeds the union (sliced
    // below); the provider-scoped parse feeds the detail/enrichment fields.
    todayAllDays = aggregateProjectsIntoDays(fp(await parseAllSessions(todayRange, 'all'))).filter(d => d.date === todayStr)
    const rawProv = fp(await parseAllSessions(isTodayOnly ? todayRange : periodInfo.range, pf))
    liveProjects = daysSelection && !isTodayOnly ? filterProjectsByDays(rawProv, daysSelection.days) : rawProv
    scanRange = isTodayOnly ? todayRange : periodInfo.range
  }

  // Name filters must reach the cache-sourced days too. Today's parse is already
  // name-filtered above (`fp`), but the historical remainder comes straight out
  // of the day cache, so without this slice a --project/--exclude headline
  // counted every expired-source day whole while the detail panels did not.
  const projectInclude = (opts.project ?? []).map(s => s.toLowerCase())
  const projectExclude = (opts.exclude ?? []).map(s => s.toLowerCase())
  const hasProjectFilter = projectInclude.length > 0 || projectExclude.length > 0
  // What a filtered total cannot claim, and therefore has to leave out: a cached
  // day with no project split at all, or — with a provider filter also active,
  // since the headline then reads that provider's slice — a slice carried from a
  // cache generation that predates per-project splits. Both are stated back to
  // the caller (footnoted by the overview) instead of vanishing from the total.
  const unattributableCost = (day: DailyEntry): number => {
    if (pf === 'all') return day.projects ? 0 : day.cost
    const slice = Object.hasOwn(day.providers, pf) ? day.providers[pf] : undefined
    if (!slice) return 0
    return !day.projects || !slice.projects ? slice.cost : 0
  }
  let unattributedCostUSD = 0
  const sliceHistorical = hasProjectFilter
    ? (day: DailyEntry): DailyEntry => {
        unattributedCostUSD += unattributableCost(day)
        return sliceDayToProject(day, projectInclude, projectExclude)
      }
    : undefined

  const allDays = unionDaysForPeriod(cache, todayAllDays, periodInfo, daysSelection?.days ?? null, sliceHistorical)
  const days = pf === 'all' ? allDays : allDays.map(d => sliceDayToProvider(d, pf))
  const data = buildPeriodDataFromDays(days, periodInfo.label)

  // Enrich the cache-authoritative headline with fields DailyEntry cannot carry.
  // These are all derivable only from surviving sessions (estimated-cost markers,
  // unpriced-model detection, per-turn workflow intelligence), so they describe
  // the live population, a subset of the carried headline.
  const scanData = buildPeriodData(periodInfo.label, liveProjects)
  data.estimatedCostUSD = scanData.estimatedCostUSD
  data.unpricedModels = scanData.unpricedModels
  data.workflow = scanData.workflow
  data.topReworkedFiles = scanData.topReworkedFiles
  data.pricingCoverage = scanData.pricingCoverage
  // Cache buckets a session on its START day, the scan on any ACTIVE day; both
  // are lower bounds of distinct sessions, so max is the tightest safe bound.
  data.sessions = Math.max(data.sessions, scanData.sessions)
  // Tokens/models/categories have no per-project split in the day cache, so
  // sliceDayToProject drops them (see there). Under a project filter they come
  // from the live parse instead: exact for the filtered projects, bounded by
  // source retention like every other scan-derived field above, and consistent
  // with the By Model / By Activity panels that read the same parse. Cost, calls,
  // sessions and savings stay durable — sliced out of the cache, expired days
  // included.
  if (hasProjectFilter) {
    data.inputTokens = scanData.inputTokens
    data.outputTokens = scanData.outputTokens
    data.cacheReadTokens = scanData.cacheReadTokens
    data.cacheWriteTokens = scanData.cacheWriteTokens
    data.models = scanData.models
    data.categories = scanData.categories
  }
  const estimatedByModel = new Map(
    scanData.models.filter(m => m.estimatedCostUSD != null).map(m => [m.name, m.estimatedCostUSD!]),
  )
  if (estimatedByModel.size > 0) {
    data.models = data.models.map(m =>
      estimatedByModel.has(m.name) ? { ...m, estimatedCostUSD: estimatedByModel.get(m.name) } : m,
    )
  }

  const carriedCostUSD = days.reduce((s, d) => s + (d.carried ? d.cost : 0), 0)
  return { data, days, carriedCostUSD, unattributedCostUSD, liveProjects, cache, todayAllDays, scanRange }
}
