/**
 * codeburn sync — push orchestration.
 *
 * Extracted from the CLI action so the flatten/filter/batch/send/ledger
 * pipeline is unit-testable without a full CLI invocation.
 */

import type { ProjectSummary } from '../types.js'
import type { PlanMap, PlanProvider } from '../config.js'
import { isProxiedPath } from '../models.js'
import { inferSessionProvider } from '../session-output.js'
import { resolveWorkUnits, workUnitSessionKey } from '../work-units.js'
import { assertHttps } from './discovery.js'
import { ledgerKeySet, appendToLedger, type LedgerEntry } from './ledger.js'
import {
  buildOtlpPayload,
  batchCalls,
  buildAttributionOtlpPayload,
  flattenAttributionRecords,
  deriveTraceId,
  type CallWithSession,
  type SessionWireContext,
  type AttributionItem,
  type OtlpPayload,
} from './otlp.js'
import type { SessionAttributionRecord } from '../yield.js'

/**
 * Safety valve, not a routine cap — pushes now loop until all batches are
 * sent (429s are waited out). This only bounds a single push in pathological
 * cases (e.g. corrupted ledger causing a full re-send of years of data).
 */
export const MAX_PER_PUSH = 50_000

/**
 * How long a copilot session must be quiet before its calls may be synced.
 *
 * The ledger is append-once and the OTLP span id derives from the same
 * deduplication key, so the whole pipeline assumes a served call is immutable:
 * same key, same value, forever. Copilot's serve-time reconciliation is the
 * first producer that breaks that (#988). Three ways, all within one session:
 * a shutdown residual SHRINKS as the store rows that cover it land; a rollup
 * is DROPPED once rows cover its leg; and an unpaired store row becomes
 * supplementary once its journal call appears. Sent once at an intermediate
 * state, the receiver keeps that state forever AND receives what supersedes
 * it — a permanent over-count that no later push can correct. Local reports
 * re-reconcile on every pass and are unaffected; this is only about what
 * leaves the machine.
 *
 * Every input to that reconciliation is written DURING the session: rows as
 * each request completes, the rollup at shutdown. So a session with no
 * activity for a day cannot reconcile any further, and its first send is also
 * its last word. A day is far longer than any session and absorbs clock skew,
 * at the cost of copilot usage reaching a receiver up to a day late.
 *
 * This is a holdback, never a drop: the calls are simply not yet unsent-
 * eligible, and the next push after the window picks them up.
 *
 * A day is deliberately conservative. The one measurement available says it
 * could be far shorter: across 91 sessions on a real macOS store, ZERO rows
 * landed after their session's shutdown - median delta -0.1s, maximum -0.0s,
 * i.e. the store was already complete when shutdown was written. That is one
 * machine and one CLI version (1.0.80), so this stays at a day until a second
 * machine agrees; the number to beat is seconds, not hours.
 *
 * Two more machines have now agreed (#946 validation): 6 sessions on CLI
 * 1.0.79-8 and 30 sessions on 1.0.80, closest row 55 ms BEFORE shutdown, p95
 * -0.067 s. Three corpora, 127 sessions, zero positive deltas. That is enough
 * evidence to tighten this to a seconds-scale window - deliberately NOT done
 * here: shortening it changes what leaves the machine and how promptly, which
 * is a product call for the maintainers, not a fix to a validated defect. The
 * evidence is recorded so whoever makes that call does not have to re-gather
 * it.
 */
export const RECONCILE_SETTLE_MS = 24 * 60 * 60 * 1000

/** Providers whose SERVED calls can change value or role between passes. */
const RECONCILING_PROVIDERS = new Set(['copilot'])

