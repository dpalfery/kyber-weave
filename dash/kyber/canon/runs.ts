// Run and AgentExecution boundary derivation for KyberDash (D13, ADR 0008).
//
// Decision D13 rules:
// Require explicit run identity where emitted by the harness; otherwise provide
// a labelled derived grouping based on working-directory and bounded time gaps
// (e.g. 15-30 min inactivity window). Never silently present heuristic grouping
// as raw fact.
//
// Parent/child execution linkage is recorded as measured where trace parentage
// or session parentage exists; otherwise it is reported as not_measurable with
// an explicit explanation.

import { isFileSource, measurabilityFor } from './measurability.js'
import { deriveOutcome, type OutcomeBlock } from './outcome.js'
import { CanonStore } from './store.js'
import { notMeasurable } from './types.js'
import type {
  CanonicalRecord,
  ExecutionRow,
  MetricAvailability,
  RunGroupingBasis,
  RunRow,
} from './types.js'

/** Attributes naming explicit run, task, or workflow boundaries emitted by harnesses. */
export const RUN_ID_ATTRIBUTE_KEYS = [
  'gen_ai.run.id',
  'run.id',
  'run_id',
  'runId',
  'gen_ai.workflow.id',
  'workflow.id',
  'workflow_id',
  'task.id',
  'task_id',
] as const

/** Attributes identifying working directory or workspace location. */
export const WORKING_DIRECTORY_ATTRIBUTE_KEYS = [
  'process.working_directory',
  'working_directory',
  'cwd',
  'workspace.root',
  'project.path',
  'vcs.repository.name',
  'repo',
] as const

/** Attributes identifying parent session or conversation ancestry. */
export const PARENT_SESSION_ATTRIBUTE_KEYS = [
  'parent_session',
  'parent_session_id',
  'gen_ai.parent_session_id',
  'parentSession',
] as const

/** Attributes naming the agent or subagent persona. */
export const AGENT_NAME_KEYS = ['gen_ai.agent.name', 'agent.name'] as const

/** Default inactivity window: 30 minutes in milliseconds. */
export const DEFAULT_INACTIVITY_WINDOW_MS = 30 * 60 * 1000

function rawAttribute(record: CanonicalRecord, keys: readonly string[]): string | undefined {
  if (record.raw === null || typeof record.raw !== 'object' || Array.isArray(record.raw)) {
    return undefined
  }
  const raw = record.raw as Record<string, unknown>
  for (const key of keys) {
    const val = raw[key]
    if (typeof val === 'string' && val.trim() !== '') return val.trim()
    if (typeof val === 'number' && Number.isFinite(val)) return String(val)
  }
  return undefined
}

export type DerivedRunIdentity = {
  runId: string
  groupingBasis: RunGroupingBasis
  groupingRule: string
  workingDirectory?: string | null
  outcome?: OutcomeBlock
}

export type DeriveRunIdentityOptions = {
  acceptDerivedGrouping?: boolean
  inactivityWindowMs?: number
  fallbackRunId?: string
}

/**
 * Derives run identity for a record or set of records following Decision D13.
 * If the harness emitted an explicit run identifier, groupingBasis is 'explicit'.
 * Otherwise, groupingBasis is ALWAYS 'derived' with the applied heuristic rule named.
 * Links the derived OutcomeBlock as a guard signal (D7/D8).
 */
