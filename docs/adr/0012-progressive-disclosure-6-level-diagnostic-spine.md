---
id: adr/0012-progressive-disclosure-6-level-diagnostic-spine
title: Progressive Disclosure Six-Level Diagnostic Spine and Independent Dimension Vectors
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-05
---

# ADR 0012: Progressive Disclosure Six-Level Diagnostic Spine and Independent Dimension Vectors

## Status

Accepted, 2026-09-05. Extends [ADR 0007](0007-kyberdash-agent-session-analysis-integration.md) and [ADR 0011](0011-asad-only-context-view-and-payload-contract.md) by introducing progressive disclosure entities above and below the single-session view.

## Context

ADR 0007 and ADR 0011 established the Agent Session Analysis Dashboard (ASAD) as the canonical single-session view. However, developers operating AI coding agents across multiple runs and harnesses faced critical structural limitations:
1. **No level existed above the session**: A developer with dozens or hundreds of runs across ten harnesses had no ranked entry point or diagnostic overview answering "where should I look first?"
2. **Sessions do not reflect the developer's unit of work**: A single user-initiated task often spawns multiple child sessions (e.g. root orchestrator delegating to specialized subagents). Delegation overhead—tokens and latency paid strictly to orchestrate handoffs—was invisible because no canonical entity spanned the task.
3. **Composite scoring masks critical failures**: Flattening multidimensional agent telemetry into a single aggregate "efficiency score" encourages false optimization, rewards harnesses with sparse telemetry (treating missing data as zero), and obscures whether high cache hit rates are cancelled by massive tool definition bloat or delegation thrash.

## Decision

1. **Six-Level Diagnostic Spine**: The navigation topology is strictly structured as:
   `All Harnesses (Attention) → Harness → Run → AgentExecution → Turn → ContextItem`.
   - The cross-harness landing page (Attention) summarizes and directs attention; the harness is the first actionable tier.
   - Every level preserves breadcrumb context, scope, active baseline, and relative deltas.
2. **First-Class Canonical Entities (`Run` and `AgentExecution`)**:
   - A `Run` represents one user-initiated unit of work and groups all executions belonging to that task.
   - An `AgentExecution` represents a single agent session within that run, capturing parent/child delegation relationships.
   - Both entities are derived and fully rebuildable from canonical records in `canon.db`.
3. **Derived Run Identity (D13)**:
   - When a harness emits an explicit run or task identifier, that identity is preserved directly.
   - When no run identity is emitted, runs are formed via labelled *derived* grouping (working-directory and bounded time gaps). Derived groupings are explicitly labelled as `derived` with their rule stated, never fabricated or presented as reported fact.
4. **Independent Dimension Vectors — No Composite Score (D3)**:
   - Diagnostic evaluation uses six independent dimensions: **Context Hygiene**, **Cache Efficiency**, **Tool Yield**, **Skill Utilisation**, **Delegation Overhead**, and **Continuity**.
   - No composite score, letter grade, or weighted aggregate efficiency index is computed.
   - Missing or unmeasurable telemetry renders as a dash (`—`) with an explicit reason, never as zero and never as a passing grade.
5. **Phase-Aligned Run Comparison and Prediction Calibration (D11)**:
   - Comparison between runs of a task family aligns turns by execution phase rather than chronological turn index.
   - Diagnostic predictions state explicit confidence and error bars, recorded in `canon.db` and scored when comparable later runs exist.

## Alternatives Considered

- **Single composite efficiency score (0–100 or letter grade)**: Rejected. Composite scoring hides specific architectural trade-offs (e.g., trading tool schema size for reasoning tokens) and mathematically rewards harnesses with missing telemetry by assuming unobserved fields are optimal.
- **Requiring harness-native run IDs only**: Rejected. Most CLI harnesses emit only session files without run identifiers; without derived grouping, multi-agent workflows would remain permanently fragmented into disconnected sessions.
- **Silent heuristic clustering**: Rejected. Merging sessions into runs without declaring the grouping basis creates confusion when time-gap heuristics misgroup overlapping executions. All derived groupings must be transparently labelled.

## Consequences

- The store schema introduces `run` and `execution` derived tables migrated in `CanonStore`, rebuildable via `kyber build`.
- Navigation in the web dashboard spans `Attention`, `HarnessDetail`, `RunDetail`, down to `AgentSessionDashboard` and `ContextInspector`.
- Rollup metrics and scorecards preserve measurability contracts; harnesses without telemetry produce dashes rather than misleading zeros.
- Comparison views align runs by phase and guard against premature recommendations (requiring sufficiency thresholds before promoting findings).

## Related

- [ADR 0007: KyberDash Agent Session Analysis Integration](0007-kyberdash-agent-session-analysis-integration.md)
- [ADR 0011: ASAD as the Only Context View and as the Canonical Session Contract](0011-asad-only-context-view-and-payload-contract.md)
- [ADR 0013: Telemetry-Grounded Finding Contracts and Waste Ranking](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md)
- [KyberDash architecture](../dash/architecture.md)
- [Plan: 2026-09-05 Diagnostic Hierarchy and Context Inspector](../plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md)