/**
 * A copilot session's input/cache can leave this machine in one of two shapes,
 * and the receiver must never end up holding both.
 *
 *   rollup      `copilot:<sid>:shutdown:<model>:<n>`
 *               one aggregate span per leg — what every pre-store version sent,
 *               and what still serves for a session with no store rows.
 *   reconciled  `copilot-store:<sid>:<rowId>:<hash>`  (one span per request)
 *             + `copilot:<sid>:shutdown-residual:...` (the part rows don't cover)
 *               the two together are exactly the rollup, re-expressed.
 *
 * They describe the SAME tokens. Locally, reconciliation picks one per
 * (session, model) on every pass and the answer can flip. Remotely there is no
 * flipping: the ledger is append-once and a usage span has no retraction, so
 * whatever was sent first is there forever and anything sent after it ADDS.
 *
 * Both directions are reachable:
 *   - Rollup first: every session synced before this release went out that
 *     way. The first push after upgrade would send rows and residuals on top.
 *   - Reconciled first: rows sync, then at the 90-day durable age-out the
 *     cached rows are pruned, the rollup stops being dropped and serves again
 *     under a key that was never sent. Past settle, it pushes on top.
 *
 * So: whichever shape a session was first synced in, it stays in, and the
 * other is frozen for that session permanently. Growth WITHIN the sent shape
 * is unaffected — a resumed session's new rows and residuals still push if the
 * reconciled shape was sent, a new leg's rollup still pushes if the rollup
 * shape was — because same-shape output is additive, never substitutive.
 *
 * `codeburn sync reset --confirm` clears the local ledger and re-pushes
 * everything under the new breakdown, but only helps if the receiver's own
 * copy is cleared too — otherwise it produces exactly the doubling this
 * avoids.
 */
type CopilotShape = 'rollup' | 'reconciled'

/** Which shape a key belongs to; null for anything reconciliation never rewrites. */
function keyShape(key: string): CopilotShape | null {
  if (key.startsWith('copilot-store:')) return 'reconciled'
  if (!key.startsWith('copilot:')) return null
  // Order matters: `:shutdown-residual:` is reconciled output, `:shutdown:` is
  // the raw rollup it replaces. Everything else under `copilot:` is a per-turn
  // call, which carries output the rollup never held and reconciliation never
  // touches.
  if (key.includes(':shutdown-residual:')) return 'reconciled'
  if (key.includes(':shutdown:')) return 'rollup'
  return null
}

/** Session id out of a copilot key, or null when the key has no session segment. */
function keySessionId(key: string): string | null {
  for (const prefix of ['copilot-store:', 'copilot:']) {
    if (!key.startsWith(prefix)) continue
    const rest = key.slice(prefix.length)
    const end = rest.indexOf(':')
    return end > 0 ? rest.slice(0, end) : null
  }
  return null
}

function syncedShapes(sentKeys: ReadonlySet<string>): Map<string, Set<CopilotShape>> {
  const bySession = new Map<string, Set<CopilotShape>>()
  for (const key of sentKeys) {
    const shape = keyShape(key)
    if (!shape) continue
    const sid = keySessionId(key)
    if (!sid) continue
    const set = bySession.get(sid) ?? new Set<CopilotShape>()
    set.add(shape)
    bySession.set(sid, set)
  }
  return bySession
}

/**
 * How far ahead of now a timestamp may sit and still count as evidence of when
 * a session was active. Clock skew is absorbed in both directions; beyond it,
 * a stamp is broken data rather than proof the session is live.
 */
const FUTURE_GRACE_MS = 60 * 60 * 1000

/**
 * Newest moment a session can be SHOWN to have been active, or null when
 * nothing about it can be dated. Unparseable and implausibly-future stamps
 * carry no information about recency and are ignored rather than treated as
 * "recent" — otherwise one broken row would hold a year-old session forever,
 * while the CLI promised a settlement that could never arrive.
 */
function newestDatableTs(timestamps: readonly string[], now: number): number | null {
  let newest: number | null = null
  for (const raw of timestamps) {
    const ts = Date.parse(raw)
    if (isNaN(ts) || ts > now + FUTURE_GRACE_MS) continue
    if (newest === null || ts > newest) newest = ts
  }
  return newest
}

