---
id: plans/2026-10-27-kyberdash-otel-harness-coverage
title: "KyberDash OTel harness coverage — project counter-less, content-less model-call sessions honestly"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-27
development-mode: test-first
---

# KyberDash OTel harness coverage — project counter-less, content-less model-call sessions honestly

**Status: Complete 2026-10-27 — archived by its finishing change.**
**Development mode:** `test-first` (the default; no opt-out supplied).
**Autonomy:** `autonomous` (resolved from repository root `AGENTS.md` Config Reg `<autonomy>`).

## 1. Problem and goal

### 1.1 Request (verbatim)

> "The kyberdash is not collecting otel data from any of the harnesses except claude"

Escalated defect from bug-crusher (attempt zero; no fix attempted). The human expectation is
that KyberDash collects OTel from **all** supported harnesses, not only Claude.

### 1.2 What is actually wrong

Investigator found, and this plan verified against code and the live store, that there is
**no Claude-only gate** anywhere in the OTLP ingest path. Attribution is a generic
fingerprint vote over seven adapters (`dash/src/canon/adapters/registry.ts`) and the
receiver is generic (`dash/src/otel/receiver.ts` / `identity.ts` / `writer.ts`). "Only
Claude shows" is the symptom of **three independent causes**, only one of which is a code
defect reachable from this checkout:

- **(a) By-design quarantine.** Kilo/OpenCode's native Effect export
  (`service.name=opencode`, `@effect/opentelemetry`) carries no AI-SDK model spans or usage —
  upstream turns AI SDK span recording off. Those effect spans are quarantined as non-model,
  by design. Documented, not a defect.
- **(b) Projection drops counter-less, content-less model calls — the defect.** The
  dash-CLI-managed opencode plugin emits `llm.invoke` turn/context spans that carry a session
  id and model identity but **no token counters** (the plugin observes request assembly,
  not completion usage) and, with consent off, **no content**. They are ingested as canonical
  records (1,390 `opencode` records on the live store) but `hasEvidence`
  (`dash/src/canon/sessions.ts:850-873`) skips a session whose turns carry no counters and no
  content, so **zero opencode sessions project** and the dashboard shows "no opencode data".
- **(c) Capture/environment.** Copilot, pi, and Antigravity exporters stopped emitting days ago
  (Oct 22-25), and Codex's OTLP path is owner-gated. The ingest path is correct and generic
  (202/202 adapter/ingest/registry tests pass); the gap is that their exporters are not running,
  which nothing can fix deterministically from this checkout.

### 1.3 Goal

1. Fix **cause (b)**: a recognized model-call turn that carries no counters and no content
   builds a **visible session** whose absent dimensions are `not_measurable` with a reason —
   never a silent skip and never a fabricated zero (honest-unobservability R1/R2).
2. Add the missing regression coverage: no test today exercises a counter-less, content-less
   `llm.invoke` through `buildSessions`, so this "ingested but session-invisible" path is uncovered.
3. State, per harness, what "collected" means end-to-end and where each non-Claude harness's
   gap sits (receiver / adapter / projection / capture), so the human's expectation is met with
   honest facts rather than empty UI. Causes (a) and (c) are documented, not faked.

## 2. Decisions

