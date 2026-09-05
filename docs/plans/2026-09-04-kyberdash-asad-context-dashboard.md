---
id: plans/2026-09-04-kyberdash-asad-context-dashboard
title: ASAD Context Dashboard on the Canonical Store
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-04
component: KyberDash
---

# ASAD Context Dashboard on the Canonical Store

**Status:** Review required
**Date:** 2026-09-04
**Goal:** Make the agent-session-analysis-dashboard (ASAD) view the only view on the KyberDash Context page, for every harness, rendered from `canon.db` rather than from the Python pipeline's `sessions.db` or hard-coded data.

Successor to [2026-09-03-kyberdash-agent-session-analysis-integration.md](../archive/plans/2026-09-03-kyberdash-agent-session-analysis-integration.md), which is complete and archived. That plan established the dual-database bridge; [ADR 0008](../adr/0008-kyberdash-single-canonical-store.md) retired it, and doing so removed the hard-coded path the ASAD view was being served from. This plan restores the view on the canonical store.

---

## 1. Problem / Motivation

1. **The ASAD view is gone from the Context page.** It was rendered from a hard-coded absolute path to `sessions.db` in another checkout. Removing that path — correct in itself, and required by ADR 0008 — left the page falling back to a table view that does not answer the question the page exists to answer.
2. **The canonical store cannot yet serve the view.** `canon.db` holds records and a derived `session` table, but the session payload is a shape invented for this codebase, not the shape the ASAD front-end reads. The store's contract has to be the ASAD payload, not a translation of it.
3. **The data behind the view is incomplete for most harnesses.** Per-server tool schemas — the input the tool-cost view ranks on — reach us from no harness at all today. Content buckets are populated for the Gemini statusline and Antigravity only.
4. **Roughly 60 percent of the corpus is not model calls.** 29,627 of 54,592 records are `GlobalHttpApi.health` and `http.server GET` spans, which manufacture sessions that never happened.
5. **Attribution is wrong for the largest source.** The Gemini statusline sets `gen_ai.system = "gemini"`, but the Gemini adapter detects a `gemini.` namespace rather than that attribute value, so 18,905 records are claimed by the Copilot adapter. Token convention coincides, so the figures are right and the label is wrong.

## 2. Approved decisions

- **D1 — ASAD is the only Context view.** The ASAD view renders on the Context page for every session, of every harness. The existing TreeTable, SessionDetails and the current `AgentSessionDashboard` sections are deleted, not hidden behind a toggle. No second view is offered.
- **D2 — Everything on the ASAD dashboard ships.** All six views, plus the caveat banners and the drill-down drawer. Not a subset.
- **D3 — ASAD's information architecture, KyberDash's styling.** The layout, view order and captions come from ASAD; the visual language is KyberDash's.
- **D4 — One store.** `canon.db`, populated from both the OTLP receiver and the harness dot-folders. If the data cannot support the view, the data layer is fixed rather than the view degraded.
- **D5 — The ASAD payload is the store's contract.** The session projection emits the ASAD shape directly. No adapter layer between store and view.
- **D6 — Every harness is in scope.** Claude Code, Antigravity/agy, Copilot (extension, CLI, coding agent), Codex, pi, opencode, Cursor and Kilo. A harness we cannot yet collect from is recorded as *not measurable with a reason*, never as absent or zero.
- **D7 — Source precedence.** Counters from the OTel row; content from the file row when the OTel row has no parts; never summed across sources for one turn. Recorded as [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md) decision 4.
- **D8 — Serve `/v1/logs`, merging into one record.** A log record enriches the record it belongs to; it never inserts a parallel one. Recorded as ADR 0009 decisions 1-3.

## 2a. Open questions (decision ledger)

| Q# | Question | Options | Recommended | Depends on | Status |
|---|---|---|---|---|---|
| — | No open implementation-blocking questions. D1-D8 are the execution contract. | — | — | — | ANSWERED |

## 3. Investigation findings

The full per-harness survey, with every claim marked verified, documented, unverified or assumed, is [docs/dash/telemetry-inventory.md](../dash/telemetry-inventory.md). The findings that shape this plan:

