---
id: plans/2026-10-28-opencode-telemetry-fill
title: "OpenCode telemetry fill — make an enabled OpenCode harness send measured data to KyberDash"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-28
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberDash]
---

# OpenCode telemetry fill — make an enabled OpenCode harness send measured data to KyberDash

**Status: Complete 2026-10-28 — archived by its finishing change.**

**Development mode:** `test-first` (default; no opt-out supplied). **Autonomy:** `autonomous`
(resolved from repository root `AGENTS.md` Config Reg `<autonomy>`; supplied in this run's packet).
Recorded next to each other as required.

## 1. Problem and goal

### 1.1 Request (owner requirement, verbatim)

> "when that toggle says enabled for opencode the content flows"
> "opencode enabled on that screen all diagnostic information to fill the dashboard is sent"

The Settings OpenCode toggle says **Enabled**, but the dashboard tiles stay honest-empty. The
owner wants an enabled OpenCode harness to actually send the diagnostic information that fills
the dashboard — token usage, tool calls/invocations, tools offered, system prompt, skills, and a
cost basis — instead of honest-empty tiles.

### 1.2 Problem (established evidence, this checkout)

1. The shipped plugin registers **one** hook, `session.context`
   (`dash/src/capture/harnesses/opencode-plugin/entry.ts:241`). Six lifecycle kinds are defined
   (`plugin.ts:44-51`) and built (`plugin.ts:187-194`) but never registered — dead code in the
   bundled file. The payload is identity + optional content; with consent off, content is dropped
   before emit (`entry.ts:195`).
2. The mapper **can** map token counters (`mapper.ts:36-41`, `:181-199` →
   `llm.token_count.*`) but no registered hook populates `tokens`; `OpencodeHookPayload` has no
   `tokens` field. Tool/turn-outcome fields (`turn.ts:50-66`) are likewise never filled.
3. OpenCode 2.1.4 exposes **no** usage/cost/token hook and no span/turn lifecycle. Un-pinned
   candidates only: `session.http.response` / `session.model.request` / `session.generate` (raw
   provider response) and `tool.execute.after`. The v1 span-payload lifecycle assumption does not
   hold on 2.1.4 (`docs/dash/telemetry-inventory.md` §"T0 pin").
4. Live store proof: 1,396 plugin records carry **zero** usage, `cost {unknown, no_rate}`,
   `duration_ms 0`, empty `models`; only 6 carry content keys.
5. A dormant rich path exists: the `codeburn/opencode` file/DB import
   (`dash/src/providers/opencode.ts` → `opencode-file-parser.ts` + `sqlite-session-parser.ts`)
   parses `~/.local/share/opencode/storage/session/*.json` + `opencode.db`. It captured REAL
   tokens and priced cost once (7 sessions, one day) then stopped. The native model-span stream
   (`experimental.openTelemetry: true` + `OTEL_EXPORTER_OTLP_ENDPOINT` → loopback 4318) stopped
   after 2026-10-26. The consent file `~/.kyberdash/opencode-plugin.json` holds `content: false`;
   the Settings panel exposes no content toggle (separate known defect, out of scope except where
   consent state gates verification).
6. Projection is faithful (empty in → honest-empty out), but the adapter never resolves a model id
   on the record: `MODEL_KEYS = ['gen_ai.response.model', 'gen_ai.request.model', 'model']`
   (`canon/sessions.ts:47`) does not include the OpenInference `llm.model_name` or the envelope
   `opencode.model.id`, so every record is priced `no_rate` with an empty `models` set even when
   the span carries the model and the counters
   (`canon/adapters/opencode.ts:426-452`, `applyEnvelope` sets session/op/content but no model).

### 1.3 Goal

Restore measured data end to end (Phase A), then build the missing capture (Phase B), prove it
(Phase C), and document it (Phase D), so an enabled OpenCode harness fills the dashboard with
measured token usage, tool invocations, tools offered, system prompt, skills, model, and a cost
basis — with every absent dimension staying honestly `not_measurable`, never a fabricated zero.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Consent treatment under "Enabled means everything": consent-on as part of Enabled, or a separate step? | **Consent-on is part of Enabled** — no separate step. | The engine already implements this contract: `enableFilePlugin` sets consent `content: true` after a successful strict install (`capture/index.ts:874`), `disableFilePlugin` sets `content: false` first (`:887`), and `docs/cli-command-surface.md:348-349` document it ("`capture enable` … sets consent `content: true`"; "`capture disable` … sets consent `content: false`"). The owner's `content: false` file is consistent with install-by-`squad install/update` (preserve mode creates the file with `content: false` only when absent, never flipping an existing value). The plan verifies the Settings Enabled toggle routes through `capture enable` and, if it does not, that routing gap is the separate Settings content-toggle defect (deferred). No new consent step is introduced. | ANSWERED — `autonomous: recommendation accepted` (approve-and-execute 2026-10-28) |
| D2 | Phase A path choice: native model-span stream vs dormant file/DB import vs both — which re-establishes measured tokens/cost with the smaller change? | **Native stream first, import as durable batch backfill** (both, sequenced). | Native stream is the smaller change: the adapter already maps OpenInference model spans (`llm.token_count.*`, `openinference.span.kind`, `llm.model_name`, `tool.name`) — the live span evidence in `canon/adapters/opencode.ts:12-18` proves it worked. Its two blockers are small: (a) the endpoint env var is process-env-only and not persisted (the stream stopped when OpenCode relaunched without it), and (b) the model id is never resolved (§1.2, item 6). The import is batch-only and its "7 sessions/one day" root cause (token source moved off the `session`/`message.data` shape) needs a live-store probe that cannot complete without a live OpenCode run. Sequencing both bounds the cost of being wrong: the adapter→projection seam is shared, so fixing it once serves both, and the fixture round trip (C1) proves the seam independent of which source feeds it. If the native stream proves unrestorable on the owner's build, the import is the fallback that still yields measured tokens/cost. | ANSWERED — `autonomous: recommendation accepted` (approve-and-execute 2026-10-28) |
| D3 | Live-turn model/funding for the Phase C manual gate. | **Cheapest funded, rate-published model, owner-operated, never a merge gate.** | The manual gate is one turn on a small rate-published model (Haiku- or Flash-class — e.g. `claude-haiku-4-5` or `gemini-3-flash`, both present in `pricing/models.ts` aliases), funded and operated by the owner on macOS. CI stays deterministic: the fixture e2e (C1) is the merge gate; the live turn is the acceptance that matters to the owner and is recorded as `[NOT LIVE-VERIFIED]` if no funded model is available at execution time, matching the existing deferred `docs/todo/kyberdash-macos-funded-live-model-turn.md`. | ANSWERED — `autonomous: recommendation accepted` (approve-and-execute 2026-10-28) |
| D4 | Scope line for tool-invocation capture if the runtime proves thin. | **Tool lifecycle only via `tool.execute.after`; usage stays on the native stream.** | Capture tool name + success + duration + result size through `tool.execute.after`, mapping into the existing `turn.ts` `tool_result` log path. Do NOT capture usage from the plugin's unpinned `session.generate`/`session.model.request` hooks: the native OTel stream already counts tokens, and both running would double-count. The double-count guard is "the plugin never emits usage while the native OTel flag is on"; plugin-side usage is a probe-gated fallback, emitted only when the native OTel export is confirmed OFF at runtime. If the runtime proves thin (no `tool.execute.after`), degrade to tool-name-only from the already-captured `toolsText`/`session.context` and record the gap. | ANSWERED — `autonomous: recommendation accepted` (approve-and-execute 2026-10-28) |
| AP1 | Approve-and-execute gate for this Draft plan | **Approved — the plan moves to Ready and is executable.** | Conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous` on 2026-10-28; scale (`extends-component`, `impact-components: [KyberDash]`) and `test-first` mode confirmed. No task executes until the conductor dispatches it. | `autonomous: recommendation accepted` |
| RP1 | Closeout-gate repair (C3/C4/C6) on the archived plan | **Reshape the closeout sections to the gate vocabulary.** Make the Decisions / Design harvest / Closeout mapping headings unnumbered and exactly named; give the Design harvest table an `ID` column (H1–H4); add a Closeout mapping table with `ID` + `Destination` columns mapping every decision (D1–D4, AP1) and harvest (H1–H4) id to a resolving relative link or a Documentation waiver (C7); fix the stray `D6` cross-reference to `§1.2`. Decision content and every `autonomous: recommendation accepted` provenance row are preserved byte-for-byte in meaning. | `docs validate . --merge-ready` reported `KW-DOC-LIFECYCLE-004` (C3/C4/C6) naming this file; the repair clears C3/C4/C6/C7/C8 without altering any decision and needs no out-of-file edit, because `docs/dash/architecture.md` and `docs/dash/runbook.md` already carry `component: KyberDash` with `last-reviewed` 2026-10-28 (C8). | `autonomous: recommendation accepted` |

No hard-block escalation: every decision has a stand-behind recommendation, and the expensive
choices (D2, D4) are each bounded by a probe task and the source-independent fixture.

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberDash]`.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberDash | architecture | `docs/dash/architecture.md` — plugin hook registration set (context + tool lifecycle), the usage-probe + double-count guard rule, model-id resolution on the OpenCode record (`opencode.model.id` / `llm.model_name` → `model`), adapter claim rules for the new tool/usage markers. |
| H2 | KyberDash | reference | `docs/dash/telemetry-inventory.md` — updated OpenCode rows (which dimensions are measured end-to-end vs `not_measurable`), the collection-mechanism hierarchy for the restored native stream + import. |
| H3 | KyberDash | runbook | `docs/dash/runbook.md` — OpenCode enable → measured-data procedure (enable sets consent on; endpoint env var; restart; live-turn verification step), import restore behavior and its refresh window. |
| H4 | KyberDash | reference | `docs/cli-command-surface.md` — changed enable/import behavior surfaced by Phase A/B (consent-on-already-documented; import window/restore note if behavior changes). |

No new boundary, schema, dependency, or surface is introduced: token/tool/content flow through the
existing plugin envelope, OpenInference adapter, `tool_result` log path, and `MODEL_KEYS`
projection seam. **ADR waiver:** refines the existing ADR 0009/0016/0031/0033 capture and
projection contracts with no new boundary, dependency, or schema — the same waiver basis the
prior opencode plans used (`docs/archive/plans/2026-10-22-opencode-span-turn-hooks-kyberdash.md`,
`2026-10-25-opencode-capture-deploy-kyberdash.md`).

## Closeout mapping

| ID | Destination |
|---|---|
| D1 | [dash/architecture.md](../../dash/architecture.md) — consent-on-as-Enabled (consent file contract, §"OpenCode plugin span/turn envelope"); [cli-command-surface.md](../../cli-command-surface.md) — `capture enable`/`disable` consent. |
| D2 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — restored native model-span stream + file/DB import collection mechanism. |
| D3 | [todo/kyberdash-macos-funded-live-model-turn.md](../../todo/kyberdash-macos-funded-live-model-turn.md) — the funded live-model turn stays owner-operated and deferred there. |
| D4 | [dash/architecture.md](../../dash/architecture.md) — plugin hook registration set + double-count guard. |
| AP1 | **Documentation waiver — transient approval record:** the approve-and-execute provenance is preserved in this plan's Decisions section. |
| RP1 | **Documentation waiver — transient repair record:** closeout-gate reshaping of this archived file only. |
| H1 | [dash/architecture.md](../../dash/architecture.md) — plugin hook set, model-id resolution, adapter claim rules. |
| H2 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) — OpenCode measured-vs-`not_measurable` rows. |
| H3 | [dash/runbook.md](../../dash/runbook.md) — enable → measured-data procedure. |
| H4 | [cli-command-surface.md](../../cli-command-surface.md) — enable/import behavior. |