This table is permanent state, preserved across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Intended behavior for a counter-less, content-less `llm.invoke` turn (≥1 turn, empty `content`, no parts): quarantine, zero-token session, or exclude-with-diagnostic? | **Build the session honestly.** A recognized model call is a session; absent counters/content are expressed `not_measurable` with a source-specific reason (never quarantined at ingest, never zero, never silently skipped). | Quarantine would throw away attribution the adapter deliberately made; zero-coercion is forbidden by [honest-unobservability](../../rules/honest-unobservability.md) R2; silent skip is the current bug. Honest projection is the documented projection contract — [architecture.md §"shared canonical projection"](../../dash/architecture.md#the-shared-canonical-projection): "a source that cannot supply content, schemas, structure, or counters produces `not_measurable` rather than a misleading zero." | `autonomous` (covered by authoritative source: honest-unobservability R1/R2 + architecture.md projection contract + `buildSessionRow`'s existing `unmeasuredTurns` handling at `sessions.ts:1362-1372`). |
| D2 | Does dropping the counter/content evidence requirement in `hasEvidence` regress the ambient-telemetry protection (44 fake sessions from HTTP spans)? | **No.** The ambient protection lives in `turnsOf` (`sessions.ts:426-432`): only `op === 'llm.invoke'` non-rollup spans are turns, so HTTP/structural spans (`op` unspecified) never satisfy "a turn exists". The redundant `some(...)` clause is the only thing removed. The existing test "does not build a session from a trace with no model call" (`sessions.test.ts:982`, op `unspecified`) still passes unchanged. | Verified: `turnsOf` returns `[]` for unspecified/structural spans; the rollup branch already requires counters. Only a fingerprint-claimed model call reaches the `llm.invoke` turn set, and that claim IS model evidence. | `autonomous` (covered by code reading of `turnsOf` + `hasEvidence` + the two existing tests at `sessions.test.ts:982,1005`). |
| D3 | Scope of "all harnesses": patch every non-Claude harness or only the code defect? | **Patch only the projection defect** (D1). pi / Copilot / Antigravity / Codex OTLP gaps are capture-side (exporters stopped / owner-gated) — documented honestly, not code-patched; the ingest path is already generic and correct (investigator 202/202 pass). | No code defect exists in the receiver/adapter path for those harnesses; inventing one would be noise. The plan records the per-harness end-to-end matrix in §3.2 as the durable truth, harvested into `docs/dash/telemetry-inventory.md` during closeout. | `autonomous` (covered by investigator evidence + `telemetry-inventory.md` verified-harness rows + ADAPTERS vote code). |
| D4 | Should a capture-side "last emitted N days ago" diagnostic (doctor/status) be added so a silently-stopped exporter is visible? | **Defer as a follow-up todo.** Not part of this defect fix; cheap to add later and orthogonal. | The user cannot today distinguish "stopped exporter" from "not configured"; a status signal is valuable but is scope beyond the one code defect. Recorded as out-of-scope `follow-up` in §8. | `autonomous` (low-confidence, no authoritative source; cheap to reverse). |
| D5 | architecture.md states "counters always flow" for opencode — is the plugin actually capable of emitting token usage? | **Correct the doc.** The plugin envelope carries no counters today: `OpencodeHookPayload` has no `tokens` field and `stampMapperPayload` never sets it (`plugin.ts:66-82,300-328`); usage is only on the native OpenInference path. Capturing real usage (a completion/session.generate hook) is a follow-up, unverified against a live opencode hook. | Verified in code: the T0 `tokens` bucket exists in `mapper.ts`'s type but the plugin never populates it. Doc correction is a governance fix; the capture enhancement is a separate, live-gated todo. | `autonomous` (covered by code reading). |

No hard-block escalations: every open decision had a recommendable option (autonomy checks 1-3 all resolved).

## 3. Investigation findings

### 3.1 Verified code facts (all from `HEAD`)

- `dash/src/canon/adapters/registry.ts:104-128` — `scoreGroup` sums `detect` per span; the
  source name is a group key only, never evidence (R6.2). Confirmed: no Claude-only filter.
- `dash/src/canon/ingest.ts:63-77` — `ADAPTERS = [pi, copilot, antigravity, gemini, claude-code, codex, opencode]`;
  OpenCode registered last, detects nothing it doesn't specifically claim.
- `dash/src/canon/ingest.ts:378-393` — opencode envelope version guard quarantines unknown/
  missing contract versions before the vote; correctly scoped to `opencode.*`.
- `dash/src/canon/sessions.ts:426-432` — `turnsOf`: model-call turns = `op === 'llm.invoke'`
  non-rollup; rollups count as turns only when they carry counters; tool/structural spans never.
- `dash/src/canon/sessions.ts:850-873` — `hasEvidence`: requires a turn AND at least one turn
  with `measuredInput>0 || output>0 || hasParts || (flat content keys>0)`. This is the skip.
- `dash/src/canon/store.ts:3687-3722` — `sessionKeys` groups by `COALESCE(session_id, trace_id)`
  with **no harness filter**; session identity is neutral.
- `dash/src/canon/adapters/opencode.ts` + `opencode-envelope.ts` — the plugin envelope is
  claimed only under contract `'2'`; model/context markers map to `llm.invoke`, tool marker wins;
  counters are **never fabricated** (comment at `opencode.ts:402-406`).
- `dash/src/capture/harnesses/opencode-plugin/plugin.ts:66-82,300-328` — `OpencodeHookPayload`
  has no `tokens`; `stampMapperPayload` sets identity/model/context but never `tokens`. The
  mapper's `tokens` type (`mapper.ts:33-39`) is unreachable from the plugin pipeline.
- `dash/src/analysis/context.ts:455-481` — `analyzeContext` tolerates zero measured turns
  (`hasStructure` false → `{ measurable:false, reason:'no_message_structure' }`), so a zero-token
  session projects without throwing; the only blocker is `hasEvidence`.
- `dash/src/canon/sessions.ts:1362-1372` — `buildSessionRow` already counts `unmeasuredTurns`
  and states the omission rather than hiding it, for content-bearing-but-counter-less turns.

### 3.2 Per-harness end-to-end matrix (OTel adapters)

"Collected" is decomposed into three stages that already exist in the system: **(1) record
ingested** (a canonical record exists in `records`), **(2) session projected** (the record feeds
a `session` row the ASAD payload reads), **(3) dashboard-visible** (the session appears in the
Sessions rail with honest `not_measurable` markers for absent dimensions).

| Harness (OTLP adapter) | Emits today? | Gap layer | Record ingested? | Session projected? | Dashboard visible? |
|---|---|---|---|---|---|
| claude-code | Yes — counters + content (opt-in env) | none (reference) | yes | yes | yes, measured |
| opencode — native Effect (`service.name=opencode`) | Effect app spans only; AI-SDK recording off upstream | by-design quarantine (adapter→`unspecified`→non-model) | quarantined (no records) | no | no — documented, not a defect |
| opencode — plugin (`kyberdash-opencode`) | `llm.invoke` turn/context spans, **no counters**, content only w/ consent | **projection** (`hasEvidence` skip) | **yes (1,390 records)** | **no — the defect** | **no — the defect** |
| pi | stopped (last seen Oct 22-25) | capture/env | when emitting: yes | yes | yes (while emitting) |
| copilot (chat) | stopped (last seen Oct 22-25) | capture/env | when emitting: yes | yes | yes (while emitting) |
| antigravity | stopped (last seen Oct 22-25) | capture/env | when emitting: yes | yes | yes (while emitting) |
| codex OTLP | only when owner points exporters at collector (owner gate) | capture/env | when emitting: yes (allowlist-persisted) | yes | yes (while emitting) |
| gemini | attribution-only survey family; never a stored harness; yields to antigravity | n/a | n/a | n/a | n/a |

Conclusion: exactly one cell is a code defect — the opencode-plugin `session projected`/`dashboard
visible` pair. Every other "no" is quarantine-by-design or a stopped exporter (capture/env) and is
handled as documentation, not code.

## 4. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | `dash/src/canon/sessions.test.ts` (new test in `describe('what counts as a session')`) | `npx vitest run dash/src/canon/sessions.test.ts` (full gate: `npm --prefix dash run test`) | A store holding only a counter-less, content-less `llm.invoke` (opencode envelope shape: `op:'llm.invoke'`, zero tokens, empty `content`, `parts:[]`, `raw` carrying `opencode.contract_version:'2'` + `opencode.model.id` + `opencode.session.id`) builds **one** session (`report.built === 1`); its ASAD payload exposes absent counters/content as `not_measurable`/reason, never a measured zero. | Test committed first, runs RED: `built === 0`, `skipped === 1` under the current `hasEvidence`. | Same test GREEN after the projection change, with `built === 1` and no measured-zero token figure; asserting the honest marker (`context.measurable === false` / availability `not_measurable`) is strengthened, not weakened. |

The existing tests "does not build a session from a trace with no model call" (op `unspecified`)
and "still builds when a model call is present alongside ambient spans" must remain green,
plus the whole `sessions.test.ts` suite and the 202-test adapter/ingest/registry corpus.

## 5. Dispatchable tasks

### T0 — RED contract test (test-dev)
- **Objective:** Add the regression test to `dash/src/canon/sessions.test.ts` per §4's Test contract row; run it and confirm RED.
- **Files:** `dash/src/canon/sessions.test.ts` only (build the opencode envelope-shaped record with the existing `turn`/`tokens` fixtures in `dash/src/canon/fixtures/records.ts`; do not modify fixtures).
- **Acceptance:** `npx vitest run dash/src/canon/sessions.test.ts` fails on the new test with `built === 0` (RED evidence captured), while every pre-existing test in that file stays green.
- **Skills:** test-dev.

### T1 — Project counter-less, content-less model-call sessions (typescript-dev)
- **Objective:** Change `hasEvidence` (`dash/src/canon/sessions.ts:850-873`) so the session-existence test is "a recognized model-call turn exists" (`turnsOf(records).length > 0`), and the counter/content requirement that currently skips a session is removed. Absent counters/content flow through the existing not-measurable machinery (`analyzeContext`'s `no_message_structure`, `buildSessionRow`'s `unmeasuredTurns`) rather than a skip.
- **Files:** `dash/src/canon/sessions.ts`. If removing the `some(...)` clause leaves the `views` parameter unused at `lines 1072/1085`, delete the parameter and update both call sites; do not add suppressions or widen anything else.
- **Acceptance:** the T0 test now passes GREEN; the two ambient tests (`sessions.test.ts:982,1005`) stay green; `npm --prefix dash run test` and `npm --prefix dash run typecheck` and `npm --prefix dash run lint` are green. No test assertion is weakened.
- **Skills:** typescript-dev.
- **Depends on:** T0.

### T2 — Documentation + canonical-claim correction + closeout (docs-dev)
- **Objective:** Governance closeout for the contract change and the doc errata.
  - `docs/dash/architecture.md`: (a) extend the shared-canonical-projection contract to state a recognized model call builds a session even with no counters or content, with those dimensions `not_measurable` (this is the D1 contract); (b) correct the opencode envelope section "counters always flow" claim to "the plugin envelope carries no counters today (request-assembly observation, not completion usage); usage flows only on the native OpenInference path."
  - `docs/dash/telemetry-inventory.md`: OpenCode plugin row — state that counter-less, content-less turns now project as a visible session with `not_measurable` counters/content (previously dropped at projection); consent still gates content; note the plugin captures no token usage (follow-up).
  - Create follow-up todo `docs/todo/opencode-plugin-token-usage-capture.md` (D4 diagnostic + D5 usage capture: i.e. capture real token usage from a completion/session.generate hook, and a per-source "last emitted" status signal) and add its index row in `docs/todo/README.md`.
- **Acceptance:** `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `... -- docs drift .` pass with zero findings (both are also the plan-level gates below).
- **Skills:** docs-dev (use the `kyber-weave-docs` skill for frontmatter).
- **Depends on:** T1.

## 6. Dependency graph

```
T0 (RED test) ──> T1 (projection fix) ──> T2 (docs/todo closeout)
```

T1 consumes T0 (the failing test defines the target). T2 consumes T1 (the landing contract).
No other file scopes overlap. One worker can run T0→T1 sequentially; T2 is a separate skill.

## 7. Risks

- **Over-broad projection.** Removing the `hasEvidence` evidence clause could, in principle,
  surface a session for any future harness that emits a bare `llm.invoke` with no counters and no
  content. Mitigation: a bare `llm.invoke` can only be claimed by a fingerprint-vote adapter that
  recognized model evidence, so such a span is a model call by construction; PR review re-checks.
  Residual risk remains that some future harness emits an `llm.invoke` with no counters and we show
  an "empty" session — but that is the honest outcome (visible + not-measurable), not a regression.
- **Ambient-telemetry regression.** Guarded by `turnsOf`'s `op` filter (§2 D2); covered by the two
  unchanged tests. No change to `turnsOf`.
- **Zero-token session rendering.** A projected opencode session has 0 measured turns; `analyzeContext`
  returns `no_message_structure` (`context.ts:455-481`) rather than throwing, so the payload is a
  `not_measurable` context, not a crash. The GREEN test exercises this end-to-end.
- **Doc drift.** Editing `docs/` requires `docs validate` + `docs drift` at zero findings; T2 runs
  both and the final gates below re-run them.

## 8. Out-of-scope / follow-ups

- **Cause (a)** (Kilo/OpenCode native Effect spans quarantined): by-design, documented; no change.
- **Cause (c)** (pi/copilot/antigravity/codex exporters stopped / owner-gated): capture-side;
  documented in the §3.2 matrix; a per-source "last emitted" status signal is deferred to the T2
  todo (D4).
- **Real opencode token-usage capture**: the plugin emitting actual usage counters
  (completion/session.generate hook) is unverified against a live hook and out of this defect's
  scope; deferred to the T2 todo (D5).

## 9. Verification gates & review

- `npx vitest run dash/src/canon/sessions.test.ts` → GREEN after T1 (RED before it, at T0).
- `npm --prefix dash run test`, `npm --prefix dash run typecheck`, `npm --prefix dash run lint`,
  `npm --prefix dash run check:reachable` → GREEN.
- The display-layer guard is unaffected (no `dash/src/architecture/display-layer.test.ts` change;
  all changes are in the engine `dash/src/**, dash/web/**, dash/tray/**` boundary).
- `docs validate .` and `docs drift .` → zero findings.
- Whole-change review via the code-review council before merge, per repository gates.

## 10. docs-dev closeout mapping

| Durable decision/invariant | Destination |
|---|---|
| D1 (counter-less, content-less model-call session projects with `not_measurable`) | `docs/dash/architecture.md` §"The shared canonical projection" (recognized model call builds a session; absent dimensions stated, not fabricated; content-bearing counter-less limitation); `docs/dash/telemetry-inventory.md` OpenCode plugin row; no ADR — refines the existing projection/ASAD contract and honest-unobservability rule (ADR 0011 / rule) with no new boundary, schema, dependency, or public surface. |
| D2 (ambient-telemetry protection: `turnsOf` unchanged; unattributed turns keep the evidence test) | `docs/dash/architecture.md` §"The shared canonical projection" (unattributed turn with counters still builds, bare ambient stays non-session, HTTP/structural spans never become turns); no ADR — the protection is an existing `turnsOf` invariant this change leaves untouched. |
| D3 (scope: patch only the projection defect; per-harness end-to-end matrix is durable truth) | `docs/dash/telemetry-inventory.md` §"Per-harness OTel collection matrix" (ingested → projected → visible, native Effect quarantine, capture-side stops for pi/Copilot/Antigravity/Codex); no ADR — capture-side gaps are environment facts, not an architectural boundary. |
| D5 (plugin carries no counters; usage only on native OpenInference) | `docs/dash/architecture.md` opencode envelope section correction + `docs/dash/telemetry-inventory.md`; follow-up todo `docs/todo/opencode-plugin-token-usage-capture.md`. |
| D4 (capture-side "last emitted" diagnostic + usage capture) | follow-up todo `docs/todo/opencode-plugin-token-usage-capture.md`. |

No ADR — ADR waiver: this fixes a projection defect and corrects a doc claim within the existing
session-projection, ASAD, and honest-unobservability contracts; nothing new is boundary-, schema-,
dependency-, or surface-forming. No documentation waiver (canonical docs are updated in T2).

## Discovery report

- codegraph MCP: **unavailable** (no `codegraph_explore` tool exposed in this harness; `.codegraph/`
  index exists but was not queried). docs MCP (`docs_explore`/`docs_glossary`): **unavailable** (no
  Kyber-Weave MCP tools exposed). Falls back used: direct `read`/`grep` over the named source files
  and the canonical docs.
- Direct reads: 17 (sessions.ts ×4, ingest.ts ×2, registry.ts, store.ts, adapters/opencode.ts,
  opencode-envelope.ts, mapper.ts, turn.ts, plugin.ts, hook-catalog.json, telemetry-inventory.md,
  architecture.md ×2, honest-unobservability.md, sessions.test.ts, fixtures/records.ts, context.ts,
  plan index README) — all reads of indexed code were direct because the MCP tools were unavailable.
- Live-store evidence is the investigator's read-only `sqlite3` snapshot (records.harness
  claude-code=3692, opencode=1390; session claude-code=74, opencode=0; ingest_log `opencode` 5.5M
  quarantined), accepted as evidence here and reconciled against code in §3.