export interface CollectOptions {
  /**
   * Configured plans (readPlans()), for the ai.subscription_covered decision.
   * Treated as "no plans configured" when omitted.
   */
  plans?: PlanMap
}

/**
 * True when a configured plan covers this provider. A plan with id 'none' is
 * the recorded absence of a plan, not coverage.
 */
function planCoversProvider(plans: PlanMap, provider: string): boolean {
  return [plans[provider as PlanProvider], plans.all]
    .some(plan => plan !== undefined && plan.id !== 'none')
}

/**
 * The ai.subscription_covered decision: true when a configured plan covers the
 * call's provider or the session's provider-recorded cwd sits under a
 * configured proxy path; false when the machinery can rule both out; undefined
 * (omit the attribute) when there is no plan match and no cwd to proxy-check.
 */
function subscriptionCoveredFor(plans: PlanMap, provider: string, workingDirectory: string | undefined): boolean | undefined {
  if (planCoversProvider(plans, provider)) return true
  if (workingDirectory !== undefined) return isProxiedPath(workingDirectory)
  return undefined
}

/**
 * Lineage context for one session, from the #1140 lineage field plus the
 * #1145 resolver derivation. Only provider-recorded lineage emits anything:
 * a recorded root derives its own work-unit id, a recorded child takes the id
 * of the unit the resolver folded it into, and anything the resolver fails
 * closed on (parent out of range, cycle, ambiguous id) emits nothing.
 */
function lineageContext(
  session: ProjectSummary['sessions'][number],
  provider: string,
  resolution: ReturnType<typeof resolveWorkUnits>,
  unitById: ReadonlyMap<string, ReturnType<typeof resolveWorkUnits>['units'][number]>,
): Pick<SessionWireContext, 'workUnitId' | 'sessionRole'> {
  const lineage = session.lineage
  if (lineage?.evidence !== 'provider-recorded') return {}
  if (lineage.role === 'root') {
    return { workUnitId: deriveTraceId(session.sessionId), sessionRole: 'root' }
  }
  if (lineage.role === 'child' && lineage.parentSessionId) {
    const workUnitId = resolution.bySession.get(workUnitSessionKey(provider, session.sessionId))
    const unit = workUnitId ? unitById.get(workUnitId) : undefined
    if (workUnitId && unit?.roles[session.sessionId] === 'child') {
      return { workUnitId, sessionRole: 'child' }
    }
  }
  return {}
}