## 4. Investigation findings

All files read on this checkout; CodeGraph index present and used for blast radius before direct
reads (see §12 Discovery).

- **F1 — one registered hook.** `entry.ts` `createSetup` calls only `session.hook('context', cb)`
  (`:241`). `plugin.ts` defines `OPENCODE_PLUGIN_HOOK_KINDS` (`session/turn/span` × start/end,
  `:44-51`) and builds their handlers (`:187-194`), and `registerOpencodePlugin` attaches them
  (`:205-235`), but the entry never calls `registerOpencodePlugin` — the lifecycle set is dead
  code in the bundle. Only `session.context` reaches the receiver.
- **F2 — mapper/turn ready, payload thin.** `mapper.ts` converts `OpencodePluginSpanTokens` onto
  `llm.token_count.*` (`:181-199`) and `turn.ts` models a `tool_result` outcome (`:50-66`) with a
  correlation + fallback join (`findOpencodeTurnTarget`, `:226-253`). Neither is populated:
  `OpencodeHookPayload` has no `tokens` and `stampTurnOutcome` (`plugin.ts:344-353`) leaves outcome
  fields unset. The plumbing is already unit-pinned (`mapper.test.ts`, `turn.test.ts`).
- **F3 — no usage hook on 2.1.4.** `docs/dash/telemetry-inventory.md:841-856` pins the v2
  namespaced surface (`session.prompt`, `session.context`, …) but no usage/cost/token boundary and
  no span/turn lifecycle. `docs/todo/opencode-plugin-per-message-tokens.md` confirms tokens stay on
  the OTel path and warns the `event` hook is unpinned.