export function deriveRunIdentity(
  target: CanonicalRecord | readonly CanonicalRecord[],
  options?: DeriveRunIdentityOptions,
): DerivedRunIdentity {
  const records = Array.isArray(target) ? (target as readonly CanonicalRecord[]) : [target as CanonicalRecord]
  const outcome = deriveOutcome(records)

  if (records.length === 0) {
    return {
      runId: options?.fallbackRunId ?? 'unknown',
      groupingBasis: 'derived',
      groupingRule: 'session_fallback',
      workingDirectory: null,
      outcome,
    }
  }

  const first = records[0]!

  // Check for explicit run identity emitted by the harness
  for (const record of records) {
    const explicitId = rawAttribute(record, RUN_ID_ATTRIBUTE_KEYS)
    if (explicitId !== undefined) {
      const cwd = records.map((r) => rawAttribute(r, WORKING_DIRECTORY_ATTRIBUTE_KEYS)).find(Boolean) ?? null
      return {
        runId: explicitId,
        groupingBasis: 'explicit',
        groupingRule: 'explicit_run_id',
        workingDirectory: cwd,
        outcome,
      }
    }
  }

  // Check for working-directory based derived grouping
  const cwd = records.map((r) => rawAttribute(r, WORKING_DIRECTORY_ATTRIBUTE_KEYS)).find(Boolean) ?? null
  const acceptDerived = options?.acceptDerivedGrouping !== false

  if (acceptDerived && cwd !== null) {
    const timestamps = records
      .map((r) => Date.parse(String(r.timestamp)))
      .filter((ts) => !Number.isNaN(ts))
    const startTime = timestamps.length > 0 ? Math.min(...timestamps) : 0
    const cleanCwd = cwd.replace(/[^a-zA-Z0-9_-]/g, '_')
    return {
      runId: `derived:${first.harness}:${cleanCwd}:${startTime}`,
      groupingBasis: 'derived',
      groupingRule: 'working_directory_and_inactivity_window',
      workingDirectory: cwd,
      outcome,
    }
  }

  // Fallback: one-session or one-execution derived run
  const fallback = options?.fallbackRunId ?? first.sessionId ?? first.traceId ?? first.spanId
  return {
    runId: `derived:${first.harness}:${fallback}`,
    groupingBasis: 'derived',
    groupingRule: 'session_fallback',
    workingDirectory: cwd,
    outcome,
  }
}

/** One candidate execution to be linked into a parent/child tree. */
export type ExecutionCandidate = {
  executionId: string
  runId?: string
  sessionId?: string | null
  parentExecutionId?: string | null
  harness: string
  agentName?: string | null
  started?: string | null
  ended?: string | null
  records: readonly CanonicalRecord[]
  parentSessionId?: string | null
  workingDirectory?: string | null
}

export type LinkExecutionsOptions = {
  unmeasurableReason?: string
}

/**
 * Links parent and child executions within a run.
 * Trace parentage (span parent matching a span in another execution) and session parentage
 * provide ground truth ('measured'). If no trace or session parentage exists, or if the
 * source cannot measure execution structure, parentLinkage is stamped 'not_measurable'.
 */