- **Copilot Chat is already exporting to our collector.** `github.copilot.chat.otel.enabled: true`, `otlpEndpoint: http://127.0.0.1:4318`, `protocol: http/protobuf` — in both VS Code stable and Insiders settings. `captureContent` is `false`. That single flag is the cheapest route to prompt, response and tool content we have.
- **Claude Code's schemas are on the logs signal.** `OTEL_LOG_RAW_API_BODIES=1` emits `claude_code.api_request_body` carrying the full request JSON: system prompt and tool definitions with schemas. Unreachable while the receiver serves traces only.
- **Cursor emits no traces.** Its official export is metrics and logs, Enterprise-only, HTTPS-only, explicitly without prompt content. Its usable path is agent hooks, which already run on this machine (four gitleaks hooks in `~/.cursor/hooks.json`), producing one trace per agent turn with token counts and the tool sequence.
- **pi has one real emitter, not the configured one.** `~/.pi/agent/settings.json` loads the ASAD pi collector from source; `observme.yaml` configures `@senad-d/observme`, whose package directory is empty and which is absent from `npm/package.json`. No pi records exist because pi has not run since the collector took port 4318.
- **opencode emits nothing today.** `experimental.openTelemetry` is `false`; its one plugin is a model-callable tool that prints to the transcript. Native OTel support exists behind that flag.
- **Kilo is dark.** Installed in four versions, with `globalStorage/kilocode.kilo-code/` empty in both stable and Insiders. Its OTel parameters are not in public documentation.
- **Copilot CLI already computes ASAD's taxonomy on disk** — `context_system_tokens`, `context_conversation_tokens`, `context_tool_definitions_tokens`, `context_mcp_tools_tokens`, `context_buffer_tokens`, `context_tier` — across three sessions.
- **The live collector has already moved off the bad conversion path, but A1 is not complete.** `startOtlpCollectorService` sends live batches through `ingestBatch`; the old `otel/service.ts:otlpSpanToRecord` that hard-codes `llm.invoke` is deprecated and unused. `ingestBatch` still accepts every span in a trace claimed by an adapter, so claimed health and `http.server` spans are not quarantined as non-model records.
- **Gemini attribution is still wrong.** `geminiAdapter.detect` only checks for `gemini.*` keys; it does not recognize `gen_ai.system = "gemini"`.
- **The canonical session projection is partial.** `buildSessionRow` already consumes `analyzeContext`, `rankSchemas` and `buildTimeline`, but emits the repository's intermediate payload: it lacks the complete ASAD `requests`, `servers`, `coverage`, `problems` and `reconciliation` contract, and its tool rows do not expose all ASAD fields.
- **The Context page still has two rendering paths.** Agent providers use `AgentSessionDashboard`; Claude/Codex/Antigravity still use `SessionDetails` and `TreeTable` through `fetchContextSessions`.
- **Much of Phase C exists as a test-backed shell, not a live-store completion.** `AgentSessionDashboard`, `SessionSpendCharts`, `SchemaCostRanking`, `SessionInspectorDrawer`, the unclipped content route, and component tests exist. None closes C2-C4 until the ASAD contract is served from and rendered against live `~/.kyberdash/canon.db`.
- **The receiver remains traces-only.** `OtlpReceiver.dispatch` rejects every path except `/v1/traces`; there is no OTLP log decoder, correlation, merge, or ordering-tolerant pending-log path.
- **Claude file ingestion is partial and currently incompatible with its reader contract.** The untracked `readers/claude.ts` extracts useful conversation and tool-result parts, but its synchronous `read(string | string[])` does not implement `ContentReader.read(filePath): AsyncIterable<ReaderTurn>`, and its tests do not exercise provider/synthesizer integration. Claude transcripts also do not contain the real system prompt; D5 must report that bucket as not measurable, while D3 obtains the real system prompt from `claude_code.api_request_body`.

## 4. Task list

**Phase A — Make the corpus honest.** No UI change; every later measurement depends on it.

### A1 — Quarantine non-model spans at ingest

