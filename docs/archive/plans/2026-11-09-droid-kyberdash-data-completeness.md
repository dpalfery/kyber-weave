---
id: plans/2026-11-09-droid-kyberdash-data-completeness
title: Droid (Factory) sessions in KyberDash show duration, cost, real tool calls and readable titles
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
created: 2026-11-09
last-reviewed: 2026-11-09
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash]
---

# Droid (Factory) sessions in KyberDash show duration, cost, real tool calls and readable titles

**Status: Archived, 2026-11-09.** Closed out by the documentation role in task T7 after tasks T1 to T6 landed. Approved for execution under decision D13. The task file was removed at closeout. The closeout mapping below records where each decision and harvest item now lives.
**Development mode:** `test-first` (RED by `test-dev`, GREEN by the implementation specialist, never the same worker).
**Autonomy:** `autonomous` (every open item decided by recommendation; provenance in `## Decisions`).
**Scale:** `extends-component`, component `KyberDash`.
**Slice:** 1 of 3. This plan is one pull request. Slices 2 and 3 are follow-ups (decision D2).

## Problem and goal

**Background.** "Droid" is Factory's coding CLI. KyberDash (this repository's analytics engine and dashboard, under `dash/`) already receives Droid data three ways: the Factory transcript files (`~/.factory/sessions/**/<id>.jsonl` plus `<id>.settings.json`), the Kyber-owned Factory hooks plugin (`kyberdash-factory-plugin`, ADR 0055), and Droid's own native OpenTelemetry metrics.

**Problem.** The data is there but the dashboard shows it badly. Measured on the owner's store (`~/.kyberdash/canon.db`, 365 top-level Droid sessions): duration is zero on every session, 161 sessions are unpriced (159 of them run `minimax-m3.1-flash`, a model with no bundled rate), the tool-call count is about 1% of what Droid itself reports (114 against roughly 10,777), and titles are machine strings such as `droid:minimax-m3.1-flash` or `factory.turn`.

**Goal.** Without a new plugin and without any change to the existing one, fix the collector, the session projection and the pricing data so a Droid session shows:

- a real **duration**;
- a **cost** for MiniMax M3.1 Flash sessions;
- the real **tool calls**, taken from the transcript;
- the session's own **title**, as Droid records it.

The web dashboard already renders all four once the engine supplies them, so this slice changes no `.tsx`.

## Decisions

