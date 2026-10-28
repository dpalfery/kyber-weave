---
id: todo/opencode-plugin-token-usage-capture
title: OpenCode plugin token-usage capture, content projection, and per-source last-emitted status
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-27
---

# OpenCode plugin token-usage capture, content projection, and per-source last-emitted status

Deferred from the [KyberDash OTel harness coverage plan](../archive/plans/2026-10-27-kyberdash-otel-harness-coverage.md)
(D4 and D5). Both are capture-side gaps recorded here rather than fixed in that plan, which
corrected the projection and the documentation only.

## Behaviour today

The dash-managed OpenCode plugin (`dash/src/capture/harnesses/opencode-plugin/`) observes
request assembly, not completion usage. `OpencodeHookPayload` has no `tokens` field and
`stampMapperPayload` never sets one, so a plugin span carries a session id, model identity, and
optionally content — never token counters. The mapper's `OpencodePluginSpanTokens` shape and its
OpenInference counter conversion exist and are unit-pinned, but no hook populates them.

The projection no longer hides this: a counter-less, content-less plugin turn now projects as a
visible session whose content is `not_measurable` and whose unchartable turns are counted
(`unmeasuredTurns`), each with a reason. The session is honest but unmeasured for tokens, so the
dashboard cannot show usage for an OpenCode-plugin session.

Content remains gated by `KYBERDASH_OPENCODE_CONTEXT=1`; this todo does not change that gate.

**Captured content on a counter-less turn is not charted.** `buildSessionRow` passes only
turns with a measured input to `analyzeContext`, because a content-bearing turn with no input
counter would produce a negative residual and a pressure figure computed from the absent
counter rather than from the model's context. This is why a consent-on plugin session still
shows no prompt content even though its spans carry it. Projecting that content is a
deliberate analysis-layer change, not a one-line filter edit.

## Follow-up 1 — capture real token usage (D5)

Add token usage to the plugin by observing a completion or `session.generate` hook that reports
the finished call's usage, then carry it through `OpencodeHookPayload` → `stampMapperPayload` →
the mapper's existing OpenInference counter path. **Unverified against a live opencode hook:**
the hook name, payload shape, and whether it exposes input/output/cache/reasoning counts are all
unknown from this checkout. Verify the live hook before relying on it.

Constraints:

- Counters come only from what the harness reports. A missing counter stays absent and projects
  `not_measurable` with a reason; it is never defaulted to zero or inferred from content.
- The plugin remains best-effort and never fails the host session; usage capture cannot widen
  the hook delivery budget or break the bounded outbox.
- Pin the round trip with unit tests on the hook payload and the emitted span, and verify a live
  turn end to end before claiming collection.

## Follow-up 2 — per-source last-emitted status (D4)

Add a per-source "last emitted" signal so a silently stopped exporter is visible, instead of
looking the same as one never configured. Candidate surfaces are the read-only capture/status
route, doctor, or `kyberdash kyber capture`; the signal must distinguish never-emitted,
recently-emitted, and stopped-after-emitting, and must never render absence as a measured zero.

Decide where the signal is recorded (an ingest-log per source, a store column, or an existing
checkpoint) before changing a surface, and keep the display-layer rule: the engine computes it,
the tray and web render it over `/api/kyber/*`.

## Follow-up 3 — project captured content on a counter-less turn

Today a turn is charted only when it has a measured input, so content captured with consent on
is stored but never reaches the composition view. Change the analysis contract so a turn can
carry buckets without a usable input counter: keep the residual and pressure `not_measurable`
(or omit them) instead of deriving them from zero input, and count the turn as charted for
content while still reporting token totals as `not_measurable`.

Constraints:

- Never infer an input counter from content, and never publish a negative residual or a
  pressure figure computed against a fabricated zero.
- The change belongs in the analysis layer (`analyzeContext` / `buildSessionRow`), not in the
  adapter or the plugin.
- Pin the counter-less-but-content-bearing case with a test that asserts buckets are present,
  residual/pressure are absent or `not_measurable`, and the token totals stay `not_measurable`.

## Acceptance criteria

- A completed OpenCode call whose hook reports usage produces a canonical record with measured
  input/output counters, and the session payload shows them as measured rather than
  `not_measurable`.
- A call whose hook reports no usage still projects as a visible session whose content is
  `not_measurable` and whose unchartable turns are counted, never as a fabricated measured
  zero.
- A consent-on, counter-less turn whose spans carry prompt or context content renders that
  content as buckets, with residual and pressure absent or `not_measurable` rather than
  computed from a fabricated zero input.
- The capture/status surface reports per source when telemetry last arrived, distinguishes
  never-configured from stopped-after-emitting, and renders no fabricated timestamp for a source
  that never emitted.
- `docs/dash/architecture.md` and `docs/dash/telemetry-inventory.md` are updated to the verified
  hook and status contract, and the todo is archived when both follow-ups land.