- **Objective:** Make model-call classification an ingest invariant: derive `op` from span evidence and quarantine non-model spans even when their trace inherits a harness attribution.
- **Files / symbols:** `dash/kyber/canon/ingest.ts` (`ingestBatch`, `IngestOutcome`); `dash/kyber/canon/adapters/copilot.ts` (`canonicalOp`, `baseRecord`); `dash/kyber/canon/store.ts` (`CanonStore.quarantine` and the quarantine migration if richer audit fields are needed); `dash/kyber/otel/service.ts` (`otlpSpanToRecord`, remove the obsolete hard-coded converter); `dash/kyber/canon/ingest.test.ts`, `store.test.ts`, and `sessions.test.ts`.
- **Acceptance criteria:** Health, `http.server`, and other spans with no model-call evidence never enter `records`, are counted with a non-model quarantine reason, and cannot produce a derived session. Genuine model calls and their supported tool/auxiliary children retain the adapter-derived canonical operation. Tests cover claimed-trace noise as well as wholly unattributed noise.
- **Skills:** TypeScript; OpenTelemetry GenAI semantic conventions; SQLite migration; Vitest.
- **Dependencies:** None. This task owns the shared ingest seam before D1 changes it.

### A2 — Correct Gemini attribution

- **Objective:** Treat the value `gen_ai.system = "gemini"` as Gemini vendor evidence in addition to the existing `gemini.*` namespace fingerprint.
- **Files / symbols:** `dash/kyber/canon/adapters/gemini.ts` (`geminiAdapter.detect`, `relevance`); `dash/kyber/canon/adapters/gemini.test.ts`; `dash/kyber/canon/adapters/registry.test.ts`.
- **Acceptance criteria:** Adapter-vote tests cover both spellings, shared `gen_ai.usage.*` keys alone remain below the attribution threshold, and the retained Gemini statusline records re-normalize to `harness = gemini`.
- **Skills:** TypeScript; adapter/fingerprint design; Vitest.
- **Dependencies:** A1.

### A3 — Repair and rebuild the retained corpus

- **Objective:** Apply A1/A2 to retained raw evidence, remove newly quarantined rows from `records`, and rebuild the derived `session` cache.
- **Files / symbols:** `dash/kyber/tools/backfill.ts` (`renormalizeRecords`); `dash/kyber/tools/reingest.ts` (`reingestFromExports`, reuse `ingestBatch` rather than its duplicate adapter list); `dash/kyber/canon/store.ts` (atomic move/delete support); `dash/kyber/cli/register.ts` (`renormalize`, `build` command handlers); `dash/kyber/canon/sessions.ts` (`buildSessions`); `dash/kyber/tools/reingest.test.ts` plus focused backfill/rebuild coverage.
- **Acceptance criteria:** On live `~/.kyberdash/canon.db`, `records` contains model calls and supported model-call children only; quarantined health/HTTP counts are auditable; the 18,905 statusline rows report Gemini; surviving rows retain their prior content/parts; rebuilding prunes phantom sessions. Record, quarantine, session, and content-preservation counts are captured before and after the run.
- **Skills:** TypeScript; SQLite data migration and transaction safety; operational data validation; Vitest.
- **Dependencies:** A2.

**Phase B — The ASAD payload becomes the store's contract.**

### B1 — Make the ASAD payload the session projection contract

- **Objective:** Replace the intermediate `buildSessionRow` payload with one typed, JSON-safe ASAD payload: `context.first`/`context.last` buckets and `reported_input`, `tools[]` with `schema_tokens`/`invocations`/`turns_resident`, `timeline`, `turns`, `requests`, `servers`, `coverage`, `problems`, `reconciliation`, and `summary`.
- **Files / symbols:** `dash/kyber/canon/sessions.ts` (`buildSessionRow`, `serializeContext`, exported payload types); `dash/kyber/canon/types.ts` (shared contract types only where canonical); `dash/kyber/canon/sessions.test.ts`; a scrubbed, content-free shape fixture at `dash/kyber/canon/fixtures/asad-session-shape.json`.
- **Acceptance criteria:** A session payload read back through `CanonStore.getSessionPayload` validates field-for-field against the sanitized ASAD shape fixture; Maps and discriminated measurability unions survive JSON serialization; no captured prompt, credential, or developer-specific path enters the fixture.
- **Skills:** TypeScript contract modelling; JSON serialization; ASAD domain model; Vitest.
- **Dependencies:** A3.

### B2 — Route payload and comparison derivation through analysis modules