export function linkExecutions(
  executions: readonly ExecutionCandidate[],
  options?: LinkExecutionsOptions,
): ExecutionRow[] {
  if (executions.length === 0) return []

  const runId = executions[0]!.runId ?? 'unknown'
  const harness = executions[0]!.harness

  // Map each span_id to the execution_id it belongs to
  const spanToExecution = new Map<string, string>()
  for (const exec of executions) {
    for (const record of exec.records) {
      spanToExecution.set(record.spanId, exec.executionId)
    }
  }

  // Map session_id to execution_id
  const sessionToExecution = new Map<string, string>()
  for (const exec of executions) {
    if (exec.sessionId) {
      sessionToExecution.set(exec.sessionId, exec.executionId)
    }
  }

  // Determine parentage for each candidate
  type LinkedCandidate = {
    exec: ExecutionCandidate
    parentExecutionId: string | null
    linkageBasis: 'measured' | 'not_measurable'
    linkageReason?: string
  }

  const linkedCandidates: LinkedCandidate[] = []

  for (const exec of executions) {
    let parentExecId: string | null = null
    let linkageBasis: 'measured' | 'not_measurable' = 'not_measurable'
    let linkageReason: string | undefined

    // 1. Trace parentage: span-level parentSpanId pointing to another execution's span
    for (const record of exec.records) {
      if (record.parentSpanId !== null) {
        const parentCandidate = spanToExecution.get(record.parentSpanId)
        if (parentCandidate !== undefined && parentCandidate !== exec.executionId) {
          parentExecId = parentCandidate
          linkageBasis = 'measured'
          break
        }
      }
    }

    // 2. Session parentage: parentSessionId attribute matching another execution
    if (parentExecId === null && exec.parentSessionId) {
      const parentCandidate = sessionToExecution.get(exec.parentSessionId)
      if (parentCandidate !== undefined && parentCandidate !== exec.executionId) {
        parentExecId = parentCandidate
        linkageBasis = 'measured'
      }
    }

    // Check if the source explicitly lacks execution structure
    const isFile = exec.records.some((r) => isFileSource(r.source))
    const explicitUnmeasurable = exec.records
      .map((r) => r.measurability?.['execution_structure'])
      .find((v) => typeof v === 'object' && v?.availability === 'not_measurable') as
      | { availability: 'not_measurable'; reason: string }
      | undefined

    if (linkageBasis === 'not_measurable') {
      if (options?.unmeasurableReason) {
        linkageReason = options.unmeasurableReason
      } else if (explicitUnmeasurable) {
        linkageReason = explicitUnmeasurable.reason
      } else if (isFile) {
        const fileDeclarations = measurabilityFor(exec.harness)
        const decl = fileDeclarations['execution_structure']
        linkageReason =
          typeof decl === 'object' && 'reason' in decl
            ? decl.reason
            : `Session files for ${exec.harness} do not include execution_structure data.`
      } else {
        linkageReason = 'The harness did not emit parent-child trace or session parentage.'
      }
    }

    linkedCandidates.push({
      exec,
      parentExecutionId: parentExecId,
      linkageBasis,
      linkageReason,
    })
  }

  // Count how many executions were linked to parents
  const hasMeasuredChildren = linkedCandidates.some((c) => c.linkageBasis === 'measured')

  return linkedCandidates.map(({ exec, parentExecutionId, linkageBasis, linkageReason }) => {
    const isRoot = parentExecutionId === null

    let parentLinkage: MetricAvailability
    if (parentExecutionId !== null && linkageBasis === 'measured') {
      // Child execution with verified parentage
      parentLinkage = 'measured'
    } else if (isRoot && hasMeasuredChildren) {
      // Root execution of a measured hierarchy
      parentLinkage = 'measured'
    } else if (isRoot && executions.length === 1 && linkageBasis !== 'measured') {
      // Single execution in a run: if source cannot measure hierarchy, report not_measurable honestly
      const isFile = exec.records.some((r) => isFileSource(r.source))
      const hasUnmeasurableDecl =
        isFile ||
        options?.unmeasurableReason !== undefined ||
        exec.records.some((r) => r.measurability?.['execution_structure'] !== undefined)
      if (hasUnmeasurableDecl) {
        parentLinkage = notMeasurable(
          options?.unmeasurableReason ??
            linkageReason ??
            `Session files for ${exec.harness} do not include execution_structure data.`,
        )
      } else {
        // Single agent run with verified telemetry
        parentLinkage = 'measured'
      }
    } else {
      // Multiple unlinked executions or unmeasurable structure
      parentLinkage = notMeasurable(
        linkageReason ?? 'The harness did not emit parent-child trace or session parentage.',
      )
    }

    return {
      executionId: exec.executionId,
      runId: exec.runId ?? runId,
      sessionId: exec.sessionId ?? null,
      parentExecutionId,
      harness: exec.harness,
      agentName: exec.agentName ?? null,
      isRoot,
      started: exec.started ?? null,
      ended: exec.ended ?? null,
      parentLinkage,
    }
  })
}

/** Check whether records represent genuine model turns or conversation evidence. */
function hasTurnEvidence(records: readonly CanonicalRecord[]): boolean {
  const turns = records.filter((r) => r.op === 'llm.invoke')
  if (turns.length === 0) return false
  return turns.some(
    (r) =>
      r.tokens.reportedInput > 0 ||
      r.tokens.output > 0 ||
      (r.parts?.length ?? 0) > 0 ||
      Object.keys(r.content).length > 0,
  )
}

export type BuildRunsOptions = {
  inactivityWindowMs?: number
  acceptDerivedGrouping?: boolean
}

export type BuildRunsReport = {
  runsBuilt: number
  executionsBuilt: number
  prunedRuns: number
  prunedExecutions: number
}

/**
 * Rebuild derived `run` and `execution` tables over canonical records in SQLite.
 *
 * Runs and executions are rebuildable derived tables over `records` (ADR 0008, D13).
 * Drops stale rows and ensures zero data loss.
 */
