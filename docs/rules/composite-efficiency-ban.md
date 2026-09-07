---
id: rules/composite-efficiency-ban
title: Ban on composite efficiency scores and single-scalar grading
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-09-06
---

# Ban on composite efficiency scores and single-scalar grading

This rule prohibits computing or rendering a single composite efficiency score, aggregate percentage,
or letter grade across multidimensional agent telemetry.

---

## Mandate

Diagnostic systems, performance scorecards, and evaluation engines must not compute or display
a single composite efficiency score (e.g., "Efficiency: 84%", "Score: A-") across disparate
agent execution dimensions.

Agent diagnostics must be evaluated and displayed as independent, orthogonal dimension vectors.

---

## Motivation

Collapsing multifaceted agent observability into a single scalar score creates serious distortions:
1. **Masking Critical Failures**: A run with a 95% cache hit rate will achieve a high composite score
   even if it suffered from massive tool definition bloat, failed subagent handoffs, or severe
   conversational thrash.
2. **Metric Gaming**: Aggregate scoring creates incentives to game the easiest component (e.g. padding
   system prompts with static boilerplate to inflate cache ratios) to compensate for architectural defects.
3. **Penalizing Comprehensive Telemetry**: When telemetry is incomplete, composite scoring formulas
   inevitably treat missing fields as zeros or average them away, penalizing richly instrumented
   harnesses while flattering dark ones.
4. **Loss of Diagnostic Actionability**: Telling an engineer their agent has an "Efficiency Score of 72"
   provides zero actionable information on what to fix. Telling them their cache prefix is invalidated
   at turn 3 due to timestamp injection provides immediate clarity.

---

## Requirements

### R1: Six Independent Dimension Vectors
Agent evaluation must maintain six orthogonal dimensions:
- **Context Hygiene**: Proportion of active instructions versus redundant conversational history.
- **Cache Efficiency**: Prefix stability, cache read ratio, and breakpoint invalidation.
- **Tool Yield**: Ratio of invoked tool calls to offered tool schemas.
- **Skill Utilisation**: Empirical invocation frequency of declared capabilities.
- **Delegation Overhead**: Tokens and latency dedicated to orchestrating subagent handoffs.
- **Continuity**: Context preservation and stability across compaction events.

### R2: Absolute Ban on Scalar Aggregates
No formula, component, API endpoint, or database table may produce:
- A single combined percentage score across dimensions.
- A letter grade ("A", "B+", "F").
- A weighted average index.

### R3: Independent Dimensional Gating
A telemetry gap in one dimension must render as unmeasurable (`—`) for that specific dimension
without altering, depressing, or masking the scores of other independent dimensions.

---

## Exceptions

Single-dimension ratios (such as a pure cache hit ratio $\frac{\text{cacheRead}}{\text{totalInput}}$)
are permitted as individual metrics, provided they remain isolated within their specific domain
and are not blended with other dimensions.

---

## Verification & Conformance

- UI component test suites (`dash/src/components/kyber/Scorecard.test.tsx`) assert that rendered
  markup contains no composite score elements, aggregate percentages, or letter grades anywhere
  in the DOM.
- REST API contracts (`dash/kyber/server/routes.ts`) ensure harness rollups and run summaries emit
  independent dimension objects rather than scalar efficiency indexes.
