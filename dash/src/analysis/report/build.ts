// `buildContextReport` — the one derivation every surface reads (Decision D2, R7.5).
//
// The CLI report, `/api/kyber/report` and the tray popover all render what this returns.
// Nothing downstream recomputes a figure: R11.14 requires the report and the REST API to
// agree, and they agree because there is only one of them.
//
// Two rules run through the whole file. A figure that cannot be measured is `Measured`
// with `value: null` and a reason, never `0` (R14.1) — every helper here returns
// `unmeasurable(...)` rather than falling back to a zero that would read as a
// measurement. And cost is assembled last and kept per basis (R8.9, R14.2): bases are not
// blended, and no section above cost may order itself by it.

import type { CanonicalRecord } from '../../canon/types.js'
import type { KyberBridge, SessionSummary } from '../../server/bridge.js'
import type { Finding } from '../findings.js'
import { buildScorecard } from '../scorecard.js'
import {
  DEFAULT_FINDING_LIMIT,
  DEFAULT_SECTIONS,
  REPORT_SCHEMA_VERSION,
  measured,
  unmeasurable,
  type BucketKey,
  type ContextReport,
  type ReportCost,
  type ReportCoverage,
  type ReportFinding,
  type ReportHarness,
  type ReportLatestSession,
  type ReportScope,
  type ReportSection,
} from './types.js'

/** The five canonical buckets, in the order every surface renders them (R8.2). */
export const BUCKET_KEYS: readonly BucketKey[] = [
  'system_prompt',
  'tool_definitions',
  'instruction_context',
  'conversation_history',
  'tool_result_content',
]

/**
 * Where detection comes from. Injected rather than imported so the report can be built
 * without touching the filesystem: detection walks provider directories, which the tray
 * must never trigger and a seeded-store test must never depend on (design C1).
 */
export type DetectionSource = () => Array<{
  harness: string
  detected: boolean
  probedPaths: string[]
}>

export type BuildOptions = {
  sections?: readonly ReportSection[]
  findingLimit?: number
  /** Injected so a seeded-store test can pin a window without freezing the clock. */
  now?: Date
  detection?: DetectionSource
  kyberdashVersion?: string
  storePath?: string
}

/** A session's end, or its start when it never ended — what "in the window" means. */
function sessionAt(session: SessionSummary): number {
  const stamp = session.ended ?? session.started
  const parsed = stamp === null ? Number.NaN : Date.parse(stamp)
  return Number.isNaN(parsed) ? Number.NEGATIVE_INFINITY : parsed
}

/**
 * Sessions the scope selects: inside `[now − days, now]`, then narrowed by harness,
 * session or run. A session with no usable timestamp is out — including it would put a
 * row of unknown age in a window the reader believes is bounded.
 */
export function sessionsInScope(
  sessions: readonly SessionSummary[],
  scope: ReportScope,
  now: Date,
): SessionSummary[] {
  const floor = now.getTime() - scope.days * 24 * 60 * 60 * 1000
  const ceiling = now.getTime()
  return sessions.filter((session) => {
    const at = sessionAt(session)
    if (!Number.isFinite(at) || at < floor || at > ceiling) return false
    if (scope.harness !== undefined && session.harness !== scope.harness) return false
    if (scope.sessionId !== undefined && session.session_id !== scope.sessionId) return false
    return true
  })
}

/** Carry a finding through unchanged. Every Contract field is stored, not recomputed (R14.4). */
function toReportFinding(finding: Finding): ReportFinding {
  const waste = finding.estimatedWasteTokens
  return {
    id: finding.id,
    title: finding.title,
    measurementClass: finding.measurementClass ?? 'inferred',
    confidence: String(finding.confidence),
    confidenceBasis: finding.confidenceBasis ?? null,
    mechanism: finding.mechanism,
    evidenceIds: finding.evidenceLinks.map((link) => link.spanId),
    // Verbatim: a recommendation that relocates context must never be re-worded into a
    // deletion, which is what paraphrasing it here would eventually do (R14.4).
    recommendation: finding.recommendation,
    recoverableTokens:
      typeof waste === 'number' && Number.isFinite(waste)
        ? measured(waste, 'tokens')
        : unmeasurable<number>('detector reported no recoverable-token estimate'),
    errorBar:
      finding.errorBar === undefined
        ? null
        : { low: finding.errorBar.lower, high: finding.errorBar.upper },
    outcomeRiskCaveat: finding.outcomeRiskCaveat,
    rankScore: finding.rankScore ?? 0,
    sessionId: finding.sessionId ?? null,
    runId: finding.runId ?? null,
    harness: null,
    view: `finding/${finding.id}`,
  }
}