Every open item was decided by recommendation under the autonomous-decisions rule. Provenance `autonomous: recommendation accepted` means the row is decided and awaits the owner's approval at the approve-and-execute gate; `autonomous: low-confidence, no authoritative source` marks a call no source settled, reviewed at pull request.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Does Droid collection need a new plugin or hook? | **No.** Every signal this slice needs is already on disk in the Factory transcript the file source reads. No plugin or hook change in this slice. | Read in this worktree: the transcript's `session_start` line carries `title` (seen in the owner's `~/.factory/sessions`; keys checked, content not read). Assistant messages carry `tool_use` blocks with ids, the following user message carries `tool_result` blocks with `tool_use_id` and `is_error`, and every entry carries a `timestamp`. The plugin cannot supply per-call tool identity at all: Factory's documented hook payloads carry no `tool_call_id` (https://docs.factory.com/reference/hooks-reference, retrieved 2026-11-09; PreToolUse has `tool_name` and `tool_input`, PostToolUse adds `tool_response`). So `toolSpan` in `dash/src/capture/harnesses/factory-plugin/mapper.ts` anchors every tool call of a session on the session id, and they all collapse onto one span id. That is the root cause of the 1% capture. | autonomous: recommendation accepted |
| D2 | How is the work sliced, given that one plan never spans several pull requests? | **Three slices.** Slice 1 (this plan): transcript tool calls, duration, title, MiniMax pricing, and the Droid pairing rule. Slice 2 (follow-up todo): project Droid's native OpenTelemetry metrics into the session (`CanonStore.getMetricPoints` has no production caller; candidates are `droid.code.lines_modified`, `droid.code.files_modified`, `droid.git.commits`, `droid.git.pull_requests`, `droid.mcp.tool_invocations`, `droid.skill.invocations`) and render them. Slice 3 (follow-up todo): Factory plugin contract 2. That covers the tool-span anchor collapse, the `Stop` hook's `elapsed_time` and `tool_execution_count`, the `SessionEnd` hook's `session_duration_ms`, the 21 quarantined plugin spans, and `kyberdash kyber capture status --harness factory` reporting native export "not configured" while metrics arrive. | Slice 1 fixes all four visible gaps for every past and future session, because the transcripts are retroactive and complete. Slice 2 adds a new payload field and a new panel, which is a contract change of its own. Slice 3 changes a wire contract and only helps sessions recorded after the fix. Ordering by value per pull request puts slice 1 first. | autonomous: recommendation accepted |
| D3 | In a session both sources saw (plugin plus file), which source owns the tool calls? | **The file transcript, when it has any tool record for the session.** The plugin's `factory.tool` records then drop out of tool counting for that session. With no file tool record, the plugin's records count as today. One session counts tool calls from exactly one source. | The plugin's tool records are broken by construction (D1). This conflicts with ADR 0055's premise that the plugin owns tool timing. The accepted ADR is honoured, not overridden: ADR 0055 requires that "any future Factory collection source ... must state how it avoids double counting against the plugin and the file source", and this rule is that statement. Closeout amends ADR 0055 with the measured evidence (harvest H3). Turns and prompts stay plugin-owned, as the ADR says. | autonomous: recommendation accepted |
| D4 | What is a Droid session's duration? | **Active time:** for each assistant call the provider emits, `activeDurationMs` = the call's timestamp minus the last boundary. A boundary is a user message that carries human text (a prompt) or the previous emitted call. A user message that carries only tool results is not a boundary, so tool execution counts. Human think time between prompts does not count. A paired session's `duration_ms` is the sum over its file-source records, because the plugin's spans have start equal to end by construction (`toWireSpan` in `dash/src/capture/harnesses/factory-plugin/entry.ts`). A missing, unparseable or negative interval leaves `activeDurationMs` absent, never 0. | This matches the existing "active duration" meaning Codex uses (`activeDurationMs` in `dash/src/providers/codex.ts`). The synthesizer already maps it onto `durationMs` (`synthesizeRecord` in `dash/src/synth/synth.ts`). The web already renders a positive `duration_ms` (`formatDuration` in `dash/web/src/lib/labels.ts`, used by `AgentSessionDashboard.tsx`) and a gap for 0. | autonomous: low-confidence, no authoritative source. Cheap to reverse: one provider function and one projection rule. |
| D5 | Which key pairs a provider call with its transcript turn? | **`pairingId` = the assistant entry id**, not `turnId`. The parser contract for `droid` goes to `'2'` (`parserContractVersionFor` in `dash/src/refresh/registry.ts`), so stored checkpoints re-synthesize and every past session gains tools, durations, titles and the new rate. | This is the exact Command Code precedent (`COMMANDCODE_PARSER_CONTRACT_VERSION` v2 comment in `registry.ts`). `nativeRecordIdentity` in `synth.ts` reads `turnId` but not `pairingId`. The twin-collapse key `collapseKey(callCounterKey(...), turnId)` also reads `turnId`. So `pairingId` leaves record identity and twin dedupe byte-identical (the droid dedup key already yields the entry id as `nativeRecordId`). | autonomous: recommendation accepted |
| D6 | Where does a readable title come from? | **Droid's own session title.** The provider reads `session_start.title` and carries it on each call as a new optional `ParsedProviderCall.sessionTitle`. The synthesizer stamps it as `kyber.session.label` (`SESSION_LABEL_KEY`), which `sessionLabelOf` in `dash/src/canon/sessions.ts` already prefers. Fallback: `isMachineLabel` in `dash/src/canon/naming.ts` also rejects `factory.session`, `factory.turn`, `factory.tool`, and `droid:<token>` with no whitespace, so an untitled session reads "Droid session". `SESSION_LABEL_KEY` joins `PROJECTED_ATTRIBUTE_KEYS` in `dash/src/canon/store-rows.ts` so a long title is never dropped. | The title is the harness's own record, not a fabricated summary, which keeps the naming module's "honest unobservability" rule. The fallback regexes mirror the existing `OPENCODE_SPAN_NAME_RE`. `droid:` followed by a space ("droid: fix build") stays a human title. | autonomous: recommendation accepted |
| D7 | What rate prices MiniMax M3.1 Flash? | **$0.15 per million input, $0.60 per million output, $0.015 per million cache read; cache write not published (null).** Key `MiniMax-M3.1-Flash`. Added to `MANUAL_ENTRIES` in `dash/scripts/bundle-litellm.mjs`, to the committed `dash/src/pricing/data/litellm-snapshot.json`, and to `dash/src/pricing/data/pricing-provenance.json`. Lookup of `minimax-m3.1-flash` resolves through the existing case-insensitive index. The Priority tier (1.5x) is not modelled, as for every other model. | Source: MiniMax's official pricing page, https://platform.minimax.io/docs/guides/pricing, retrieved 2026-11-09. A null cache write follows `buildCosts` (1.25x input, `cacheWriteCostIsExplicit: false`), the same treatment as other models with no published write rate. The reproducibility guard in `pricing-fallback-data.test.ts` requires the `MANUAL_ENTRIES` and snapshot pair. | autonomous: recommendation accepted |
| D8 | Does the web dashboard need changes? | **No `.tsx` or `dash/web` change in this slice.** | `AgentSessionDashboard.tsx` renders `summary.duration_ms` through `formatDuration` and shows a gap for 0. The session cost panel renders a priced block. The payload `label` is already formatted by the engine's `formatSessionTitle`. The gaps are all engine-side. | autonomous: recommendation accepted |
| D9 | Are Droid's cache-creation tokens (26 of 365 sessions non-zero) a bug? | **No change.** The provider passes through `tokenUsage.cacheCreationTokens` from the companion settings verbatim (`createParser` in `dash/src/providers/droid.ts`). The zeros are what Droid reports, and are expected for providers without prompt-cache writes. | Not verified against Factory documentation. Recorded for slice 2 to re-check against native data. | autonomous: low-confidence, no authoritative source. Cost of being wrong is low: display only. |
| D10 | Is the report-path session cache invalidated? | **Yes.** Add `droid: 'droid-completeness-v1'` to `PROVIDER_PARSE_VERSIONS` in `dash/src/ingest/session-cache.ts`, so the CLI report path re-parses cached Droid files and picks up the new rate and durations. | Established pattern (the `activity-price-v1` and `est-cost` bumps in the same map). The canonical refresh path re-synthesizes through D5's contract bump. | autonomous: recommendation accepted |
| D11 | Is a new ADR needed? | **No new ADR. Amend ADR 0055 in place** at closeout: the transcript content reader is part of the existing file source, not a new collection source; one source per session owns tool calls (D3); duration comes from the file source (D4). | Amending in place is ADR 0055's own established practice (seven dated amendments). | autonomous: recommendation accepted |
| D12 | Test contract and development mode. | **`test-first`**, with the Test contract below. | Default mode; no opt-out was supplied. | autonomous: recommendation accepted |
| D13 | Is the plan approved for execution? | **Approved.** D1 to D12 and the Test contract are accepted as recorded. | Conductor input `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous`, 2026-11-09, after the worktree documentation checks passed (only the expected `KW-DOC-LIFECYCLE-003` remained). The low-confidence rows D4 and D9 stay flagged for the pull request body. | autonomous: recommendation accepted (AUTONOMOUS_APPROVAL approve-and-execute, 2026-11-09) |

## Investigation findings

**Discovery:** codegraph 0, docs 3, direct reads 41 (plus 33 grep/glob lookups).

- **CodeGraph, 0 calls.** The MCP server is bound to the canonical checkout at revision `05eb938a` on another branch, not to this run's `RUN_BASE`. The `codegraph explore` CLI needs a shell, and this role has none. Every code fact below was read directly, reason: "server bound to another revision".
- **Tree read.** The worktree `/Users/dave/git/personal/claude/kyber-weave-worktrees/claude-feat-droid-otel-kyberdash`, branch `claude/feat/droid-otel-kyberdash`. The packet names it as `origin/main` `7a235215`, not confirmed from here because there is no shell.
- **Docs, 3 calls:** `docs_get_source_repository_and_revision`, `docs_find_documents_citing_code_symbol(splitDroidSessionRecords)` (cited by `docs/dash/architecture.md`), and `docs_answer_question_from_repo_documentation` (Factory section of `docs/dash/telemetry-inventory.md`). These answered from the canonical checkout, so they were used only to locate docs.
- **External, self-gathered.** Factory hooks reference and MiniMax pricing, both fetched by WebFetch. No research agent was used.

**Verified facts (worktree):**

- **Tools: the file source yields none.** Tool-invoke records are synthesized only from a content reader's `ReaderTurn.toolCalls` (`dash/src/synth/readers/types.ts`). `PROVIDER_READERS` in `dash/src/synth/provider.ts` has no `droid` entry, so the Droid file source contributes zero tool records.
- **Tools: paired sessions read only plugin records.** In a paired session, `buildSessionRow` in `dash/src/canon/sessions.ts` sets `structureRecords` to the plugin's records alone (`splitDroidSessionRecords` in `dash/src/canon/droid-pairing.ts`). `toolRecords = structureRecords.filter(op === 'tool.invoke')`, so tool calls come only from the plugin.
- **Tools: why the plugin undercounts.** Plugin tool spans anchor on `tool_call_id ?? sessionId` (`toolSpan` in `mapper.ts`). Factory's hook payloads carry no `tool_call_id` (D1), so all of a session's tool calls share one span id.
- **Duration.** `summary.duration_ms` is the sum of `record.durationMs` over the request roots or structure records (`buildSessionRow`). File records get `durationMs: call.activeDurationMs ?? 0` (`synthesizeRecord` in `synth.ts`), and the Droid provider never sets `activeDurationMs`. Plugin spans have start equal to end (`toWireSpan` in `entry.ts`). Result: 0 for every Droid session.
- **Cost.** Cost is fixed at synthesis by `costBlockFor` from the provider's `calculateCost`. `repriceTurns` reprices only published-table and Copilot harnesses, so Droid needs re-synthesis (D5) after the rate lands. No `MiniMax-M3.1-Flash` key exists in `dash/src/pricing/data/*`. `MiniMax-M3` and `MiniMax-M2.7` do.
- **Titles.** `sessionLabelOf` falls back to the first record's name. Synthesized records are named `${provider}:${model}` (`synthesizeRecord`) and plugin records `factory.turn` / `factory.session`. `isMachineLabel` in `naming.ts` matches none of them.
- **Precedent for the reader.** The Command Code reader (`dash/src/synth/readers/commandcode.ts`) reads the same Anthropic-shaped `message.content` blocks and pairs by `pairingId` under its own contract bump.
- **Web.** `AgentSessionDashboard.tsx` already renders positive durations, priced cost and `tool_calls`. No display change is needed (D8).
- **Native metrics.** These are stored (`CanonStore.upsertMetrics`) but never projected (`CanonStore.getMetricPoints` has no production caller). Out of scope here; slice 2.

## Design

1. **Droid content reader** (new `dash/src/synth/readers/droid.ts`, exported `droidReader: ContentReader`), modelled on `commandcodeReader`:
   - **Session id.** From the `session_start` entry's `id`.
   - **One `ReaderTurn` per `message` entry with `role: 'assistant'`.** `nativeRecordId` is the entry `id`. Text blocks become `conversation_history` parts. `tool_use` blocks become `ReaderToolCall { id, name, arguments, timestamp: entry.timestamp }`.
   - **Tool results.** A following `role: 'user'` entry's `tool_result` blocks attach to the latest pending turn as `ReaderToolResult { toolCallId: tool_use_id, content, isError: is_error === true, timestamp: entry.timestamp }`.
   - **Pairing.** `positionalPairingUnsafe: true`.
   - **Robustness.** Malformed lines are skipped. The stream is closed in `finally`.
2. **Droid provider** (`createParser` in `dash/src/providers/droid.ts`). Each emitted call gains:
   - `pairingId` = the assistant entry id (the same id the dedup key already uses);
   - `activeDurationMs`, per D4, absent when not computable;
   - `sessionTitle` = the trimmed, non-empty `session_start.title`, absent otherwise.

   `deduplicationKey`, tokens and cost logic are unchanged.
3. **Call type and synthesis.**
   - `ParsedProviderCall` (`dash/src/providers/types.ts`) gains `sessionTitle?: string`.
   - `synthesizeRecord` (`dash/src/synth/synth.ts`) stamps `raw[SESSION_LABEL_KEY] = call.sessionTitle` when present.
   - `PROVIDER_READERS` registers `['droid', droidReader]`.
   - `parserContractVersionFor` returns `'2'` for `droid`, with a v2 comment in the house style.
4. **Pairing split** (`dash/src/canon/droid-pairing.ts`). `DroidSessionSplit` gains `fileToolRecords`: the Droid records whose source starts with `codeburn/` and whose `op` is `tool.invoke`. They also stay in `counterRecords`, so cost and token sums are unchanged because tool records carry no tokens. The split stays a leaf and stays inert outside Droid.
5. **Session projection** (`buildSessionRow` in `dash/src/canon/sessions.ts`). This applies only to a paired Droid session:
   - **Tools.** When `split.fileToolRecords` is non-empty, it is the tool source for `toolRecords`, the tools table, `tool_calls` and `tools_invoked`. Otherwise the plugin's tool records are used.
   - **Duration.** `duration_ms` is the sum of the file-source records' `durationMs`.
   - **Everything else** is byte-identical for unpaired and non-Droid sessions.
6. **Naming** (`isMachineLabel` in `dash/src/canon/naming.ts`). Add `FACTORY_SPAN_NAME_RE = /^factory\.(?:session|turn|tool)$/i` and `DROID_SYNTH_NAME_RE = /^droid:\S+$/i`. `PROJECTED_ATTRIBUTE_KEYS` in `dash/src/canon/store-rows.ts` gains `SESSION_LABEL_KEY`.
7. **Pricing.** Add the tuple `'MiniMax-M3.1-Flash': [0.15e-6, 0.6e-6, null, 0.015e-6]`:
   - to `MANUAL_ENTRIES` in `dash/scripts/bundle-litellm.mjs`;
   - to `dash/src/pricing/data/litellm-snapshot.json`, with an identical tuple;
   - with provenance `{ source: "https://platform.minimax.io/docs/guides/pricing", retrieved: "2026-11-09" }` in `pricing-provenance.json`.

   Then `PROVIDER_PARSE_VERSIONS.droid = 'droid-completeness-v1'` in `dash/src/ingest/session-cache.ts`.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | Droid file source reads tool calls, tool results, active duration and session title from the transcript; parser contract 2 | KyberDash | reference | `docs/dash/telemetry-inventory.md` (section "Factory (Droid) capture: the plugin path and the file path") |
| H2 | Pairing split gains the file tool bucket; one source per session owns tool calls; paired duration comes from the file source | KyberDash | architecture | `docs/dash/architecture.md` (the Factory pairing passage that cites `splitDroidSessionRecords`) |
| H3 | ADR 0055 amendment: measured plugin tool-anchor collapse and start=end timing; transcript reader is part of the file source; double-count rule | KyberDash | adr | `docs/adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md` (dated amendment) |
| H4 | MiniMax M3.1 Flash rate and its provenance | KyberDash | reference | `docs/dash/telemetry-inventory.md` (same Factory section, cost line) |
| H5 | Slices 2 and 3 as authorized deferred work | KyberDash | todo | two new todos under `docs/todo/`, registered in `docs/todo/README.md` |

## Test contract (test-first)

All commands run from the repository root. Paths after `--` are relative to `dash/`. Every run must report a non-zero test count.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T2 | `dash/src/synth/readers/droid.test.ts` (describe `droidReader`); `dash/src/providers/droid.test.ts` (describe `droid provider data completeness`); `dash/src/synth/provider.test.ts` (describe `droid content reader registration`); `dash/src/synth/synth.test.ts` (describe `session title stamping`); `dash/src/refresh/registry.test.ts` (describe `droid parser contract`) | `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/synth/readers/droid.test.ts src/providers/droid.test.ts src/synth/provider.test.ts src/synth/synth.test.ts src/refresh/registry.test.ts` | Reader yields one turn per assistant entry keyed by its id, with tool calls, results (`isError`) and timestamps. Provider sets `pairingId`, `activeDurationMs` per D4 (absent, never 0, when not computable) and `sessionTitle`; dedup key and `nativeRecordIdentity` unchanged. `PROVIDER_READERS.get('droid')` is the reader, and a Droid fixture synthesizes one `tool.invoke` record per `tool_use`. A call with `sessionTitle` yields `raw['kyber.session.label']`, and without one yields no key. `parserContractVersionFor({ providerName: 'droid', harnessId: 'droid' })` is `'2'`. | The run fails: the reader module is missing, `pairingId`/`activeDurationMs`/`sessionTitle` are undefined, the registry returns `'1'`. Failure output recorded in the task digest. | Same command passes with assertions unchanged; then typecheck, lint and `check:reachable` pass. |
| T4 | `dash/src/canon/droid-pairing.test.ts` (describe `file tool bucket`); `dash/src/canon/sessions.droid-pairing.test.ts` (describe `paired droid tools, duration and title`); `dash/src/canon/naming.test.ts` (describe `factory and droid machine labels`) | `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/droid-pairing.test.ts src/canon/sessions.droid-pairing.test.ts src/canon/naming.test.ts` | Split puts `codeburn/` Droid `tool.invoke` records in `fileToolRecords` (and still in `counterRecords`); inert for other harnesses. Paired session with 1 plugin `factory.tool` + 3 file tool records: `tool_calls` 3, `tools_invoked` the file names. Paired with none: plugin count kept. Paired `duration_ms` = sum of the file records' `durationMs`. A non-Droid fixture's summary is unchanged. A record labelled `kyber.session.label` 'Fix login bug' yields payload `label` 'Fix login bug'. `PROJECTED_ATTRIBUTE_KEYS` has `kyber.session.label`. `isMachineLabel` is true for `factory.turn`, `factory.session`, `factory.tool`, `droid:minimax-m3.1-flash`, and false for `droid: fix the build`. `formatSessionTitle({ label: 'droid:minimax-m3.1-flash', harness: 'droid' })` is `Droid session`. | The run fails on: the missing `fileToolRecords`, `tool_calls` 1, `duration_ms` 0, the missing label key, and the machine labels returning false. | Same command passes unchanged; then typecheck and lint pass. |
| T6 | `dash/src/pricing/minimax.test.ts` (describe `MiniMax model pricing`, new case); `dash/src/pricing/data/pricing-fallback-data.test.ts` (describe `MiniMax M3.1 Flash bundled rate`) | `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/pricing/minimax.test.ts src/pricing/data/pricing-fallback-data.test.ts` | `getModelCosts('minimax-m3.1-flash')` and `getModelCosts('MiniMax-M3.1-Flash')` both give input 0.15e-6, output 0.6e-6, cache read 0.015e-6, `cacheWriteCostIsExplicit` false. The snapshot carries the key, its provenance cites `https://platform.minimax.io/` with retrieval `2026-11-09`, and the existing `MANUAL_ENTRIES` reproducibility loop covers it. | `getModelCosts` returns null; the provenance key is absent. | Same command passes unchanged. |
| T7 | No test: documentation task | `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .`, run in the worktree | Harvest H1 to H5 written; plan archived with its closeout mapping; task file removed | n/a (no-test task, replaced by the two docs gates) | Both gates report zero findings |

## Implementation tasks

Every developer task ends with the worker's own `task-gate` run. Standards to fetch: `docs_get_coding_standard_for_technology('typescript')` for typescript-dev, `('test')` for test-dev.

### T1 (test-dev): failing tests for the Droid collector, the title stamp and the parser contract

**Session:** `red-contract`. **Depends on:** none. **Required skills:** `test-dev`

**Files:**

- `dash/src/synth/readers/droid.test.ts` (new; describe `droidReader`; inline JSONL fixtures: a `session_start` entry with `title`, an assistant `tool_use`, a user `tool_result` with `is_error`, one malformed line)
- `dash/src/providers/droid.test.ts` (describe `droid provider data completeness`; build the provider with `createDroidProvider` over a temporary Factory directory)
- `dash/src/synth/provider.test.ts` (describe `droid content reader registration`; `PROVIDER_READERS`)
- `dash/src/synth/synth.test.ts` (describe `session title stamping`; the synthesis entry the file already exercises)
- `dash/src/refresh/registry.test.ts` (describe `droid parser contract`; `parserContractVersionFor`)

**Acceptance criteria:** every assertion in the T2 Test-contract row exists and fails for the missing behaviour, not for a fixture error. Run the T2 row command and report the failing count and messages.

### T2 (typescript-dev): Droid reader, provider fields, title stamp, contract bump

**Session:** `green-collector`. **Depends on:** T1. **Required skills:** `typescript-dev`

**Files:**

- `dash/src/synth/readers/droid.ts` (new, exported `droidReader`)
- `dash/src/providers/droid.ts` (`createParser`: set `pairingId`, `activeDurationMs` and `sessionTitle` on each call; read `title` from the `session_start` entry)
- `dash/src/providers/types.ts` (`ParsedProviderCall`: new optional `sessionTitle`)
- `dash/src/synth/provider.ts` (`PROVIDER_READERS`)
- `dash/src/synth/synth.ts` (`synthesizeRecord`: raw stamp of `SESSION_LABEL_KEY`)
- `dash/src/refresh/registry.ts` (new droid contract constant at '2' with a v2 comment; `parserContractVersionFor`)

**Acceptance criteria:** the T2 Test-contract row passes with T1's assertions unchanged; the dedup key and record identity are unchanged for existing calls; typecheck, lint and the reachability check pass.

### T3 (test-dev): failing tests for the pairing split, projection and naming

**Session:** `red-contract`. **Depends on:** none. **Required skills:** `test-dev`

**Files:**

- `dash/src/canon/droid-pairing.test.ts` (describe `file tool bucket`)
- `dash/src/canon/sessions.droid-pairing.test.ts` (describe `paired droid tools, duration and title`; canonical-record fixtures built with the file's existing helpers, plugin records under source kyberdash-factory-plugin and file records under the codeburn droid source)
- `dash/src/canon/naming.test.ts` (describe `factory and droid machine labels`)

**Acceptance criteria:** every assertion in the T4 Test-contract row exists and fails for the missing behaviour. Run the T4 row command and report the failing count and messages.

### T4 (typescript-dev): pairing split, paired tools and duration, machine labels

**Session:** `green-projection`. **Depends on:** T3. **Required skills:** `typescript-dev`

**Files:**

- `dash/src/canon/droid-pairing.ts` (`DroidSessionSplit`, `splitDroidSessionRecords`)
- `dash/src/canon/sessions.ts` (`buildSessionRow`: tool source and duration for a paired Droid session only)
- `dash/src/canon/naming.ts` (`isMachineLabel`)
- `dash/src/canon/store-rows.ts` (`PROJECTED_ATTRIBUTE_KEYS`)

**Acceptance criteria:** the T4 Test-contract row passes unchanged; unpaired and non-Droid sessions are byte-identical, so the existing pairing and naming cases stay green; typecheck and lint pass.

### T5 (test-dev): failing tests for the MiniMax M3.1 Flash rate

**Session:** `red-contract`. **Depends on:** none. **Required skills:** `test-dev`

**Files:**

- `dash/src/pricing/minimax.test.ts` (describe `MiniMax model pricing`, new case `returns official pricing for MiniMax-M3.1-Flash`)
- `dash/src/pricing/data/pricing-fallback-data.test.ts` (new describe `MiniMax M3.1 Flash bundled rate`: snapshot tuple and provenance)

**Acceptance criteria:** the T6 row command fails because the rate key and its provenance are absent.

### T6 (typescript-dev): bundle the MiniMax M3.1 Flash rate and invalidate the Droid report cache

**Session:** `green-pricing`. **Depends on:** T5. **Required skills:** `typescript-dev`

**Files:**

- `dash/scripts/bundle-litellm.mjs` (`MANUAL_ENTRIES`)
- `dash/src/pricing/data/litellm-snapshot.json` (new MiniMax M3.1 Flash key)
- `dash/src/pricing/data/pricing-provenance.json` (its source and retrieval date)
- `dash/src/ingest/session-cache.ts` (droid entry of `PROVIDER_PARSE_VERSIONS`)

**Acceptance criteria:** the T6 Test-contract row passes unchanged and the `MANUAL_ENTRIES` reproducibility loop covers the new key; the networked bundler is not run; the session-cache tests and typecheck pass.

### T7 (docs-dev): closeout

**Session:** `docs`. **Depends on:** T2, T4, T6. **Required skills:** `docs-dev`

**Files:**

- `docs/dash/telemetry-inventory.md` (harvest H1 and H4)
- `docs/dash/architecture.md` (harvest H2)
- `docs/adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md` (harvest H3, dated amendment)
- `docs/todo/` (harvest H5: the slice 2 and slice 3 todos and their index rows)
- `docs/plans/README.md` (row removed at closeout)
- `docs/plans/droid-kyberdash-data-completeness.md` (closeout mapping, then moved to the archive)
- `docs/plans/droid-kyberdash-data-completeness.tasks.json` (removed at closeout)
- `docs/archive/plans/` (archived plan and its inventory row)

**Acceptance criteria:** harvest H1 to H5 written, the plan archived with its closeout mapping, the task file removed, and both `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` report zero findings in the worktree.

## Dependency graph

```text
T1 ─▶ T2 ─┐
T3 ─▶ T4 ─┼─▶ T7
T5 ─▶ T6 ─┘
```

- **RED.** T1, T3 and T5 share the `red-contract` session; none needs an earlier GREEN.
- **GREEN.** T2, T4 and T6 have disjoint file scopes and run in parallel, within `review.max-concurrent-workers` (4).

## Risks

- **Re-synthesis volume.** The contract bump re-reads every Droid transcript once (about 364 files, 43k records). This is bounded and the established repair path (ADR 0016).
- **Tool records outside the timeline.** In a paired session, file tool records are counted but are not nested under plugin turns in the timeline. Accepted; the timeline stays plugin-owned.
- **Duration definition (D4)** is low-confidence. A wrong choice is a one-function revert.
- **Mixed-model sessions** stay priced at the session's last model, the existing known limitation in `createParser`.

## Out of scope

- Slice 2: native metric projection and its panel.
- Slice 3: Factory plugin contract 2 (D2).
- Any change under `dash/web/**`, `dash/tray/**` or `dash/src/capture/harnesses/factory-plugin/**`.
- Tools offered, skills, repo and branch, and the context-limit source for Droid.
- Reconciling Droid's own `droid.tool.invocations` against the transcript count.

## Verification and review

- **Per task.** Each GREEN task runs its row command, typecheck and lint, then its `task-gate`.
- **End of run.** One `code-reviewer` council over the whole change, governed by the `code-review` skill. The plan carries no review command.
- **Live check, owner-run, not a CI gate.** After `kyberdash` refreshes, a Droid session in the dashboard shows a non-zero duration, a cost for a MiniMax M3.1 Flash session, a tool count in the order of Droid's own, and the session's Droid title.

## Closeout mapping

**Completion summary (2026-11-09).** Tasks T1 to T6 are complete. The red tests (T1, T3, T5) were
written first. The green changes landed as T2 (commit `be0ef100`), T4 (`1d14c419`) and T6
(`e738f529`). The conductor's integrated branch ran 365 targeted tests green, with typecheck and
lint clean. T7 wrote harvest items H1 to H5, archived this plan and removed its task file.

**Acceptance criteria.** H1 to H5 are written to the destinations below. The closeout mapping
exists. The task file is removed. The two documentation gates are run in the worktree, and their
results are in the closeout digest.

**Low-confidence rows for review.** D4 (active duration definition) and D9 (cache-creation tokens
passed through unchanged). Neither is confirmed against Factory's documentation.

**Consent outcome to know.** The transcript content reader files assistant text as content parts.
The consent file does not govern file-source content, the same as the file source's first-message
fragment. The telemetry inventory's Content row states this.

**Decisions**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [ADR 0055, amendments of 2026-11-09](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md); [telemetry inventory, Factory section](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path) | extends-component | KyberDash | 2026-11-09 | archived |
| D2 | [factory-native-metrics-projection.md](../../todo/factory-native-metrics-projection.md) and [factory-plugin-contract-2.md](../../todo/factory-plugin-contract-2.md), in the [todo index](../../todo/README.md); no ADR — ADR waiver: work sequencing, not an architectural constraint | extends-component | KyberDash | 2026-11-09 | archived |
| D3 | [architecture, the Factory source-precedence rule](../../dash/architecture.md#the-factory-source-precedence-rule); [ADR 0055, double-count amendment](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md) | extends-component | KyberDash | 2026-11-09 | archived |
| D4 | [architecture, the Factory source-precedence rule](../../dash/architecture.md#the-factory-source-precedence-rule); [telemetry inventory, Factory file source](../../dash/telemetry-inventory.md#what-the-file-source-measures); no ADR — ADR waiver: refines the file source's behaviour within ADR 0055 | extends-component | KyberDash | 2026-11-09 | archived |
| D5 | [telemetry inventory, Factory file source, pairing key and contract](../../dash/telemetry-inventory.md#what-the-file-source-measures); no ADR — ADR waiver: follows the existing contract-bump and re-synthesis pattern (ADR 0016) | extends-component | KyberDash | 2026-11-09 | archived |
| D6 | [telemetry inventory, Factory file source, session title](../../dash/telemetry-inventory.md#what-the-file-source-measures); no ADR — ADR waiver: naming-rule detail within the existing naming module | extends-component | KyberDash | 2026-11-09 | archived |
| D7 | [telemetry inventory, Factory file source, cost](../../dash/telemetry-inventory.md#what-the-file-source-measures); no ADR — ADR waiver: rate data in the bundled table, provenance in `pricing-provenance.json` | extends-component | KyberDash | 2026-11-09 | archived |
| D8 | [telemetry inventory, Factory section introduction](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path); no ADR — ADR waiver: no architectural decision, display unchanged | extends-component | KyberDash | 2026-11-09 | archived |
| D9 | [telemetry inventory, Factory file source, token counters](../../dash/telemetry-inventory.md#what-the-file-source-measures) (unchanged); the unverified check is filed in [factory-native-metrics-projection.md](../../todo/factory-native-metrics-projection.md); no ADR — ADR waiver: no code change | extends-component | KyberDash | 2026-11-09 | archived |
| D10 | [telemetry inventory, Factory file source, pairing key and contract](../../dash/telemetry-inventory.md#what-the-file-source-measures); no ADR — ADR waiver: follows the established cache-version bump pattern | extends-component | KyberDash | 2026-11-09 | archived |
| D11 | [ADR 0055, amendments of 2026-11-09](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md): no new ADR, ADR amended in place | extends-component | KyberDash | 2026-11-09 | archived |
| D12 | Documentation waiver: process metadata with no surviving architectural, operational or behavioural invariant; the test contract stays in this archived plan. No ADR — ADR waiver: development process, not architecture | extends-component | KyberDash | 2026-11-09 | archived |
| D13 | Documentation waiver: approval record, transient with no surviving invariant. No ADR — ADR waiver: process decision | extends-component | KyberDash | 2026-11-09 | archived |

**Design harvest**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1 | [telemetry inventory, Factory section](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path): tools, content, duration, session title and pairing key rows | extends-component | KyberDash | 2026-11-09 | archived |
| H2 | [architecture, the Factory source-precedence rule](../../dash/architecture.md#the-factory-source-precedence-rule): the three buckets, the one-source tool rule and the file-source duration | extends-component | KyberDash | 2026-11-09 | archived |
| H3 | [ADR 0055, three amendments of 2026-11-09](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md): measured tool-span collapse and start-equals-end timing; the transcript reader as part of the file source; the double-count rule | extends-component | KyberDash | 2026-11-09 | archived |
| H4 | [telemetry inventory, Factory file source, cost](../../dash/telemetry-inventory.md#what-the-file-source-measures): the MiniMax M3.1 Flash rate and its provenance | extends-component | KyberDash | 2026-11-09 | archived |
| H5 | [factory-native-metrics-projection.md](../../todo/factory-native-metrics-projection.md) and [factory-plugin-contract-2.md](../../todo/factory-plugin-contract-2.md), in the [todo index](../../todo/README.md); no ADR — ADR waiver: deferred work, not a decision | extends-component | KyberDash | 2026-11-09 | archived |

The task file `droid-kyberdash-data-completeness.tasks.json` was removed at closeout.