/** Flatten parsed projects into individual calls and filter out already-sent ones. */
export function collectUnsentCalls(projects: ProjectSummary[], now: number = Date.now(), opts?: CollectOptions): {
  allCalls: CallWithSession[]
  unsent: CallWithSession[]
  /** Not yet sent because their session is still reconciling. Retried later. */
  held: CallWithSession[]
  /** Never sent: the receiver already holds this session in the other shape. */
  frozen: CallWithSession[]
} {
  const plans = opts?.plans ?? {}

  // Lineage resolves over every session in the window, never just the unsent
  // slice: a child whose root was already synced still names the same unit.
  const resolution = resolveWorkUnits(projects.flatMap(project =>
    project.sessions.map(session => ({
      sessionId: session.sessionId,
      provider: inferSessionProvider(session),
      lineage: session.lineage,
    })),
  ))
  const unitById = new Map(resolution.units.map(unit => [unit.workUnitId, unit]))

  const allCalls: CallWithSession[] = []
  for (const project of projects) {
    for (const session of project.sessions) {
      const provider = inferSessionProvider(session)
      let callCount = 0
      for (const turn of session.turns) callCount += turn.assistantCalls.length
      const first = Date.parse(session.firstTimestamp)
      const last = Date.parse(session.lastTimestamp)
      const durationMs = !isNaN(first) && !isNaN(last) && last >= first ? last - first : undefined
      const sessionContext: SessionWireContext = {
        callCount,
        ...(durationMs !== undefined ? { durationMs } : {}),
        ...lineageContext(session, provider, resolution, unitById),
      }
      for (const turn of session.turns) {
        for (const call of turn.assistantCalls) {
          const covered = subscriptionCoveredFor(plans, call.provider, session.workingDirectory)
          allCalls.push({
            call,
            sessionId: session.sessionId,
            project: project.project,
            workingDirectory: session.workingDirectory,
            session: covered !== undefined ? { ...sessionContext, subscriptionCovered: covered } : sessionContext,
          })
        }
      }
    }
  }

  // A session settles as a whole: holding only the residual would still let a
  // row that is about to change ITS value through, and holding only the rows
  // would still ship a rollup that is about to be dropped. So the decision is
  // taken once per session, from the newest moment it can be SHOWN active.
  const settleCutoff = now - RECONCILE_SETTLE_MS
  const stampsBySession = new Map<string, string[]>()
  for (const c of allCalls) {
    if (!RECONCILING_PROVIDERS.has(c.call.provider)) continue
    const key = `${c.project}\u0000${c.sessionId}`
    const list = stampsBySession.get(key) ?? []
    list.push(c.call.timestamp)
    stampsBySession.set(key, list)
  }
  const unsettled = new Set<string>()
  for (const [key, stamps] of stampsBySession) {
    const newest = newestDatableTs(stamps, now)
    if (newest !== null && newest > settleCutoff) unsettled.add(key)
  }

  const sent = ledgerKeySet()
  const shapes = syncedShapes(sent)

  // Only the reconciliation OUTPUT is frozen, and only into the shape the
  // session was NOT first synced in. Per-turn calls (`copilot:<sid>:<msgId>`)
  // belong to neither shape and always sync.
  const isFrozen = (c: CallWithSession): boolean => {
    if (!RECONCILING_PROVIDERS.has(c.call.provider)) return false
    const shape = keyShape(c.call.deduplicationKey)
    if (!shape) return false
    const already = shapes.get(c.sessionId)
    return already !== undefined && !already.has(shape)
  }

  const isHeld = (c: CallWithSession): boolean =>
    RECONCILING_PROVIDERS.has(c.call.provider) && unsettled.has(`${c.project}\u0000${c.sessionId}`)

  const pending = allCalls.filter(c => !sent.has(c.call.deduplicationKey))
  const frozen = pending.filter(isFrozen)
  const rest = pending.filter(c => !isFrozen(c))
  return { allCalls, unsent: rest.filter(c => !isHeld(c)), held: rest.filter(isHeld), frozen }
}

export type PushOutcome = 'complete' | 'auth-rejected' | 'rate-limited' | 'server-error'

export interface PushResult {
  outcome: PushOutcome
  totalSent: number
  totalRejected: number
  totalCostSent: number
  retryAfter?: string
  httpStatus?: number
  /** Total milliseconds spent waiting on 429 Retry-After */
  totalWaitMs?: number
}

export interface SendBatchesOptions {
  endpoint: string
  accessToken: string
  batches: CallWithSession[][]
  log?: (msg: string) => void
  /** ISO date stamped as codeburn.coverage_through on every batch. Omit when unproven. */
  coverageThrough?: string
  /**
   * Plugin-socket extension point (teams issue #3): the set of attribute keys
   * loaded plugins have DECLARED on their manifests, plus the runtime-supplied
   * attribute values to attach to every span. The wire guard in otlp.ts drops
   * anything not in the declared set, so an empty values array (the common
   * case with no plugin runtime yet) keeps every batch byte-identical.
   */
  pluginAttributes?: { keys: ReadonlySet<string>; values: import('./otlp.js').OtlpAttribute[] }
  /**
   * Per-call attributes and extra spans from plugin exporters (sync exporter
   * seam, teams issue #3 phase 2). Attached to matching calls and appended to
   * the span list across all batches.
   */
  pluginEnrichment?: {
    perCall: Map<string, import('./otlp.js').OtlpAttribute[]>
    extraSpans: import('./otlp.js').OtlpSpan[]
  }
  /** Injectable sleep for tests. Defaults to real setTimeout. */
  sleep?: (ms: number) => Promise<void>
  /** Max wait per 429 (caps Retry-After). Default 120s. */
  maxWaitMs?: number
  /** Consecutive 429 retries per batch before giving up. Default 3. */
  max429Retries?: number
}

