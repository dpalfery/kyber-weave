---
id: rules/honest-unobservability
title: Honest unobservability and telemetry absence standard
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-09-06
---

# Honest unobservability and telemetry absence standard

This rule mandates that absent telemetry, missing coverage, and unmeasured metrics are represented
transparently as unobserved or not measurable, and never substituted with zeros, passing grades,
or fabricated values.

---

## Mandate

Telemetry absence, unmeasured dimensions, and coverage gaps must always be communicated explicitly
as unobserved or not measurable with a stated reason (`—` or `not_measurable`).

Scorecards, diagnostic ranking engines, and comparative rollups must never substitute missing
telemetry with zero, default passing scores, or fabricated averages. A harness, provider, or
agent execution with no telemetry must never rank as efficient or healthy by absence of evidence.

---

## Motivation

A pervasive failure mode in developer tooling and observability systems is the silent coercion
of missing data to zero:
- A harness that emits no cache telemetry appears to have zero cache misses, falsely suggesting perfect efficiency.
- A tool that does not report error codes appears to have a 100% success rate.
- An uninstrumented agent framework appears to introduce zero latency overhead.

Coercing absence to zero actively rewards poorly instrumented tools over transparently instrumented
ones, penalizes engineering teams who implement comprehensive telemetry, and misleads developers
into making architectural decisions on false premises.

Enforcing honest unobservability ensures that every gap is visible, actionable, and incapable
of being confused with measured success.

---

## Requirements

### R1: Explicit Representation of Unmeasured Metrics
Every metric, scorecard dimension, and diagnostic property must support a first-class
`not_measurable` state alongside measured values:
- In user interfaces, unmeasured dimensions render as an explicit dash (`—`), accompanied by a tooltip
  or badge explaining why the metric could not be determined.
- In JSON APIs and domain models, fields must use discriminated unions (e.g. `{ status: "not_measurable", reason: string }`),
  never `null` or `0`.

### R2: Prohibition of Zero-Coercion
Systems must not coerce missing, absent, or uncollected telemetry into numerical zeros:
- Token usage counters must distinguish between "0 tokens consumed" (an empirically observed zero)
  and "unreported" (no token telemetry exported).
- Rate calculations (e.g., cache hit rate, tool yield) must refuse to evaluate when the denominator
  is unobserved.

### R3: Incomplete Instrumentation Emits Coverage Gap Findings
When a monitored harness or execution environment exports incomplete telemetry, the finding engine
must generate an explicit `coverageGap` finding declaring what signals are missing and how to enable them.
A harness with coverage gaps is disqualified from top efficiency rankings.

### R4: Isolation of Unattributable Residuals
Any discrepancy between reported aggregate usage and reconstructed context parts must be preserved
in an isolated `unattributed` residual block. Residual tokens must never be mathematically smoothed
over or proportionally distributed into other buckets.

---

## Exceptions

Predefined static constants and baseline identity elements in mathematical calculations may use zero
only if explicitly documented as an axiomatic convention rather than an empirical observation (e.g.,
the base timestamp offset of a relative timeline).

---

## Verification & Conformance

- Store layers (`dash/kyber/canon/measurability.ts`) and signal detectors (`analysis/signals.ts`)
  are verified via tests (`measurability.test.ts`, `signals.test.ts`) asserting that missing fields
  yield explicit reasons rather than zero values.
- UI component tests (`Scorecard.test.tsx`) assert that unmeasurable dimensions render dashes and
  contain no fabricated passing indicators.