- **Objective:** Finish replacing bridge-local SQL/translation logic with the existing analysis layer and the B1 contract.
- **Files / symbols:** `dash/kyber/canon/sessions.ts` (`buildSessionRow`, `buildSessions`); `dash/kyber/analysis/context.ts` (`analyzeContext`); `schema.ts` (`rankSchemas`); `timeline.ts` (`buildTimeline`); `compare.ts` (`compareHarnesses`); `dash/kyber/canon/cost.ts` (`createCostBlock`, `sumCosts`); `dash/kyber/server/bridge.ts` (`getSessionPayload`, `getComparisonTable`, legacy sessions-db branches); `dash/kyber/server/routes.ts` (remove payload translation in legacy analysis endpoints); `dash/tests/kyber-bridge.test.ts` and `kyber-api.test.ts`.
- **Acceptance criteria:** Context, schema, timeline, comparison, and cost code have production consumers; `/api/kyber/session/:id` serves the B1 payload without adapting it; duplicate comparison SQL and default `sessions.db` reads are gone; `AGENTDASH_DB` is unset in the canonical-store test.
- **Skills:** TypeScript; SQLite query review; data-flow refactoring; Vitest.
- **Dependencies:** B1.

### B3 — Carry per-bucket measurability and reasons

- **Objective:** Make availability part of each payload bucket so absence can never be serialized as a measured zero.
- **Files / symbols:** `dash/kyber/canon/types.ts` (`Measurability`, metric availability/reason contract); `dash/kyber/canon/measurability.ts` (`measurabilityFor`, `getMeasurability`, `schemaRankingAvailability`, `contextCompositionAvailability`); `dash/kyber/canon/sessions.ts` (`mergeMeasurability`, `buildSessionRow`); `dash/kyber/canon/measurability.test.ts` and `sessions.test.ts`.
- **Acceptance criteria:** Every ASAD bucket is either measured/derived with a value or `not_measurable` with a source-specific reason; JSON payloads never substitute `0` for unavailable content, schema, structure, or counters.
- **Skills:** TypeScript discriminated unions; measurement semantics; Vitest.
- **Dependencies:** B2.

**Phase C — The view.**

### C1 — Remove the legacy Context rendering path

- **Objective:** Make every provider use the canonical session list and one ASAD expansion path.
- **Files / symbols:** `dash/dash/src/components/ContextExplorer.tsx` (`TreeTable`, `SessionDetails`, `SessionRow`, `AgentSessionRow`, `ContextExplorer`, provider routing); `dash/dash/src/lib/api.ts` (remove unused legacy context fetch/types); `dash/dash/src/lib/kyberApi.ts` (`fetchKyberSessions`); `ContextExplorer.test.tsx` and `dash/dash/src/components/kyber/session-dashboard.test.tsx`.
- **Acceptance criteria:** The Context page has one session-row/expanded-view implementation for every harness, no TreeTable/SessionDetails symbols or toggle, and no Context-page request to the legacy context-tree endpoint.
- **Skills:** TypeScript; React; TanStack Query; component testing.
- **Dependencies:** B3.

### C2 — Render all six ASAD views

- **Objective:** Render ASAD's information architecture in KyberDash styling: session overview, per-turn token spend, context composition, tool/schema cost, execution timeline, and session cost/token accounting.
- **Files / symbols:** `dash/dash/src/components/AgentSessionDashboard.tsx` (`AgentSessionPayload`, `AgentSessionContent`, `AgentSessionLoader`); `SessionSpendCharts.tsx` (`TurnSpendChart`, `ContextCompositionChart`); `SchemaCostRanking.tsx`; `SessionCostPanel.tsx`; `kyber/TimelineView.tsx`; `dash/dash/src/lib/kyberApi.ts`; their colocated tests, including `AgentSessionDashboard.test.tsx`, `SessionSpendCharts.test.tsx`, `SessionCostPanel.test.tsx`, and `kyber/kyber-views.test.tsx`.
- **Acceptance criteria:** All six views consume the B1 payload directly, preserve ASAD order/captions, use KyberDash visual primitives, and render without shape fallbacks. Fixture work may proceed after B3; completion requires rendering at least one real canonical session, and the per-server path additionally waits for D3.
- **Skills:** TypeScript; React; data visualization; accessibility; component testing.
- **Dependencies:** B3 for fixture implementation; D3 for the real-data per-server completion gate. May run in parallel with C1 against separate files.