/**
 * Findings for the scope, ranked. Ties break on id so two runs over one store produce the
 * same order — a report that reshuffles equal-ranked findings between runs cannot be
 * diffed, which is most of what a coding agent would use it for.
 */
export function rankFindings(findings: readonly Finding[], limit: number): ReportFinding[] {
  return [...findings]
    .sort((a, b) => (b.rankScore ?? 0) - (a.rankScore ?? 0) || a.id.localeCompare(b.id))
    .slice(0, limit)
    .map(toReportFinding)
}

/** The scope's latest session: the greatest end time, which is what "current work" means. */
export function latestOf(sessions: readonly SessionSummary[]): SessionSummary | null {
  let best: SessionSummary | null = null
  for (const session of sessions) {
    if (best === null || sessionAt(session) > sessionAt(best)) best = session
  }
  return best
}

/** A priced record, reduced to what the cost section needs. */
export type CostContribution = { sessionId: string | null; basis: string; status: string; value?: number }

/**
 * Cost per basis, summed without blending (R8.9, R14.2, and R5.1 of the archived
 * KyberDash requirements).
 *
 * A harness-reported figure and a table-derived one answer different questions — one is
 * what the vendor billed, the other what our rate table says those tokens cost — so they
 * stay in separate rows even though both are dollars. Summing them would produce a number
 * that is neither.
 *
 * Only `priced` records contribute. `no_rate` and `not_billed` are different answers and
 * neither is a zero (R5.4, R5.5): a basis whose records are all unpriced reports its
 * reason rather than a total of 0.
 */
