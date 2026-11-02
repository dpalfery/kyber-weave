---
id: rules/kyberdash-harness-collection
title: KyberDash harness collection standard
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-11-02
decided-by:
  - adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline
  - adr/0053-factory-capture-is-a-hooks-plugin-pipeline
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
| `opencode` native OpenTelemetry | Not collected by design. OpenCode 2.2.x exports no usage in native spans, and no adapter claims them ([ADR 0048](../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)). | Documented absence. Native spans appear only as unclaimed quarantine, never as an OpenCode session, never as a zero. |
| `opencode` V2 plugin | Contract-3 request, step and tool spans from the `kyberdash-opencode` plugin. Usage and reported cost are measured; system prompt, tool definitions, skills listing and prompt text are content, captured only under consent. | Every OpenCode request is a visible session with its usage, cost, duration and turn count. With consent off, content dimensions render `not_measurable` with a reason, never zero. Live capture is verified by the OpenCode live gate, which passed on 2026-10-31 and runs only on request. |
| `factory` (Droid) plugin | Contract-1 session, turn, tool and child-session spans from the `kyberdash-factory` plugin. The turn boundary, the tool invocations, the timing, the model and the byte sizes are measured; the prompt text is content, captured only under consent. The plugin's hooks are the PRIMARY mechanism; the Factory file source is the **token source** — it reads the session's counters from the companion `<session-id>.settings.json`, and the plugin itself reports no counter at all. Where both sources observed one session, the two are **split by what each can measure**, never summed: the plugin rows are the session's turns and the file rows are its counters and its cost. The content consent is **off by default**, and the Settings switch that installs the capture **never turns it on**: only the explicit consent verb (`kyberdash kyber capture plugin consent --harness factory --on`) or the owner editing the consent file turns the prompt recording on. This is deliberate, and it differs from OpenCode, whose switch also sets consent — it is a privacy control, so a capture install is never also a decision to record prompts. | Every Factory request is a visible session with its turn and tool structure. Where no source supplied a counter the token classes render `not_measurable` with a reason that names the file source and the session-id pairing as where the number comes from — never zero, and never a number borrowed from another session. A plugin session the file source also observed is the same session, joined on exact session-id equality, and the two sources are **split rather than summed**: the plugin rows are the session's turns — the turn count, the timeline's turns, the request roots, the per-turn model, the tool invocations, the duration and the context turns — and the file rows are the session's counters, the token totals and the availability that gates them, and its cost. The file row contributes nothing else: it is not a turn, not a timeline entry, not a request root and not a tool invocation, and its duration is not added to the session's duration. The cost is one figure over the union of the turn records and the counter records, which are disjoint, so no cost is counted twice. The context analysis reads the turn records and falls back to the counter records where no turn record carries a measured input. A plugin-only session and a file-only session keep the figures their own source gave them, and a near-miss id stays its own session. The split is Factory's alone: every other harness's derivation is unchanged. Live capture is proved by an owner-run check, not by CI; the recorded replay proves only what the dashboard renders from the replay. |
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

The spec's OpenCode scenario replays recorded OpenCode 2.2.1 events through the production plugin,
receiver, adapter and projection. Its honest-empty row and the other seeded rows are rendering-only
seeds. None of them is proof of capture. Live capture is proven by the OpenCode live gate, which passed on
2026-10-31 and runs only on request ([runbook §1b](../dash/runbook.md#1b-opencode-capture-enable-and-verify)).

## Related

- [Honest unobservability](honest-unobservability.md) — `not_measurable` rendering, zero-coercion ban
- [Secondary cost display](secondary-cost-display.md) — cost as derived secondary metric, pricing-basis transparency
- [Ban on composite efficiency scores](composite-efficiency-ban.md) — dimensions stay independent vectors
- [Rules index](README.md)