### C3 — Complete source-aware caveat banners

- **Objective:** Port ASAD caveat text and escalation rules without presenting derived counts as reported facts.
- **Files / symbols:** `dash/dash/src/components/SessionSpendCharts.tsx` (`ContextCaveat`, `ContextCompositionChart`); `AgentSessionDashboard.tsx` (session-level notes/reconciliation banners); `SessionSpendCharts.test.tsx` and `AgentSessionDashboard.test.tsx`.
- **Acceptance criteria:** Banner text matches ASAD for equivalent input; derived bucket counts escalate above a strictly greater-than-15-percent residual; harness-reported buckets suppress that escalation while still naming any residual; B3 reasons appear verbatim for unavailable buckets. Verify on live derived and harness-reported sessions where available.
- **Skills:** TypeScript; React; measurement communication; component testing.
- **Dependencies:** C2.

### C4 — Complete full-content drill-down

- **Objective:** Wire every clickable band/tool/span to the existing unclipped session-content endpoint and make truncation explicit.
- **Files / symbols:** `dash/dash/src/components/AgentSessionDashboard.tsx` (`openDrawerForTurn`, `openDrawerForTool`, `openDrawerForSpan`); `SessionInspectorDrawer.tsx` (`FullContentPanel`, `SessionInspectorDrawer`); `dash/dash/src/lib/kyberApi.ts` (`fetchKyberSessionContent`); `dash/kyber/server/routes.ts` (`parseSessionContentPath`, session content route); `dash/kyber/server/bridge.ts` (`getSessionContent`, `applyContentBudget`); drawer/dashboard/API tests.
- **Acceptance criteria:** Clicking a real live-session band retrieves the full canonical part rather than the payload's clipped 2,000-character preview; server-side budget clipping is visibly labelled with shown/total length; invalid session, span, and part filters fail explicitly. Existing tests are necessary but do not replace live verification.
- **Skills:** TypeScript; React; TanStack Query; HTTP API design; Vitest.
- **Dependencies:** C3.

**Phase D — Collection breadth.** Each row is independently shippable.

### D1 — Ingest and correlate OTLP logs

- **Objective:** Serve `/v1/logs` in JSON and protobuf and enrich one span-shaped canonical record per ADR 0009, including logs-before-spans.
- **Files / symbols:** `dash/kyber/otel/receiver.ts` (`OTLP_LOGS_PATH`, `OtlpLog`, JSON/protobuf log decoders, `OtlpReceiver.dispatch`); `receiver.test.ts`; `dash/kyber/otel/writer.ts` and `writer.test.ts` (signal-aware batch sink); new `dash/kyber/canon/log-ingest.ts` (`ingestLogBatch`, correlation and enrichment); `dash/kyber/canon/store.ts` (`find`/enrich/quarantine/consume-pending operations and migration of the existing quarantine table, not a parallel record table); `store.test.ts`; `dash/kyber/otel/service.ts` (`startOtlpCollectorService` signal sinks); `dash/kyber/canon/ingest.test.ts`.
- **Acceptance criteria:** Both encodings accept `/v1/logs`; exact `(trace_id, span_id)` correlation wins, then `session_id` plus a bounded timestamp window; one log enriches one existing record without changing counters; duplicate delivery is idempotent; a log arriving before its span remains auditable and enriches when the span arrives; unresolved logs are quarantined and counted, never inserted as parallel records.
- **Skills:** TypeScript; OTLP/HTTP JSON and protobuf wire formats; OpenTelemetry log/trace correlation; SQLite migration and idempotency; Vitest.
- **Dependencies:** None functionally. It is in the first ready wave with A1, but both tasks touch `ingest.ts`, `store.ts`, `service.ts`, and their tests, so concurrent workers require isolated branches and an explicit integration order.

### D2 — Map live Copilot Chat content