/** Parse Retry-After header: delta-seconds or HTTP-date. Returns ms, or null. */
export function parseRetryAfterMs(value: string | null): number | null {
  if (!value) return null
  if (/^-?\d+$/.test(value.trim())) {
    const seconds = Number(value)
    return seconds >= 0 ? seconds * 1000 : null
  }
  const date = Date.parse(value)
  if (!isNaN(date)) return Math.max(0, date - Date.now())
  return null
}

/**
 * Send batches sequentially until all are sent. Ledgers each fully-accepted
 * batch. Partially-rejected batches are NOT ledgered (OTLP doesn't identify
 * which spans were rejected; deterministic span IDs make full-batch retry safe).
 *
 * 429 responses are honored: waits Retry-After (capped at maxWaitMs, default
 * backoff 5s when absent) and retries the same batch, up to max429Retries
 * consecutive times before giving up. Stops on 401/5xx — unsent batches
 * retry on the next push.
 */
export async function sendBatches(opts: SendBatchesOptions): Promise<PushResult> {
  return sendBatchesCore({
    ...opts,
    buildPayload: batch => buildOtlpPayload(batch, {
      ...(opts.coverageThrough ? { coverageThrough: opts.coverageThrough } : {}),
      ...(opts.pluginAttributes
        ? { pluginAttributes: opts.pluginAttributes.values, pluginAttributeKeys: opts.pluginAttributes.keys }
        : {}),
      ...(opts.pluginEnrichment ? { pluginEnrichment: opts.pluginEnrichment } : {}),
    }),
    toOutbound: c => ({ key: c.call.deduplicationKey, ts: c.call.timestamp, costUSD: c.call.costUSD }),
  })
}

/** How sendBatchesCore ledgers and prices a batch item. */
type OutboundItem = { key: string; ts: string; costUSD: number }

type SendBatchesCoreOptions<T> = Omit<SendBatchesOptions, 'batches'> & {
  batches: T[][]
  buildPayload: (batch: T[]) => OtlpPayload
  toOutbound: (item: T) => OutboundItem
}