export async function buildRuns(
  store: CanonStore,
  options?: BuildRunsOptions,
): Promise<BuildRunsReport> {
  const inactivityWindowMs = options?.inactivityWindowMs ?? DEFAULT_INACTIVITY_WINDOW_MS
  const acceptDerived = options?.acceptDerivedGrouping !== false

  // 1. Gather all candidate executions from session keys
  const candidates: ExecutionCandidate[] = []
  for (const sessionKey of store.sessionKeys()) {
    const records = store.recordsForSession(sessionKey.key)
    if (records.length === 0 || !hasTurnEvidence(records)) {
      continue
    }

    const first = records[0]!
    const started = records[0]!.timestamp
    const ended = records[records.length - 1]!.timestamp
    const cwd = records.map((r) => rawAttribute(r, WORKING_DIRECTORY_ATTRIBUTE_KEYS)).find(Boolean) ?? null
    const parentSession = records.map((r) => rawAttribute(r, PARENT_SESSION_ATTRIBUTE_KEYS)).find(Boolean) ?? null
    const agentName = records.map((r) => rawAttribute(r, AGENT_NAME_KEYS)).find(Boolean) ?? null

    candidates.push({
      executionId: sessionKey.key,
      sessionId: sessionKey.key,
      harness: sessionKey.harness,
      agentName,
      started: typeof started === 'string' ? started : started.toISOString(),
      ended: typeof ended === 'string' ? ended : ended.toISOString(),
      records,
      parentSessionId: parentSession,
      workingDirectory: cwd,
    })
  }

  // 2. Group candidate executions into Runs
  // Group A: Explicit run identity emitted by harness
  const explicitGroups = new Map<string, ExecutionCandidate[]>()
  const unassigned: ExecutionCandidate[] = []

  for (const cand of candidates) {
    const explicitRunId = cand.records.map((r) => rawAttribute(r, RUN_ID_ATTRIBUTE_KEYS)).find(Boolean)
    if (explicitRunId !== undefined) {
      const group = explicitGroups.get(explicitRunId) ?? []
      group.push(cand)
      explicitGroups.set(explicitRunId, group)
    } else {
      unassigned.push(cand)
    }
  }

  type PlannedRun = {
    run: RunRow
    executions: ExecutionCandidate[]
  }

  const plannedRuns: PlannedRun[] = []

  // Create explicit runs
  for (const [explicitRunId, execs] of explicitGroups) {
    const sorted = [...execs].sort((a, b) => Date.parse(a.started ?? '') - Date.parse(b.started ?? ''))
    const first = sorted[0]!
    const last = sorted[sorted.length - 1]!
    const cwd = sorted.map((e) => e.workingDirectory).find(Boolean) ?? null
    const allRecords = sorted.flatMap((e) => e.records)
    const outcome = deriveOutcome(allRecords)

    plannedRuns.push({
      run: {
        runId: explicitRunId,
        harness: first.harness,
        label: first.agentName ?? first.executionId,
        groupingBasis: 'explicit',
        groupingRule: 'explicit_run_id',
        workingDirectory: cwd,
        started: first.started,
        ended: last.ended,
        executionCount: sorted.length,
        outcome,
      },
      executions: sorted,
    })
  }

  // Group B: Derived grouping per harness based on working directory and inactivity window
  const byHarness = new Map<string, ExecutionCandidate[]>()
  for (const cand of unassigned) {
    const group = byHarness.get(cand.harness) ?? []
    group.push(cand)
    byHarness.set(cand.harness, group)
  }

  for (const [harness, harnessCandidates] of byHarness) {
    if (!acceptDerived) {
      // Fallback: one-execution per run
      for (const cand of harnessCandidates) {
        plannedRuns.push({
          run: {
            runId: `derived:${harness}:${cand.executionId}`,
            harness,
            label: cand.agentName ?? cand.executionId,
            groupingBasis: 'derived',
            groupingRule: 'session_fallback',
            workingDirectory: cand.workingDirectory,
            started: cand.started,
            ended: cand.ended,
            executionCount: 1,
            outcome: deriveOutcome(cand.records),
          },
          executions: [cand],
        })
      }
      continue
    }

    // Cluster by working directory
    const byCwd = new Map<string, ExecutionCandidate[]>()
    const noCwd: ExecutionCandidate[] = []

    for (const cand of harnessCandidates) {
      if (cand.workingDirectory) {
        const list = byCwd.get(cand.workingDirectory) ?? []
        list.push(cand)
        byCwd.set(cand.workingDirectory, list)
      } else {
        noCwd.push(cand)
      }
    }

    for (const [cwd, cwdCandidates] of byCwd) {
      const sorted = [...cwdCandidates].sort(
        (a, b) => Date.parse(a.started ?? '') - Date.parse(b.started ?? ''),
      )

      // Time-gap clustering: cluster if inactivity <= inactivityWindowMs
      let currentCluster: ExecutionCandidate[] = []
      let clusterLastEndedMs = 0

      for (const cand of sorted) {
        const startMs = Date.parse(cand.started ?? '') || 0
        const endMs = Date.parse(cand.ended ?? '') || startMs

        if (currentCluster.length === 0) {
          currentCluster.push(cand)
          clusterLastEndedMs = endMs
        } else {
          const gap = startMs - clusterLastEndedMs
          if (gap <= inactivityWindowMs) {
            currentCluster.push(cand)
            clusterLastEndedMs = Math.max(clusterLastEndedMs, endMs)
          } else {
            // Commit current cluster and start new one
            const clusterStart = currentCluster[0]!.started
            const clusterEnd = currentCluster[currentCluster.length - 1]!.ended
            const cleanCwd = cwd.replace(/[^a-zA-Z0-9_-]/g, '_')
            const runId = `derived:${harness}:${cleanCwd}:${Date.parse(clusterStart ?? '') || 0}`
            const allRecords = currentCluster.flatMap((e) => e.records)
            const outcome = deriveOutcome(allRecords)

            plannedRuns.push({
              run: {
                runId,
                harness,
                label: currentCluster[0]!.agentName ?? currentCluster[0]!.executionId,
                groupingBasis: 'derived',
                groupingRule: 'working_directory_and_inactivity_window',
                workingDirectory: cwd,
                started: clusterStart,
                ended: clusterEnd,
                executionCount: currentCluster.length,
                outcome,
              },
              executions: currentCluster,
            })

            currentCluster = [cand]
            clusterLastEndedMs = endMs
          }
        }
      }

      if (currentCluster.length > 0) {
        const clusterStart = currentCluster[0]!.started
        const clusterEnd = currentCluster[currentCluster.length - 1]!.ended
        const cleanCwd = cwd.replace(/[^a-zA-Z0-9_-]/g, '_')
        const runId = `derived:${harness}:${cleanCwd}:${Date.parse(clusterStart ?? '') || 0}`
        const allRecords = currentCluster.flatMap((e) => e.records)
        const outcome = deriveOutcome(allRecords)

        plannedRuns.push({
          run: {
            runId,
            harness,
            label: currentCluster[0]!.agentName ?? currentCluster[0]!.executionId,
            groupingBasis: 'derived',
            groupingRule: 'working_directory_and_inactivity_window',
            workingDirectory: cwd,
            started: clusterStart,
            ended: clusterEnd,
            executionCount: currentCluster.length,
            outcome,
          },
          executions: currentCluster,
        })
      }
    }

    // Fallback for candidates without working directory
    for (const cand of noCwd) {
      plannedRuns.push({
        run: {
          runId: `derived:${harness}:${cand.executionId}`,
          harness,
          label: cand.agentName ?? cand.executionId,
          groupingBasis: 'derived',
          groupingRule: 'session_fallback',
          workingDirectory: null,
          started: cand.started,
          ended: cand.ended,
          executionCount: 1,
          outcome: deriveOutcome(cand.records),
        },
        executions: [cand],
      })
    }
  }

  // 3. Persist planned runs and their linked executions
  const builtRunIds = new Set<string>()
  const builtExecutionIds = new Set<string>()

  for (const { run, executions } of plannedRuns) {
    store.upsertRun(run)
    builtRunIds.add(run.runId)

    // Tag each candidate with the resolved runId before linking
    const preparedCandidates = executions.map((e) => ({ ...e, runId: run.runId }))
    const linked = linkExecutions(preparedCandidates)

    store.upsertExecutions(linked)
    for (const exec of linked) {
      builtExecutionIds.add(exec.executionId)
    }
  }

  // 4. Prune stale rows
  let prunedRuns = 0
  for (const existingRunId of store.builtRunIds()) {
    if (!builtRunIds.has(existingRunId)) {
      store.deleteRun(existingRunId)
      prunedRuns += 1
    }
  }

  let prunedExecutions = 0
  for (const existingExecId of store.builtExecutionIds()) {
    if (!builtExecutionIds.has(existingExecId)) {
      store.deleteExecution(existingExecId)
      prunedExecutions += 1
    }
  }

  return {
    runsBuilt: builtRunIds.size,
    executionsBuilt: builtExecutionIds.size,
    prunedRuns,
    prunedExecutions,
  }
}