- **Objective:** Observe the content-enabled Copilot exporter and map its actual GenAI vocabulary without guessing.
- **Files / symbols:** `dash/kyber/canon/adapters/copilot.ts` (`canonicalParts`, attribute-key lists, `canonicalSessionId`, adapter normalization); `adapters/copilot.test.ts` and `adapters/content.test.ts`; `docs/dash/telemetry-inventory.md` Copilot attribute inventory.
- **Acceptance criteria:** After D1 and after the owner explicitly enables capture outside this task, live Copilot spans/logs land with prompt, response, tool content, session identity, and accurate measurability; observed attribute names and confidence are recorded in the inventory; disabling capture still yields counters and explicit not-measurable content. No worker edits VS Code/Copilot user configuration.
- **Skills:** TypeScript; OpenTelemetry GenAI semantic conventions; empirical telemetry inspection; Vitest; governed documentation.
- **Dependencies:** D1 and owner-controlled capture enablement.

### D3 — Enrich Claude records from raw API-body logs

- **Objective:** Parse `claude_code.api_request_body` into canonical system-prompt and tool-definition parts with ground-truth server fields.
- **Files / symbols:** `dash/kyber/canon/adapters/claude-code.ts` (add API-body parsing/content-part helpers); `dash/kyber/canon/log-ingest.ts` (dispatch enrichment through the Claude mapper); `dash/kyber/canon/adapters/content.test.ts` and `claude-code` adapter tests; `dash/kyber/canon/sessions.test.ts` for server aggregation.
- **Acceptance criteria:** After the owner explicitly enables Claude raw-body logging, a live log enriches its `claude_code.llm_request` record; system prompt and complete tool schemas survive storage; `ContentPart.server` comes only from ground-truth request structure; B1 `tools`/`servers` produce per-server bands in the live C2 view. No worker changes Claude environment or configuration.
- **Skills:** TypeScript; Claude Messages API request schema; OTLP log enrichment; JSON parsing; Vitest.
- **Dependencies:** D2 for the required D1→D2→D3 sequence and capture-policy discipline.

### D4 — Add a Cursor hook OTLP emitter

- **Objective:** Convert Cursor agent hook events into one OTLP trace per agent turn, carrying available token counts and ordered tool activity.
- **Files / symbols:** new `dash/kyber/otel/cursor-hook.ts` (hook-event state machine and OTLP exporter); `cursor-hook.test.ts`; `dash/kyber/cli/register.ts` (a stdin-driven hook command); focused CLI tests; `docs/dash/telemetry-inventory.md` Cursor measurability outcome.
- **Acceptance criteria:** Synthetic hook sequences produce one idempotent trace per turn with stable session/turn identity, available token counts, and tool order; unavailable prompt/schema fields carry reasons. Live completion requires the owner to register the command alongside existing hooks and execute one turn; the task never edits `~/.cursor/hooks.json` or existing gitleaks hooks.
- **Skills:** TypeScript; Cursor hook event contracts; OTLP/HTTP JSON; streaming state machines; Vitest.
- **Dependencies:** B3. Owner-controlled hook registration is the live gate.

### D5 — Integrate Claude and Codex dot-folder content

- **Objective:** Join file-derived content to canonical turns under D7 precedence and replace blanket file-source measurability with what each reader actually supplies.
- **Files / symbols:** `dash/kyber/synth/readers/types.ts` (`ContentReader`, `ReaderTurn`); `readers/codex.ts`/`codex.test.ts`; untracked `readers/claude.ts`/`claude.test.ts` (adapt to the async reader contract); `dash/kyber/synth/synth.ts` (`synthesizeCall` content/parts input); `provider.ts` (`ingestProviders` reader integration); `dedup.ts` (OTel/file turn join and D7 precedence); `dash/kyber/canon/measurability.ts` (provider declarations); associated synth/dedup/measurability tests.
- **Acceptance criteria:** Codex files yield the real system prompt, instructions, conversation, tool results, and context window they contain; Claude files yield real conversation/tool-result content and explicitly mark system prompt/tool definitions not measurable; an OTel+file duplicate keeps OTel counters and uses file parts only when OTel has none; values are never summed.
- **Skills:** TypeScript; streaming JSONL parsing; cross-source deduplication; measurement semantics; Vitest.
- **Dependencies:** B3.

### D6 — Cover opencode, Kilo, Copilot CLI, and pi