export function costByBasis(contributions: readonly CostContribution[]): ReportCost {
  const priced = new Map<string, number>()
  const unpriced = new Map<string, Set<string>>()
  for (const row of contributions) {
    if (row.status === 'priced' && typeof row.value === 'number' && Number.isFinite(row.value)) {
      priced.set(row.basis, (priced.get(row.basis) ?? 0) + row.value)
    } else {
      const reasons = unpriced.get(row.basis) ?? new Set<string>()
      reasons.add(row.status)
      unpriced.set(row.basis, reasons)
    }
  }

  const rows: ReportCost = []
  for (const [basis, amount] of [...priced.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    rows.push({ basis, amountUsd: measured(amount, 'USD') })
  }
  for (const [basis, reasons] of [...unpriced.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    if (priced.has(basis)) continue
    rows.push({
      basis,
      amountUsd: unmeasurable<number>(`no priced record on this basis (${[...reasons].sort().join(', ')})`),
    })
  }
  if (rows.length === 0) {
    rows.push({
      basis: 'unknown',
      amountUsd: unmeasurable<number>('no session in scope carried a cost'),
    })
  }
  return rows
}

/**
 * Cost contributions for the scoped sessions.
 *
 * Read from the canonical records, because that is where the basis lives; the session
 * summary carries one blended figure with no basis at all, and labelling that as a basis
 * would be the blending this section exists to prevent. Without a store to read, the
 * section reports that it could not be computed rather than falling back to the blend.
 */
function readCostContributions(bridge: KyberBridge, sessionIds: ReadonlySet<string>): CostContribution[] {
  const store = (bridge as unknown as { store?: { listAll?: () => CanonicalRecord[] } }).store
  if (store?.listAll === undefined) return []
  return safely(() => store.listAll!(), [])
    .filter((record) => record.sessionId != null && sessionIds.has(record.sessionId))
    .map((record) => ({
      sessionId: record.sessionId ?? null,
      basis: String(record.cost?.basis ?? 'unknown'),
      status: String(record.cost?.status ?? 'no_rate'),
      ...(typeof record.cost?.value === 'number' ? { value: record.cost.value } : {}),
    }))
}

function buildCoverage(
  bridge: KyberBridge,
  scoped: readonly SessionSummary[],
  storePath: string,
  now: Date,
): ReportCoverage {
  const refresh = readRefreshState(bridge)
  const perHarness = new Map<string, number>()
  for (const session of scoped) {
    perHarness.set(session.harness, (perHarness.get(session.harness) ?? 0) + 1)
  }

  const rollups = safely(() => bridge.listHarnessRollups(), [])
  const harnesses = rollups.map((row) => ({
    harness: row.harness,
    name: row.harness,
    sessionsInWindow: perHarness.get(row.harness) ?? 0,
    measurability: Object.fromEntries(
      Object.entries(row.measurability ?? {}).map(([metric, availability]) => [
        metric,
        typeof availability === 'object' && availability !== null && 'reason' in availability
          ? { reason: String((availability as { reason: unknown }).reason) }
          : ('measurable' as const),
      ]),
    ),
  }))

  const hints: string[] = []
  if (scoped.length === 0) {
    hints.push('No session in the window. Run `kyberdash dash refresh` to read local harness history.')
  }
  // An hour is the point past which a reader should be told the figures are old rather
  // than left to compare timestamps themselves (R11.4).
  const staleAfterMs = 60 * 60 * 1000
  if (refresh.lastSuccessAt === null) {
    hints.push('No successful refresh recorded. Run `kyberdash dash refresh`.')
  } else if (now.getTime() - Date.parse(refresh.lastSuccessAt) > staleAfterMs) {
    hints.push(`Last successful refresh was ${refresh.lastSuccessAt}. Run \`kyberdash dash refresh\`.`)
  }

  return {
    storePath,
    refresh,
    harnesses,
    quarantineCount: safely(() => bridge.getQuarantine().length, 0),
    problemCount: safely(() => bridge.getProblems().length, 0),
    hints,
  }
}

/**
 * Refresh state from the run log (R10.5). The last success and the last failure are read
 * independently on purpose: a failure must not hide the success before it, because "it
 * last worked on Tuesday" is what tells a reader how stale the data is.
 */
function readRefreshState(bridge: KyberBridge): ReportCoverage['refresh'] {
  const store = (bridge as unknown as { store?: RefreshRunReader }).store
  if (store?.latestRefreshRun === undefined) {
    return { lastSuccessAt: null, lastFailure: null, inProgress: null }
  }
  const success = safely(() => store.latestRefreshRun('success'), undefined)
  const failure = safely(() => store.latestRefreshRun('failure'), undefined)
  const running = safely(() => store.latestRefreshRun('running'), undefined)
  return {
    lastSuccessAt: success?.completedAt ?? success?.startedAt ?? null,
    lastFailure:
      failure === undefined
        ? null
        : { at: failure.completedAt ?? failure.startedAt, summary: failure.summary ?? 'refresh failed' },
    inProgress: running === undefined ? null : { pid: running.pid, since: running.startedAt },
  }
}

type RefreshRunReader = {
  latestRefreshRun(status: 'success' | 'failure' | 'running'):
    | { startedAt: string; completedAt: string | null; pid: number; summary: string | null }
    | undefined
}

function buildHarnesses(bridge: KyberBridge, scope: ReportScope): ReportHarness[] {
  return safely(() => bridge.listHarnessRollups(), [])
    .filter((row) => scope.harness === undefined || row.harness === scope.harness)
    .map((row) => ({ harness: row.harness, name: row.harness, dimensions: buildScorecard(row) }))
}

/**
 * The latest session's current context state (R8.2–R8.4, R11.7).
 *
 * Every figure here is `Measured`. A harness that does not export message structure
 * cannot have its buckets computed, and the honest answer is the reason — a zeroed bucket
 * row would claim the context is empty, which is the opposite of what is known.
 */
function buildLatestSession(
  bridge: KyberBridge,
  session: SessionSummary,
): ReportLatestSession {
  const analysis = safely(() => bridge.getSessionContent(session.session_id), undefined) as
    | { context?: unknown }
    | undefined
  const context = extractContext(analysis)

  const unmeasured = (reason: string) => ({
    index: 0,
    pressure: unmeasurable<number>(reason),
    contextWindow: unmeasurable<number>(reason),
    buckets: Object.fromEntries(
      BUCKET_KEYS.map((key) => [key, unmeasurable<number>(reason)]),
    ) as Record<BucketKey, ReturnType<typeof unmeasurable<number>>>,
    residual: unmeasurable<number>(reason),
    cacheInvalidation: false,
  })

  const latestTurn =
    context === null
      ? unmeasured('harness exported no message structure for this session')
      : {
          index: context.turn.index,
          pressure: measured(context.turn.pressure),
          contextWindow: measured(context.contextLimit, 'tokens'),
          buckets: Object.fromEntries(
            BUCKET_KEYS.map((key) => [key, measured(context.turn.buckets[key] ?? 0, 'tokens')]),
          ) as Record<BucketKey, ReturnType<typeof measured<number>>>,
          residual: measured(context.turn.residual.tokens, 'tokens'),
          cacheInvalidation: context.flagged.includes(context.turn.index),
        }

  return {
    sessionId: session.session_id,
    harness: session.harness,
    project: session.repo ?? null,
    lastActivityAt: session.ended ?? session.started ?? '',
    turnCount: session.turn_count ?? 0,
    view: `session/${session.session_id}`,
    latestTurn,
    toolDefinitionSources:
      context === null
        ? []
        : [...context.turn.toolDefinitionsByServer.entries()]
            .sort(([, a], [, b]) => b - a)
            .map(([source, tokens]) => ({ source, tokens: measured(tokens, 'tokens') })),
    cacheInvalidationTurns: context?.flagged ?? [],
  }
}

type LatestContext = {
  contextLimit: number
  turn: {
    index: number
    pressure: number
    buckets: Record<string, number>
    residual: { tokens: number }
    toolDefinitionsByServer: Map<string, number>
  }
  flagged: number[]
}

/** Pull the latest turn out of a persisted context analysis, or `null` when unmeasurable. */
function extractContext(payload: { context?: unknown } | undefined): LatestContext | null {
  const context = payload?.context as
    | {
        measurable?: boolean
        contextLimit?: number
        turns?: LatestContext['turn'][]
        flaggedTurns?: number[]
      }
    | undefined
  if (context?.measurable !== true) return null
  const turns = context.turns ?? []
  const turn = turns[turns.length - 1]
  if (turn === undefined || typeof context.contextLimit !== 'number') return null
  return {
    contextLimit: context.contextLimit,
    turn: {
      ...turn,
      toolDefinitionsByServer:
        turn.toolDefinitionsByServer instanceof Map
          ? turn.toolDefinitionsByServer
          : new Map(Object.entries((turn.toolDefinitionsByServer ?? {}) as Record<string, number>)),
    },
    flagged: context.flaggedTurns ?? [],
  }
}

/**
 * A bridge call that fails must not take the whole report with it: a report missing one
 * section is still actionable, and a crashed one tells the reader nothing at all.
 */
function safely<T>(read: () => T, fallback: T): T {
  try {
    return read()
  } catch {
    return fallback
  }
}

/**
 * Build the report for a scope.
 *
 * Sections are opt-in so the tray can skip `detection`, which walks the filesystem.
 * `cost` is assembled last and appears last in the document — the order is the contract
 * that keeps a spend figure from leading any surface (R14.2).
 */
export function buildContextReport(
  bridge: KyberBridge,
  scope: ReportScope,
  options: BuildOptions = {},
): ContextReport {
  const now = options.now ?? new Date()
  const sections = new Set(options.sections ?? DEFAULT_SECTIONS)
  const findingLimit = options.findingLimit ?? DEFAULT_FINDING_LIMIT

  const allSessions = safely(() => bridge.listSessions(), [])
  let scoped = sessionsInScope(allSessions, scope, now)
  if (scope.runId !== undefined) {
    const runSessionIds = new Set(
      safely(() => bridge.listFindings({ runId: scope.runId }), [])
        .map((finding) => finding.sessionId)
        .filter((id): id is string => typeof id === 'string'),
    )
    scoped = scoped.filter((session) => runSessionIds.has(session.session_id))
  }

  const report: ContextReport = {
    schemaVersion: REPORT_SCHEMA_VERSION,
    generatedAt: now.toISOString(),
    kyberdashVersion: options.kyberdashVersion ?? '0.0.0',
    scope,
  }

  if (sections.has('coverage')) {
    report.coverage = buildCoverage(bridge, scoped, options.storePath ?? 'canon.db', now)
  }

  if (sections.has('detection')) {
    report.detection = options.detection === undefined ? [] : safely(() => options.detection!(), [])
  }

  if (sections.has('findings')) {
    const scopedIds = new Set(scoped.map((session) => session.session_id))
    const findings = safely(
      () =>
        bridge.listFindings(
          scope.runId === undefined ? {} : { runId: scope.runId },
        ),
      [],
    ).filter(
      (finding) =>
        finding.sessionId === undefined ||
        finding.sessionId === null ||
        scopedIds.has(finding.sessionId),
    )
    report.findings = rankFindings(findings, findingLimit)
  }

  if (sections.has('harnesses')) {
    report.harnesses = buildHarnesses(bridge, scope)
  }

  if (sections.has('latestSession')) {
    const latest = latestOf(scoped)
    report.latestSession = latest === null ? null : buildLatestSession(bridge, latest)
  }

  // Last, always (R14.2).
  if (sections.has('cost')) {
    report.cost = costByBasis(
      readCostContributions(bridge, new Set(scoped.map((session) => session.session_id))),
    )
  }

  return report
}
