---
id: rules/kyberdash-harness-collection
title: KyberDash harness collection standard
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-10-30
---

# KyberDash harness collection standard

This rule states, per harness, what must be collected and what the dashboard must
show. It is current guidance and carries the standing of the other
[rules](README.md). A harness row below is ground truth: the collection mechanism
and the dashboard rendering it requires are fixed here, not inferred per run.
The rows below cover the OTel/matrix harnesses; file-source harness ground truth
lives in the [telemetry inventory](../dash/telemetry-inventory.md).

## Mandate

Every harness with a standard name collects through its declared primary
mechanism, and the dashboard renders exactly what was collected: a measured
value where the source supplies one, and an explicit `not_measurable` / dash
with a stated reason where it genuinely cannot. Absence is never coerced to
zero, never silently skipped, and never labelled complete.

## Per-harness ground truth

| Harness | Collection | Dashboard must show |
|---|---|---|
| `claude-code` | Reference harness. Counters plus content through the primary mechanism. | Measured counters and content-derived dimensions as real values. The completeness bar for every other row. |
| `opencode` native Effect | By-design quarantine. No model operation is collected. | Documented absence. Rendered as a stated by-design gap, never a defect and never a zero. |
| `opencode` plugin | Counter-less turns project as visible sessions. Dimensions the source cannot supply render `not_measurable`. | Every recognized turn is a visible session. Unsupplied dimensions render dash with reason. |
| `pi` | Capture-side stopped exporter. Environment fact, not a pipeline defect. | Honest-empty with the stated reason. No fabricated values. |
| `copilot (chat)` | Capture-side stopped exporter. Environment fact, not a pipeline defect. | Honest-empty with the stated reason. No fabricated values. |
| `antigravity` | Capture-side stopped exporter. Environment fact, not a pipeline defect. | Honest-empty with the stated reason. No fabricated values. |
| `codex` OTLP | Owner-gated collection. | Measured values when the owner enables it; explicit gated-absent rendering with reason when not. |
| `gemini` | Attribution-only. Never a stored harness. | Attribution where recognized; never a stored harness row, never a fabricated session. |

## Completeness predicate

A harness rendering is complete if and only if all of the following hold:

1. Every standard-named dimension is either (a) a real measured value from a
   PRIMARY mechanism (see below), or (b) rendered `not_measurable` / dash with
   a stated reason because the source genuinely cannot supply it.
2. A recognized model call always surfaces as a visible session. An absent
   session for a recognized call fails completeness.
3. A zero-record harness renders honest-empty ("No runs recorded"). Honest-empty
   is never "complete".

Never allowed:

- A fabricated zero for a missing or empty dimension.
- A silent skip of a standard-named dimension.
- An absent session for a recognized call.
- "Complete" claimed for a missing or empty dimension.

This predicate restates [honest unobservability](honest-unobservability.md) for
the harness surface: unmeasured renders as unmeasured with a reason, never as
zero or as passing by absence.

## Mechanism hierarchy

PRIMARY mechanisms are OpenTelemetry (OTel), plugin hooks, and direct database
reads. File ingest — `dash refresh` synth and `dash import-history` — is
backup and fallback only where a harness declares a PRIMARY mechanism. A
fallback-derived value must never be billed as a primary measurement, and a
completeness claim must never rest on a fallback value where a primary
mechanism is declared.

Some harnesses have no OTel, plugin, or database surface at all — Command Code,
Cursor, Devin, Warp and Kilo are read from their own files. For those, the file
source **is** that harness's primary mechanism: it may be billed as a measured
value, stated as file-derived, and used for a completeness claim. It is still
not a substitute for a PRIMARY mechanism on a harness that has one.

## Cost and rate exclusion

Per-session "no published rate" is honest-empty cost state, per
[honest unobservability](honest-unobservability.md) and
[secondary cost display](secondary-cost-display.md) R3. It must appear as
`unpriced` / stated-reason rendering, never `$0.00`, and it must not be a
"complete" criterion. Cost is derived and secondary; completeness is decided on
collection dimensions alone.

## Running the conformance spec

The conformance spec (`dash/e2e/harness-collection.spec.ts`) is run only with the isolated
Playwright config, through the process watchdog, from the repository root:

```bash
scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts
```

The isolated config starts no web server, sets the spawn guard to deny, and fails the run if
the spec leaves a process behind. The spec boots its own dashboard on an ephemeral port from a
temp `canon.db`, so a run never reaches the live dashboard port or `~/.kyberdash/canon.db`. A
run that skips the isolated config is not a conformance result. See the
[runbook](../dash/runbook.md#running-tests-safely).

## Related

- [Honest unobservability](honest-unobservability.md) — `not_measurable` rendering, zero-coercion ban
- [Secondary cost display](secondary-cost-display.md) — cost as derived secondary metric, pricing-basis transparency
- [Ban on composite efficiency scores](composite-efficiency-ban.md) — dimensions stay independent vectors
- [Rules index](README.md)