- **Objective:** Add provider-specific collection/readers where evidence exists and explicit not-collectable outcomes where it does not.
- **Files / symbols:** new provider readers under `dash/kyber/synth/readers/` (`opencode.ts`, `kilo.ts`, `copilot.ts`, `pi.ts` with colocated tests); `dash/kyber/synth/provider.ts` (`ingestProviders` registration); `synth.ts` and `dedup.ts` only for shared canonical joins; `dash/kyber/canon/measurability.ts`; existing upstream read-only seams in `dash/src/providers/opencode.ts`, `kilo-code.ts`, `copilot.ts`, and `pi.ts`; `docs/dash/telemetry-inventory.md`.
- **Acceptance criteria:** Each provider either produces canonical records through its existing OTel/file evidence or appears as not collectable with a concrete reason; Copilot CLI preserves its reported ASAD taxonomy; no Kilo zero is fabricated from an empty store; pi OTel/file duplicates obey D7. No task enables opencode/Kilo/pi/Copilot configuration without owner authorization.
- **Skills:** TypeScript; SQLite/JSON session-store parsing; provider telemetry investigation; measurement semantics; Vitest; governed documentation.
- **Dependencies:** B3.

## 5. Sequencing / dependency graph

```
A1 ─ A2 ─ A3 ─ B1 ─ B2 ─ B3 ─┬─ C1 ───────────────┐
                              ├─ C2 (fixture) ─────┼─ C2 (live) ─ C3 ─ C4
D1 ─ D2 ─ D3 ─────────────────┘                    │
                              ├─ D4                │
                              ├─ D5                │
                              └─ D6                │
```

Phase A precedes B: the payload cannot be validated against a corpus that is 60 percent noise. A1 and D1 form the first ready wave, but overlap in `ingest.ts`, `store.ts`, `service.ts`, and tests; conductor must use isolated branches and merge A1's classification invariant before replaying D1's signal-aware changes, or assign both to one worker. B1 is the data-contract gate for C. C1 and C2's fixture path may proceed in parallel after B3. D1-D3 join before C2's real-data per-server completion gate. D4, D5, and D6 each depend only on B3 and are independently shippable.

D5 and D6 both integrate at `synth/provider.ts`, `synth.ts`, `dedup.ts`, and `measurability.ts`. If dispatched concurrently, use isolated branches and merge one task's shared-seam changes before replaying the other's provider-specific commits; do not let either worker overwrite the other's provider declarations.

## 6. Residual decisions / risks

- **Phantom completion.** The archived predecessor plan's post-mortem records a task marked complete on mock React shells with no backend. Every phase here is gated on rendering against the developer's live `~/.kyberdash/canon.db`, not on tests passing. Tests do not close a task on their own.
- **Storage.** Capturing request bodies multiplies content volume. R12.4's budget is re-measured after the first capture, before D5 adds file sources.
- **Content is prompts.** R12.3 forbids captured content in tracked files. Enabling capture on a developer machine is the developer's call; no task here changes harness configuration without being asked.
- **Schema availability is unproven for most harnesses.** Antigravity emits tool names only; whether that is a limit or a setting is unverified. If no harness supplies schemas, the tool-cost view says so rather than rendering an empty ranking.
- **Kilo may not be collectable at all** on the current evidence. D6 permits recording that as a finding.
- **Owner-controlled capture gates.** D2, D3, and D4 can implement and test their code without changing harness settings, but their live completion gates require the owner to enable content or register a hook outside the task. This is an execution condition, not permission for a worker to edit user-level configuration.
- **Shared-file integration.** A1/D1 and D5/D6 have explicit overlap called out in §5; conductor must serialize or isolate those edits.

## 7. Out of scope

- Importing the Python pipeline's historical corpus. The loss was accepted in ADR 0008.
- Reverse-engineering Copilot's `chatSessions` delta log or Cursor's `blobs` store. Both are deferred until the OTel path for that harness has been tested, since success there makes the replayer unnecessary.
- Cursor's Enterprise OpenTelemetry Export. It carries no prompt content and no traces.
- Any change to upstream `dash/src/**` beyond the single existing registration and route delegation points.

## 8. Required skills

- TypeScript and strict contract modelling
- Vitest and React component testing (`renderToStaticMarkup` where the existing component tests use it)
- React, TanStack Query, accessibility, and data visualization
- SQLite schema/data migration, transactional repair, and idempotent correlation
- OTLP/HTTP traces and logs in JSON and protobuf
- OpenTelemetry GenAI semantic conventions and adapter fingerprinting
- Streaming JSONL and SQLite session-store parsing
- Cross-source identity, deduplication, and measurability semantics
- Cursor hook event contracts
- Governed documentation updates and privacy-safe live telemetry validation