- **F4 — native stream stopped operationally.** `harnesses/opencode.ts:63-71` declares the OTel
  flag (`experimental.openTelemetry`) as the only written key; the endpoint is
  `OTEL_EXPORTER_OTLP_ENDPOINT`, process-env-only, reported on status but never written (upstream
  #13438; non-model span volume noted). The stream that produced the live `616/159744/242/145`
  span evidence (`canon/adapters/opencode.ts:12-18`) stopped after 2026-10-26 — consistent with
  OpenCode relaunched from a shell lacking the env var, or a reinstall resetting the flag. Fix is
  guidance + model-id placement, not a new writer (writing a shell rc stays forbidden per the
  declared contract).
- **F5 — model never resolved.** `MODEL_KEYS` (`canon/sessions.ts:47`) omits `llm.model_name`
  (OpenInference native) and `opencode.model.id` (plugin envelope). `applyEnvelope`
  (`canon/adapters/opencode.ts:426-452`) sets session/op/content but no model; `baseRecord`
  (`copilot.ts:666-694`) carries no `model` field — the projection reads it from `record.raw` via
  `attributeOf` (`sessions.ts:275-285`). Result: `cost {unknown, no_rate}`, empty `models` for all
  1,396 plugin records and every native span.
- **F6 — import token source.** The import reads tokens from assistant `message.data`
  (`tokens`/`usage`/flat keys, `session-message.ts:114-120`) with a **fallback** to the `session`
  table's `cost/tokens_*` columns that fires only when `yieldCount === 0`
  (`sqlite-session-parser.ts:333-363`). Both are the pre-migration OpenCode shapes; "7 sessions,
  one day" is consistent with the token columns/fields ceasing to be written after the 2.1.4
  migration. The refresh also slices to `utcHistoryWindow(commandStartedAt, DEFAULT_HISTORY_WEEKS=2)`
  (`refresh/orchestrator.ts:42,71-73`). A live-store probe is required to confirm the current
  token location — see A2.
- **F7 — consent contract already "Enabled means everything".** `enableFilePlugin`
  (`capture/index.ts:851-875`) = strict install + consent `content: true`; `disableFilePlugin`
  (`:879-902`) = consent `content: false` then uninstall. `plugin install --preserve` creates the
  consent file `content: false` only when absent (`cli-command-surface.md:350`). The owner's
  `content: false` is consistent with install (not enable) having created it.

## 5. Test-first contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| A1 | `dash/src/canon/adapters/opencode.test.ts` (+ `opencode-envelope.test.ts`) | `npm --prefix dash run test -- opencode` | A native OpenInference span (`llm.model_name` + `llm.token_count.*`) and an envelope span (`opencode.model.id`) project a record whose model resolves to a priced cost basis; a span with no model id stays `no_rate`. | New test asserting model-resolution + priced cost fails today (model unresolved → `no_rate`, empty `models`). | Same test passes without weakening; `attributeOf`/adapter returns the model; cost path prices when a rate exists. |
| A2 | `dash/src/providers/opencode.test.ts`, `opencode-file.test.ts` (+ a new sqlite-token fixture) | `npm --prefix dash run test -- opencode` | A fixture `opencode.db`/JSON with the current token shape yields a measured call; a schema-shifted fixture yields honest `not_measurable` (never zero). | New fixture-driven test fails against the dormant parser (0 tokens on the migrated shape). | Same test passes; parser reads the current token location or reports the exact probe procedure for a live-store confirmation. |
| B1 | `dash/src/capture/harnesses/opencode-plugin/{plugin,entry,mapper,turn}.test.ts`, `bundle.test.ts`, `canon/adapters/opencode.test.ts` | `npm --prefix dash run test -- openencode-plugin opencode` | Registered `tool.execute.after` maps to a `tool_result` log with name/success/duration; usage emitted only when native OTel confirmed off (double-count guard); the committed bundle matches a fresh bundle. | New tests fail: entry registers no tool hook, payload has no tool/outcome, bundle drift after entry/plugin edit. | Same tests pass; guard asserts no usage emitted under the native-OTel-on condition; `bundle.test.ts` green (bundle regenerated + committed). |
| C1 | `dash/e2e/harness-collection-world.ts` + `harness-collection.spec.ts` | `npx playwright test dash/e2e/harness-collection.spec.ts` | A fixture-driven opencode round trip (hook payload → mapper → adapter → projection) renders measured dimensions; the existing three conformance tests stay byte-for-byte green. | New measured-opencode scenario absent; existing 3 tests green before any change. | 4 scenarios green; existing assertions unchanged (no weakening); new hook tests use injected fetch (network guard). |
| D1 | `docs validate` / `docs drift` (indexed corpus) | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` and `docs drift .` | Updated telemetry-inventory/architecture/runbook/cli-command-surface reflect the verified behavior; corpus stays at zero findings. | N/A (docs-only; validated after edits). | Both checks exit 0; no new `KW-*` ids; no ontology widening. |

## 6. Dispatchable tasks

Skills named, not owners: `csharp-dev` is not used (this is TypeScript under `dash/`); the tasks
use `typescript-dev` (engine/plugin), `test-dev` (fixtures/tests), and `kyber-weave-docs` (D1).

### A1 — Restore native stream + place the model id (smallest change)
- **Objective.** Make native OpenInference spans and plugin envelope spans resolve a model id, so
  measured counters price to a cost basis; make the endpoint/flag guidance actionable so a
  relaunched OpenCode keeps streaming.
- **Files.** `dash/src/canon/adapters/opencode.ts` (adapter-scoped model stamp), `dash/src/canon/adapters/opencode-envelope.ts` (only if a marker key is needed), `dash/src/capture/harnesses/opencode.ts` (status/manual-step guidance text; no new keys, no shell-rc write), `dash/src/capture/status.ts` (manual-step line if needed).
- **Mechanism (preferred).** In `opencodeAdapter.normalize`, when the envelope names `opencode.model.id` or the span carries `llm.model_name`, stamp the resolved id onto a **fresh copy** of `record.raw` under the existing `model` key (`record.raw = { ...raw.attributes, model }`) so the shared `MODEL_KEYS`/`attributeOf` resolution and the cost path pick it up unchanged, without widening `MODEL_KEYS` for other harnesses. (Alternative recorded: extend `MODEL_KEYS` with `opencode.model.id` + `llm.model_name`; rejected as primary because both are only plausibly OpenCode's, but the shared-key widen is the fallback.)
- **Acceptance.** RED→GREEN per §5 A1; no change to `detect`/`relevance` scoring; existing opencode tests stay green; the live span evidence convention (cache-EXCLUSIVE) is untouched.

### A2 — Restore the dormant file/DB import token source
- **Objective.** Determine why the import captured 7 sessions on one day and re-establish token/cost capture from the current `opencode.db` schema; record the exact live probe procedure where a live run is required (no model calls are run by any agent in this plan).
- **Files.** `dash/src/providers/sqlite-session-parser.ts` (current token location; the `yieldCount === 0` fallback), `dash/src/providers/opencode-file-parser.ts`, `dash/src/providers/opencode.ts`, `dash/src/refresh/orchestrator.ts` (window only if the 2-week slice is implicated — confirm before touching).
- **Investigation.** (a) Confirm against the live `~/.local/share/opencode` tree whether token fields live on `message.data` (`tokens`/`usage`/flat), on the `session` table rollup, or on a newer table (read-only probe; report the exact `sqlite3`/file procedure if the store is absent in CI). (b) Confirm the 2-week UTC window is not silently dropping the sessions the owner saw. (c) Fix the parser to read the current shape; a schema-shifted store reports `not_measurable`, never zero.
- **Acceptance.** RED→GREEN per §5 A2; the parse never fabricates a zero; findings recorded in this plan's Investigation section on completion.

### B1 — Register the missing plugin hooks; map tool lifecycle + usage; claim in the adapter; rebundle
- **Objective.** Register `tool.execute.before/after` (probe `session.http.response`/`session.model.request`/`session.generate` for usage with the D4 double-count guard); map tool name/success/duration/result-size into the payload → `turn.ts` `tool_result` log; extend the adapter to claim the new markers; regenerate and commit the bundle.
- **Files.** `entry.ts` (register the tool hook next to `session.context`), `plugin.ts` (payload + stampers), `mapper.ts` (tool marker already at `:174-179`; add outcome fields if needed), `turn.ts` (outcome population), `canon/adapters/opencode-envelope.ts` + `canon/adapters/opencode.ts` (claim new markers), `dash/scripts/bundle-opencode-plugin.mjs` + `generated/plugin-source.ts` (regenerate + commit; `bundle.test.ts` stays green).
- **Constraint.** The plugin remains best-effort and never fails the host; usage capture does not widen the 250 ms delivery budget or break the bounded outbox; the double-count guard (D4) is a unit-pinned assertion.
- **Acceptance.** RED→GREEN per §5 B1; leaves test (`dash/src/architecture/opencode-leaves.test.ts`) stays green — `mapper.ts`/`turn.ts`/`entry.ts` gain no value import beyond the existing leaves.

### C1 — Prove: extend e2e conformance + network guard; write the live-turn gate procedure
- **Objective.** Add a fixture-driven measured-opencode round trip to the e2e conformance spec without weakening the existing three tests; extend the network guard (fake `fetchFn` injection) to the new hook tests; write the owner-operated live-turn gate procedure.
- **Files.** `dash/e2e/harness-collection-world.ts` (new measured-opencode scenario through the real adapter/projection, distinct from the existing `opencode` honest-empty seed), `dash/e2e/harness-collection.spec.ts` (fourth scenario), new hook test guards.
- **Live gate (procedure only — not executed by an agent).** One turn on the owner's cheapest funded rate-published model (D3): enable OpenCode in Settings (`capture enable` → flag + plugin + consent on), ensure `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318` in the launching shell, restart OpenCode, run one turn, and confirm the dashboard shows measured tokens/cost/tools/content. Record `[NOT LIVE-VERIFIED]` if no funded model is available; the fixture e2e remains the merge gate.
- **Acceptance.** 4/4 e2e scenarios green; existing three assertions unchanged; no real network in any new test.

### D1 — Docs closeout
- **Objective.** Update `docs/dash/telemetry-inventory.md` (OpenCode rows), `docs/dash/architecture.md` (§"OpenCode plugin span/turn envelope"), `docs/dash/runbook.md` (§2 OpenCode capture plugin), `docs/cli-command-surface.md` (enable/import behavior) to the verified contract; run both docs checks to zero.
- **Acceptance.** `docs validate .` and `docs drift .` exit 0; no new `KW-*` ids; no ontology widening; plan index row + this plan's closeout mapping completed at finalization.

## 7. Dependency graph

```
A1 ──┐
     ├──► B1 ──► C1 ──► D1
A2 ──┘            (C1 also needs A1's measured projection)
```

- A1 and A2 are independent (canon adapter vs providers — disjoint files) and run in parallel.
- B1 depends on A1: both edit `canon/adapters/opencode.ts` (model stamp vs new-marker claim), a
  real file overlap, and B1 consumes A1's settled model-resolution.
- C1 depends on B1 (exercises the new hook→mapper→adapter→projection path) and A1 (measured
  projection).
- D1 depends on B1 (documents the verified hook/adapter contract); D1 and C1 may run in parallel
  with D1's docs edits scoped to the doc files only.
- `review.max-concurrent-workers` default 4; A1+A2 fill two slots, then B1, then C1+D1.

**PR #455 relationship.** This checkout is on the `feat/kyberdash-harness-collection-standard`
branch (open draft PR #455, which introduced `dash/e2e/harness-collection.spec.ts` +
`harness-collection-world.ts`). This plan **extends** that pattern in place and does not depend on
#455's merge. If #455 rebases or merges before execution, rebase this plan's C1 edits onto it and
preserve its three GREEN scenarios ("do not weaken").

## 8. Risks, out-of-scope boundaries, verification gates, review

**Risks.**
- Un-pinned 2.1.4 hooks (D4): mitigated by a bounded probe with a thin-runtime degradation
  (tool-name-only) and by keeping usage on the native stream (no double count).
- Native stream unrestorable on the owner's build: mitigated by the parallel import path (A2) and
  the source-independent fixture (C1).
- Live gate needs funding: never a merge gate; recorded `[NOT LIVE-VERIFIED]` otherwise.

**Out of scope (deferred, with reason).**
- Per-source "last-emitted" status signal (`docs/todo/opencode-plugin-token-usage-capture.md`
  Follow-up 2): distinct signal design, not needed to fill the tiles; deferred.
- Missing Settings content toggle (separate defect): out of scope except where consent gates C1's
  live verification (D1 keeps consent-on-as-Enabled).
- CI macOS funded-turn job (`docs/todo/kyberdash-macos-funded-live-model-turn.md`): already
  deferred; C1's manual gate references it, does not re-open it.
- OpenCode 1.x runtime support and per-message `event`-hook tokens
  (`docs/todo/opencode-1x-runtime-support.md`, `opencode-plugin-per-message-tokens.md`): unchanged.

**Verification gates.** `npm --prefix dash run typecheck`, `npm --prefix dash run lint`,
`npm --prefix dash run test`, `npm --prefix dash run check:reachable`, plus the C1 Playwright
spec; then `docs validate .` and `docs drift .`. Display-layer rule: no logic in `dash/web` or
`dash/tray`; `dash/src/architecture/display-layer.test.ts` stays green.

**Review.** Whole-change `code-review` at the PR boundary (as the prior opencode plans did);
per-task `task-gate` before each handoff.

**docs-dev closeout.** At finalization, map D1–D4 and the Design harvest rows to their canonical
destinations (telemetry-inventory, architecture, runbook, cli-command-surface) and record the ADR
waiver.

## 9. Discovery report

`Discovery: codegraph 3, docs 0, direct reads 14` — see §12 for the DIRECT_READ_REASONS.

## 10. References

- `docs/dash/telemetry-inventory.md` (OpenCode rows; §"T0 pin" lines 811-910)
- `docs/dash/architecture.md` §"OpenCode plugin span/turn envelope" (line 299+)
- `docs/dash/runbook.md` §2 OpenCode capture plugin (lines 1314-1378)
- `docs/cli-command-surface.md` capture verb rows (lines 347-350)
- `docs/todo/opencode-plugin-token-usage-capture.md`, `opencode-plugin-per-message-tokens.md`,
  `kyberdash-macos-funded-live-model-turn.md`, `opencode-1x-runtime-support.md`
- `docs/archive/plans/2026-10-25-opencode-capture-deploy-kyberdash.md`,
  `2026-10-22-opencode-span-turn-hooks-kyberdash.md`,
  `2026-10-27-kyberdash-otel-harness-coverage.md`