async function sendBatchesCore<T>(opts: SendBatchesCoreOptions<T>): Promise<PushResult> {
  assertHttps(opts.endpoint, 'Traces endpoint')
  const log = opts.log ?? (() => {})
  const sleep = opts.sleep ?? ((ms: number) => new Promise<void>(r => setTimeout(r, ms)))
  const maxWaitMs = opts.maxWaitMs ?? 120_000
  const max429Retries = opts.max429Retries ?? 3

  let totalSent = 0
  let totalRejected = 0
  let totalCostSent = 0
  let totalWaitMs = 0

  for (const batch of opts.batches) {
    let attempts429 = 0

    // Retry loop for the current batch (429 only)
    for (;;) {
      const payload = opts.buildPayload(batch)

      const response = await fetch(opts.endpoint, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${opts.accessToken}`,
        },
        body: JSON.stringify(payload),
      })

      if (response.status === 401) {
        return { outcome: 'auth-rejected', totalSent, totalRejected, totalCostSent, totalWaitMs, httpStatus: 401 }
      }

      if (response.status === 429) {
        const retryAfterHeader = response.headers.get('Retry-After')
        attempts429++
        if (attempts429 > max429Retries) {
          return {
            outcome: 'rate-limited', totalSent, totalRejected, totalCostSent, totalWaitMs,
            retryAfter: retryAfterHeader ?? undefined, httpStatus: 429,
          }
        }
        const waitMs = Math.min(parseRetryAfterMs(retryAfterHeader) ?? 5000, maxWaitMs)
        log(`  Rate limited — waiting ${Math.round(waitMs / 1000)}s before retrying (attempt ${attempts429}/${max429Retries})`)
        totalWaitMs += waitMs
        await sleep(waitMs)
        continue
      }

      if (!response.ok) {
        return { outcome: 'server-error', totalSent, totalRejected, totalCostSent, totalWaitMs, httpStatus: response.status }
      }

      // Check for partial success
      let rejected = 0
      try {
        const body = await response.json() as { partialSuccess?: { rejectedSpans?: number | string } }
        // proto3 int64 JSON mapping: strict protojson servers send int64 as a
        // string — Number() both so `totalRejected +=` never concatenates.
        rejected = Number(body?.partialSuccess?.rejectedSpans ?? 0)
        if (!Number.isFinite(rejected) || rejected < 0) rejected = 0
      } catch { /* empty response = full success */ }

      if (rejected > 0) {
        // OTLP partial_success doesn't identify WHICH spans were rejected.
        // Ledger nothing — the whole batch retries on the next push.
        totalRejected += rejected
        log(`  Batch: ${rejected}/${batch.length} spans rejected — whole batch will retry on next push`)
      } else {
        const outbound = batch.map(opts.toOutbound)
        const entries: LedgerEntry[] = outbound.map(o => ({ key: o.key, ts: o.ts }))
        appendToLedger(entries)
        totalSent += batch.length
        totalCostSent += outbound.reduce((s, o) => s + o.costUSD, 0)
      }
      break // batch done (success or partial) — move to next batch
    }
  }

  return { outcome: 'complete', totalSent, totalRejected, totalCostSent, totalWaitMs }
}

/**
 * Safety valve for attribution items, mirroring MAX_PER_PUSH: bounds a first
 * `--since all --attribution` push over a long history. Remaining facts are
 * sent on the next push (the ledger tracks progress).
 */
export const MAX_ATTRIBUTION_PER_PUSH = 10_000

/** Flatten attribution records into items and filter out already-sent ones. */
export function collectUnsentAttribution(records: SessionAttributionRecord[]): {
  allItems: AttributionItem[]
  unsent: AttributionItem[]
} {
  const sent = ledgerKeySet()

  // Empty records (no commits, no PR links) exist only to RETRACT a session
  // span whose commits migrated to another session. Send one only when a
  // PRIOR state for that session was already ledgered — a session that was
  // never sent has nothing to retract.
  const sessionsWithPriorState = new Set<string>()
  for (const key of sent) {
    if (key.startsWith('attr:s:')) {
      const sessionId = key.slice('attr:s:'.length, key.lastIndexOf(':'))
      sessionsWithPriorState.add(sessionId)
    }
  }
  const sendable = records.filter(r =>
    r.commits.length > 0 || r.prLinks.length > 0 || sessionsWithPriorState.has(r.sessionId),
  )

  const allItems = flattenAttributionRecords(sendable)
  const unsent = allItems.filter(i => !sent.has(i.dedupKey))
  return { allItems, unsent }
}

export interface SendAttributionBatchesOptions extends Omit<SendBatchesOptions, 'batches'> {
  batches: AttributionItem[][]
}

/**
 * Send attribution batches through the same retry/ledger pipeline as usage
 * batches. Items are ledgered by their state-encoding dedup keys, so an
 * identical attribution fact is sent once and a state transition (commit
 * merged to main, commit reverted) re-sends the updated fact.
 */
export async function sendAttributionBatches(opts: SendAttributionBatchesOptions): Promise<PushResult> {
  return sendBatchesCore({
    ...opts,
    buildPayload: buildAttributionOtlpPayload,
    toOutbound: item => ({ key: item.dedupKey, ts: item.timestamp, costUSD: 0 }),
  })
}

export { batchCalls }