## 9. Verification harness

Per phase, against the live store — not only against tests.

```bash
npm --prefix dash run typecheck
npx --prefix dash vitest run kyber
npx --prefix dash vitest run tests/kyber-api.test.ts tests/kyber-bridge.test.ts
npx --prefix dash vitest run dash/src/components
```

- **Phase A**: assert `records` contains no `http.server` or health spans, and that quarantine counts them. Assert `harness = gemini` for statusline records.
- **Phase B**: a payload from `canon.db` validates against the ASAD fixture, with `AGENTDASH_DB` unset — proving the canonical store is self-sufficient.
- **Phase C**: open the Context page, expand a session per harness, and confirm one band per MCP server, a visible residual, and full text in the drawer.
- **Phase D**: per source, assert an absent bucket reports not measurable with a reason rather than 0.
- **Review gates**: run the repository's code-review skill over the completed diff and the security-review skill over OTLP/content ingestion, local file readers, hook input, and content endpoints. Resolve findings before closeout.
- **Documentation gates**: because D2/D4/D6 update the inventory, run `docs validate .` and `docs drift .` before completion.

`dash/package.json`'s `"test": "vitest run tests"` excludes `dash/dash/src/**`, so component tests do not run in CI today. Adding them is part of Phase C.

## 10. Closeout verification — 2026-09-04

The plan remains **Review required** and is **not archived**. Implementation and
repository review are complete; the plan's own live completion gates are still unmet.
This closeout does not treat owner/runtime enablement as done: `~/.cursor/hooks.json`
and Claude environment variables were not changed here.

B2 follow-up remains **PASS**: production code under `dash/kyber` has no
`AGENTDASH_DB`, `KYBER_DB`, `sessions.db`, `sessionsPath`, or `sessionsDb` path, and
tests prove those environment variables cannot expose a legacy session.

| Area | Verified outcome | Closeout state |
|---|---|---|
| A1–A3 | Ingest quarantines non-model spans, recognizes Gemini's `gen_ai.system` value, and rebuilds derived sessions from retained records. | Verified |
| B1–B3 | `canon.db` projects the JSON-safe ASAD contract directly and preserves per-bucket measurability. `KyberBridge` reads `canon.db` only. | B1 waived for formal review. B2 follow-up **PASS**. |
| C1 | The legacy Context rendering path was removed. | Waived after the review cap; evidence-only review did not produce a formal pass |
| C2–C3 | Fixture coverage passed; live Copilot session `08551cf5-b064-4095-9552-8a9a0a0f78d2` rendered. Per-server schemas were 0 of 81, reported as unavailable rather than fabricated. | Live per-server bands still wait on D3 owner Claude raw-body logs |
| C4 | Fixture coverage passed. | **Not passed:** live drawer click against a live session remains required |
| D1–D3 | `/v1/logs` enriches span-shaped records; Copilot and Claude enrichment paths passed their stated verification. Duplicate log records use unique `deriveLogId` identities. | **Not passed:** owner must enable `OTEL_LOG_RAW_API_BODIES=1` for live Claude raw-body verification; per-server schemas remain empty live |
| D4 | Cursor hook conversion passed synthetic and CLI POST verification. | **Not passed:** owner must register `codeburn kyber cursor-hook` in `~/.cursor/hooks.json` and run a Cursor turn; this work does not edit that file |
| D5–D6 | Dot-folder readers and D7 source precedence passed; OpenCode and Kilo report concrete non-collectable reasons; Copilot CLI taxonomy and pi support are implemented. | Verified |
| Review gates | End-of-run code-reviewer **APPROVE**, risk LOW. Prior `REQUEST_CHANGES` (unique `deriveLogId`) is closed. | 14/14 declared gates pass, including `ts-typecheck`, `ts-test`, and `ts-lint` |

Canonical behavior is documented in
[KyberDash architecture](../dash/architecture.md), [KyberDash runbook](../dash/runbook.md),
and the [telemetry inventory](../dash/telemetry-inventory.md). Decisions D4, D7, and D8 are
carried by [ADR 0008](../adr/0008-kyberdash-single-canonical-store.md) and
[ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md).
