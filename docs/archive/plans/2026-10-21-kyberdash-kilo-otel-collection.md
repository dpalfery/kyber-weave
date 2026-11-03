---
id: plans/2026-10-21-kyberdash-kilo-otel-collection
title: "KyberDash Kilo Code collection: Settings status row, kilo.db collector repair and a baseline content reader"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-21
development-mode: test-first
autonomy: autonomous
---

# KyberDash Kilo Code collection: Settings status row, kilo.db collector repair and a baseline content reader

**Status: Complete 2026-10-21.** T1, T2 and T3 shipped, the F1 and F3 findings are fixed in the pass, and the durable decisions are harvested into canonical documentation ([section 12](#12-completion-summary-and-closeout)). The file's move to `docs/archive/plans/` and the plan-index row swap were the last mechanical steps, deferred at closeout because that harness had no shell or file-creation capability; they have since landed, which is why this file sits under `docs/archive/plans/`. The user chose **approve and execute** on 2026-10-21. That choice explicitly accepted the reframed scope (D2–D5): a status-only Kilo Code Settings row, the kilo.db collector repair and the baseline content reader, with no Kilo OTLP toggle or adapter. The approval is recorded as AP1 in [section 3](#3-decisions). Decisions D1–D13 were settled autonomously and are covered by that approval. No decision is open. Frontmatter `status: complete` now carries the lifecycle state the plan index labelled Complete while the file sat in `docs/plans/`. Previously **Ready 2026-10-21** and **Draft 2026-10-21**.

**Development mode:** `test-first`. The user did not opt out, so the default was persisted. Every implementation task has a row in the [Test contract](#6-test-contract-test-first), and RED evidence comes before GREEN.

**Autonomy:** `autonomous`. The conductor supplied this mode. Decisions carry the provenance `autonomous: recommendation accepted` where an authoritative source answered, and `autonomous: low-confidence, no authoritative source` where none did.

## 1. Problem and goal

The user's request was: *"kyberdash is not collecting kilo app data. build and execute a PLAN: update the dashboard setting to turn on otel data collection and build a collector for the kyberdash collector and a baseline reader for the dashboard."*

Investigation confirmed the symptom but not the assumed cause (see [section 2](#2-investigation-findings)):

- On this machine, `~/.kyberdash/canon.db` holds **zero** Kilo records and **zero** Kilo `source_checkpoint` rows.
- `~/.local/share/kilo/kilo.db` holds 77 root sessions, six of them from 2026-10-19 to 2026-10-21, each carrying session-level token and cost columns.
- Scheduled folder import (`settings.folder_import.scheduled`) is `off`.
- The last manual import ran on 2026-10-18 with a one-week window.
- Kilo has no live path into KyberDash at all.
- Kilo's OpenTelemetry export cannot supply that path. Upstream Kilo turns off AI SDK span recording, so its OTLP stream carries no model calls, tokens or content. Its only on/off key, `experimental.openTelemetry`, controls Kilo's own product analytics and is not a local OTLP switch.

The goal is to make Kilo Code data actually reach the dashboard through the existing architecture, and to say honestly in the Settings drawer how Kilo is collected. Each of the user's three asks maps onto that goal as follows:

| User ask | Maps to (verified) | Task |
|---|---|---|
| "update the dashboard setting to turn on otel data collection" | A `kilo` row in the Settings drawer, served by `/api/kyber/capture` from the capture registry. It is **status-only**: it shows install evidence, explains that Kilo's OTLP carries no usage, and gives the one action that does collect Kilo (folder import). Under ADR 0031 decision 1 it cannot be a writable toggle, because Kilo has no config key that turns on local telemetry export (D2, D3). | T3 |
| "build a collector for the kyberdash collector" | KyberDash's collector for Kilo is the engine's harness-source job (`dash refresh` / `import-history` → `kilo-shared-runtime`, `kilo-vscode-legacy`). It is not an OTLP adapter, because Kilo's OTLP has no model evidence and no attribute that tells it apart from OpenCode (D4). The task pins the job against the **live** kilo.db schema and stops a present-but-unreadable store from failing silently (R1.3). | T1 |
| "a baseline reader for the dashboard" | `dash/src/synth/readers/kilo.ts`, the Kilo `ContentReader`. Today it is an empty stub whose stated reason ("empty store, undocumented OTel") is no longer true. Under ADR 0029 the file reader is the baseline content source that OTel overrides per bucket. The task replaces the stub with a reader of kilo.db `message` and `part` rows (D5). | T2 |

## 2. Investigation findings

Findings were read from the tree at `main` HEAD `50e5ae4` unless marked otherwise. "Self-gathered" marks external lookups the architect made directly, because no delegation tool was available in this harness.

1. **Settings drawer and `/api/kyber/capture`.** The drawer (`dash/web/src/components/settings/SettingsDrawer.tsx`, `HarnessCollectionList.tsx`) renders what `GET /api/kyber/capture` returns (`dash/src/server/capture-routes.ts`). It parses that response defensively in `dash/web/src/lib/kyberApi.ts` (`readHarness`): an unknown `mode` falls back to `status-only`, and `CAPTURE_MAX_HARNESSES = 64`. The route builds its status from `CAPTURE_HARNESS_REGISTRY` (`dash/src/capture/harnesses/registry.ts`). `POST` accepts only harnesses whose writer is `kind: 'managed'` (`MANAGED_IDS` in `capture-routes.ts`). A `kind: 'pending'` writer shows up automatically as a read-only `status-only` row with `installEvidence`, `statusLines` and `manualSteps` (`buildOne` in `dash/src/capture/status.ts`). Adding a status-only harness therefore needs **no web change**.
2. **Per-harness key model and receiver coupling.** The registry declares six writers. Three are managed: `copilot`, `claude-code` and `opencode`, the last writing only `experimental.openTelemetry` in `~/.config/opencode/opencode.jsonc`. Three are pending: `codex`, `pi` and `antigravity`. Install detection lives in `groupsFor` (`dash/src/capture/detect.ts`), where an unknown id falls back to "its one config file". Manual steps live in `manualStepsFor` (`status.ts`). OpenCode's endpoint step names `OTEL_EXPORTER_OTLP_ENDPOINT`, which is env-only, so it is never written. Antigravity's step carries no env at all. Under ADR 0031 (2026-10-11 amendment), receiver auto-enable happens only on a successful managed enable through the route, and a status-only row never couples to the receiver. ADR 0031 decision 1 says capture writes "only the keys that enable telemetry export".
3. **Kilo in the code today.**
   - *File ingest:* provider `kilo-code` (`dash/src/providers/kilo-code.ts`) uses `discoverSqliteSessions` / `createSqliteSessionParser` (`dash/src/providers/sqlite-session-parser.ts`) over `$XDG_DATA_HOME|~/.local/share/kilo/kilo*.db`. It also covers the legacy Cline-style task tree under the VS Code `kilocode.kilo-code` globalStorage. Refresh harnesses are `kilo-shared-runtime` and `kilo-vscode-legacy`, with `KILO_PARSER_CONTRACT_VERSION = '3'` (`dash/src/refresh/registry.ts`). Measurability aliases them to `kilo-code` (`dash/src/canon/measurability.ts`).
   - *Silent discovery:* `discoverSqliteSessions` `continue`s past a database that will not open or fails its schema check, and swallows any query error (`catch { // skip this DB }`). A present-but-unreadable store therefore turns into "no sessions", which contradicts R1.3 as documented in `dash/src/synth/provider.ts`.
   - *Reader:* `kiloReader` (`dash/src/synth/readers/kilo.ts`) yields nothing. `KILO_NOT_COLLECTABLE_REASON` says "no session data in its empty store, and its OTel format is undocumented", and `kilo.test.ts` pins that text. `READER_UNMEASURABLE` marks every canonical content bucket as unmeasurable for `kilo` and `kilo-code`.
   - *Pairing:* the shared SQLite parser sets neither `turnId` nor `pairingId` (it sets only `deduplicationKey`), so reader turns would pair by position (`matchingTurns` in `synth/provider.ts`). That is fragile, because `buildAssistantCall` can return `null` for some assistant messages. The parser is shared with the `opencode` provider (`dash/src/providers/opencode.ts`).
   - *OTLP:* there is no Kilo adapter. `ADAPTERS` (`dash/src/canon/ingest.ts`) lists pi, copilot, antigravity, gemini, claude-code, codex and opencode. `RawSpan` (`dash/src/canon/adapters/base.ts`) carries span attributes only. Resource attributes are not adapter evidence, and `service.name` becomes `source`, which R6.2 rules inadmissible as evidence.
4. **Kilo in the docs.** `docs/dash/telemetry-inventory.md` (Kilo Code row) describes the SQLite ingest and says "Undocumented live OTel surface remains uncollected". `docs/dash/architecture.md` describes Kilo's parser contract. No doc mentions Kilo in the capture or Settings sections.
5. **Kilo's OTel surface (self-gathered; upstream `Kilo-Org/kilocode` at `42530c80`, 2026-10-21, plus kilo.ai docs).**
   - *Export trigger:* OTLP export turns on **only** when `OTEL_EXPORTER_OTLP_ENDPOINT` is set in the process environment (`packages/core/src/observability/otlp.ts`). It sends traces to `/v1/traces` and logs to `/v1/logs`. No config key sets the endpoint: `packages/core/src/v1/config/config.ts` has no such field.
   - *Resource identity:* `service.name` is `"opencode"`. The resource attributes are `opencode.client` (`KILO_CLIENT`: `cli` by default, `vscode` from the extension), `opencode.run` and `deployment.environment.name`.
   - *No model spans:* AI SDK span recording is explicitly off. `packages/opencode/src/session/llm.ts` sets `experimental_telemetry: { isEnabled: false }` with the comment "disable AI SDK span recording (ai.* / gen_ai.*)", and `KiloAgent.telemetryOptions` returns `{ isEnabled: false }`. What gets exported is Effect application spans (`Session.updateMessage`, `Session.updatePart`, `Tool.execute` with `tool.name`, `tool.call_id`, `session.id`, `message.id`, and HTTP request spans) plus Effect logs. None of them carries token usage, a model or prompt content.
   - *`experimental.openTelemetry` is analytics:* it defaults to `true` and is read only by `Telemetry.init({ enabled: cfg.experimental?.openTelemetry !== false })` in `packages/opencode/src/kilocode/cli/setup.ts`. That is `@kilocode/kilo-telemetry`, which is PostHog product analytics. Its `src/` has no OTLP exporter.
   - *Config location:* global config is `~/.config/kilo/kilo.json[c]` (legacy `opencode.json[c]` there is also read). The VS Code extension spawns `kilo serve` with the inherited `process.env` plus `KILO_CLIENT=vscode` (`packages/kilo-vscode/src/services/cli-backend/server-manager.ts`).
6. **How KyberDash would see Kilo OTLP today.** Kilo `Session.*` spans match `opencodeAdapter.detect` (a `Session.` name prefix scores 1). A claimed span with op `unspecified` is quarantined as non-model under OpenCode. `Tool.execute` carries `tool.name`, not `gen_ai.tool.name`, so `canonicalOp` returns `unspecified`, and the span either inherits OpenCode by same-source pass 2 (source `opencode`) and is quarantined, or stays `unclaimed`. HTTP spans are structural. No Kilo OTLP span can become a record, and every one would add OpenCode quarantine noise.
7. **Local runtime evidence (read-only `sqlite3 -readonly` queries; no content read).**
   - *canon.db* (`~/.kyberdash/canon.db`, resolved by `resolveCanonDbPath`): per-harness record counts show no `kilo*` harness. `source_checkpoint` has no `kilo*` rows. The last `refresh_run` was on 2026-10-18 (`history_weeks` 1, nine jobs).
   - *Settings metadata:* `settings.folder_import.scheduled = off`, `settings.receiver.hosted = on`.
   - *kilo.db:* has `session`, `message`, `part`, plus newer `session_message`, `session_input`, `event` and `kilo_board*`. `session` gained `path`, `agent`, `model`, `cost`, `tokens_input|output|reasoning|cache_read|cache_write` and `metadata`. Recent sessions keep their messages in `message` (`session_message` is empty for them).
   - *Message and part counts:* 6,820 assistant and 643 user messages. The part types are `tool` (10,227), `step-start`, `step-finish`, `reasoning`, `text`, `patch`, `file` and `compaction`. Tool parts carry `tool`, `callID`, `state` (`status` ∈ `completed|error`) and `metadata`.
   - *Unexplained gap:* the 2026-10-11 two-week import window covered a 2026-10-08 Kilo session, yet no checkpoint exists. Why is not proven: a later per-harness clean is possible, and so is a silent discovery skip (finding 3). T1 settles it.
8. **The "baseline reader" alternatives considered.** The dashboard reads canon.db generically by harness through `/api/kyber/*` (`dash/src/server/routes.ts`, `bridge.ts`). Kilo harness ids already appear in measurability and rollups, so Kilo records surface once they exist, and no new route or API reader is needed. The only Kilo-specific reader seam that is empty is `kiloReader`.
9. **canon.db schema implications.** None. Records, provenance and checkpoints use existing tables. A parser contract bump (`'3'` → `'4'`) re-synthesizes Kilo units through existing checkpoint invalidation (architecture.md, parser contracts).
10. **Display-layer rule.** Every change lands in `dash/src/**`. The web drawer renders the new row from data. `dash/src/architecture/display-layer.test.ts` is unaffected.
11. **Tooling.**
   - *CodeGraph:* the CodeGraph MCP tool and the Kyber-Weave docs MCP tools (`docs_explore`, `docs_for_symbol`) were **not available** in this harness. CodeGraph was used through its documented shell fallback (`codegraph explore`, one call; `.codegraph/` present).
   - *Docs discovery:* documentation started from the documentation and plan indexes with targeted searches, as the root `AGENTS.md` allows when the docs server is unavailable.
   - *Direct reads:* about 44 targeted direct reads or searches of indexed code and docs were made. Reason: no MCP code or docs tool was loaded, and the single shell `codegraph explore` returned broad, mostly off-target symbols (Skills routing), so narrow file reads were cheaper.
   - *Upstream source:* Kilo's code was read from a sparse clone in `/tmp` (self-gathered).
   - *Tally:* Discovery: codegraph 1, docs 0, direct reads 44.

## 3. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What is the root cause of "not collecting kilo app data"? | Kilo is file-sourced only, and file-source import is manual on this machine (`settings.folder_import.scheduled = off`; last run 2026-10-18 with a 1-week window). A possible silent-discovery defect is not ruled out (finding 7). Kilo's OTLP cannot supply live data (finding 5). | Evidence: findings 3, 5 and 7. T1 makes the remaining uncertainty observable and fixes it in scope if real. | ANSWERED — autonomous: recommendation accepted |
| D2 | Should the Settings drawer get a writable Kilo "turn on OTel" toggle that writes `experimental.openTelemetry: true` to `~/.config/kilo/kilo.jsonc`, as OpenCode does? | **No.** Kilo is added as a **status-only** (`kind: 'pending'`) capture harness. | `experimental.openTelemetry` gates Kilo's PostHog product analytics, defaults to `true`, and is not a local export key (finding 5). Writing it breaches ADR 0031 decision 1 ("only the keys that enable telemetry export"), and could re-enable vendor analytics for an owner who opted out. Pending writers are the precedent for harnesses with no writable export key (Pi, Antigravity, Codex). Making it managed later, if Kilo ever ships a config-level export key, is additive. | ANSWERED — autonomous: recommendation accepted |
| D3 | What does the Kilo row say and offer? | id `kilo`, displayName `Kilo Code`, `resolvePath` = `<home>/.config/kilo/kilo.jsonc` (via `envJoin`, `.config` root on every OS, as for OpenCode). Install evidence: any of the config directory `<home>/.config/kilo`, the shared-runtime DB `<home>/.local/share/kilo/kilo.db`, or the VS Code Stable globalStorage `kilocode.kilo-code` directory. `statusLines` (static, no owner config text echoed): Kilo's OTLP export is env-gated and carries no model spans, usage or content (AI SDK span recording is off upstream), so KyberDash does not ingest Kilo OTLP, and Kilo usage is collected from kilo.db by folder import. One manual step, `kilo-folder-import`, with **no env**: turn on Scheduled folder import (Maintenance) or run `kyberdash dash import-history --harness kilo-shared-runtime`. | `buildOne` and `manualStepsFor` already carry exactly these fields (finding 1). The endpoint env var is deliberately **not** offered: setting it only produces OpenCode-attributed quarantine noise (finding 6). The VS Code globalStorage path reuses the `appData` and platform seam the Copilot paths use (D14 of the 2026-10-11 plan). | ANSWERED — autonomous: recommendation accepted |
| D4 | What is "the collector" for Kilo? Should a Kilo OTLP adapter be built? | The collector is the engine's existing Kilo harness-source job. **No OTLP adapter** is built in this plan. T1 hardens the job: a live-schema characterization and visible failure for a present-but-unreadable store. | Kilo OTLP has no model, token or content evidence (finding 5). Its attributes cannot be told apart from OpenCode's, and resource or `service.name` evidence is inadmissible under R6.2 (finding 3). An adapter would attribute nothing measurable. Revisit only if Kilo re-enables AI SDK spans. | ANSWERED — autonomous: recommendation accepted |
| D5 | What is "the baseline reader"? | Replace the `kiloReader` stub with a real `ContentReader` over `<kilo.db>:<sessionId>`. It reads `message` and `part` rows for the session tree and yields one `ReaderTurn` per token-bearing assistant message, with `nativeRecordId` = Kilo message id. Buckets: `conversation_history` from the preceding user text and the assistant `text` parts; `tool_result_content` from completed or errored `tool` parts' `state.output`; `toolCalls` / `toolResults` from `tool` parts (`callID`, `tool`, `state.input`, `state.output`, `state.status === 'error'` → `isError`). `system_prompt`, `tool_definitions` and `instruction_context` stay absent (kilo.db does not store them). `reasoning`, `patch`, `step-*`, `file` and `compaction` parts are not content buckets. | ADR 0029: the file reader is the per-bucket baseline that OTel overrides. Kilo has no OTel content, so this reader is the whole of Kilo's content. Finding 7 confirms the part shapes. A missing bucket stays absent, never zero (R10.1, R10.2, `rules/honest-unobservability`). | ANSWERED — autonomous: recommendation accepted |
| D6 | How do reader turns pair with parser calls? | The SQLite parser stamps `pairingId = <message id>` on calls **for `kilo-code` only**, through an opt-in field on `SqliteProviderConfig`. OpenCode's parser output stays byte-identical. | `turnId` is identity-bearing (`providers/types.ts` comments). `pairingId` is the pairing-only key `matchingTurns` already honours. Positional pairing is unsafe because `buildAssistantCall` may skip messages (finding 3). | ANSWERED — autonomous: recommendation accepted |
| D7 | Re-synthesis of existing Kilo units | Bump `KILO_PARSER_CONTRACT_VERSION` from `'3'` to `'4'`. Update `READER_UNMEASURABLE` for `kilo` and `kilo-code` to `['schema_cost', 'system_prompt', 'tool_definitions', 'instruction_context']`. | This is the documented checkpoint-invalidation mechanism (architecture.md, parser contracts). It needs no schema migration. | ANSWERED — autonomous: recommendation accepted |
| D8 | Should a present-but-unreadable kilo.db stay silent? | No. A kilo.db that exists but will not open, or that fails the required-table check, records exactly one `PROVIDER_PARSE_ERROR` problem attributed to `kilo-shared-runtime` that names the source. Other harnesses in the run continue. An **absent** store stays silent (R1.2). | R1.2 and R1.3 in `dash/src/synth/provider.ts`. Today's silent `continue` hides exactly the failure the user reported. The worker picks the seam (discovery throws or returns an error unit). The observable contract is fixed in the Test contract. | ANSWERED — autonomous: recommendation accepted |
| D9 | Should the fix also stop Kilo app spans from being claimed by the OpenCode adapter? | No, out of scope. It is recorded as a risk. | No admissible attribute tells them apart (finding 6), and the plan does not recommend setting the Kilo endpoint (D3), so the noise only appears when an owner opts in by hand. | ANSWERED — autonomous: recommendation accepted |
| D10 | Should Kilo's Settings row turn on `settings.folder_import.scheduled` itself? | No. The row points to the existing Maintenance switch. | That setting is global to every file harness, and the capture route owns harness telemetry keys only (ADR 0031 decision 5). Coupling it would widen the route's write scope without a record. | ANSWERED — autonomous: recommendation accepted |
| D11 | ADR form | Amend ADR 0031 in place with a dated paragraph: the harness scope gains `kilo` as status-only, with the reason. Update its `docs/adr/README.md` row. No new ADR. | Precedent: ADR 0031 was amended in place on 2026-10-10 and 2026-10-11. The reader and collector changes refine existing architecture (ADRs 0009, 0016, 0029), so they are recorded with an ADR waiver in closeout. | ANSWERED — autonomous: recommendation accepted |
| D12 | Should the Kilo row also detect VS Code Insiders, JetBrains or Windows `%APPDATA%` Kilo config locations? | Detect VS Code Stable globalStorage, config and DB only. Other editions count as "not detected" evidence lines, not errors. | Kilo docs say the Windows config directory "may vary". Without a verified path, the plan keeps the `.config` root used for OpenCode. | ANSWERED — autonomous: low-confidence, no authoritative source |
| D13 | Development mode | `test-first` (default; no opt-out). | Conductor packet. | ANSWERED — autonomous: recommendation accepted |
| AP1 | Approve and execute the Draft plan, including the reframed scope? | **Approved.** The user chose approve and execute. The plan moves to Ready and T1 and T3 are dispatchable. | The approval explicitly accepts the D2–D5 reframe: a status-only `kilo` capture row with no Kilo OTLP toggle and no Kilo config write, the collector defined as the kilo.db harness-source job with no Kilo OTLP adapter, and the baseline reader defined as a real `kiloReader`. It covers D1–D13 as recorded and the Test contract in section 6. Changing the Test contract or the scope returns the plan to Draft. | APPROVED — user-approved at the approve-and-execute gate, 2026-10-21 (relayed by the conductor), including explicit acceptance of the D2–D5 scope reframe |

## 4. Design

**T1, the collector (engine job, `dash/src/providers`, `dash/src/refresh`).**
- *Fixture:* extend the Kilo fixture database in `dash/src/refresh/issue-189-sessions.test.ts` (or a sibling `kilo-live-schema.test.ts` built the same way with `node:sqlite` under a temporary `XDG_DATA_HOME`) to the live schema in finding 7.
- *Ingest characterization:* drive the same refresh entry point the existing #227 test uses and assert that records and a checkpoint land.
- *Silent-failure fix:* make `discoverSqliteSessions`' silent skip for a present-but-unreadable DB observable as one `PROVIDER_PARSE_ERROR` (D8). The change goes through the existing problem path (`parseProblem` / refresh problem recording).
- *If ingest is already green:* the characterization stays as a regression pin. The completion summary then states that the gap was operational (D1).
- *If ingest is red:* fix it within `kilo-code.ts` / `sqlite-session-parser.ts`.

**T2, the baseline reader (`dash/src/synth/readers/kilo.ts`).**
- *Read path:* parse `<dbPath>:<sessionId>` exactly as the parser does (last `:` segment is the session id). Open read-only through `dash/src/ingest/sqlite.ts`, and reuse the same recursive session-tree CTE.
- *Turn yield:* yield turns in `time_created, id` order, honouring `dateRange` the way other readers do.
- *Removals:* drop `KILO_NOT_COLLECTABLE_REASON` and its pins. Nothing else imports it.
- *Pairing:* add the D6 `pairingId` stamp behind a `SqliteProviderConfig` flag set only in `kilo-code.ts`.
- *Re-synthesis:* apply the D7 contract bump and measurability update.

**T3, the Settings row (`dash/src/capture`).**
- *Writer:* new `harnesses/kilo.ts` exports `kiloHarness: PendingHarnessWriter` (D3), appended to `CAPTURE_HARNESS_REGISTRY` after `antigravity`.
- *Install detection:* a `case 'kilo'` in `detect.ts` `groupsFor`, with one probe group holding the three alternatives.
- *Manual step:* a `kilo` branch in `status.ts` `manualStepsFor`.
- *What does not change:* the route needs nothing, because `MANAGED_IDS` excludes pending writers and so `POST` rejects `kilo`. The web needs nothing (finding 1).
- *CLI side effects:* `capture status` gains a Kilo block. `capture enable --harness kilo` writes nothing and leaves no receipt, the same as Pi.

**T4, docs closeout (docs-dev).** See [section 10](#10-risks-scope-review-and-closeout).

## 5. Constraints that bind every task

- *Display layers:* no feature logic in `dash/web/**` or `dash/tray/**` (`docs/rules/kyberdash-display-layer.md`, ADR 0033). This plan edits neither.
- *One datastore, one API:* canon.db through `resolveCanonDbPath`, and `/api/kyber/*`. No new route.
- *Capture boundary:* capture writes no Kilo file (ADR 0031). `kilo` is pending.
- *No new dependencies:* SQLite access goes through the existing `node:sqlite` wrapper (`dash/src/ingest/sqlite.ts`).
- *Honest unobservability:* an absent bucket or counter is absent, never zero.
- *No assertion weakening:* existing assertions are not weakened. The only authorized edits to existing assertions are:
  - replacing the `kiloReader` stub pins in `dash/src/synth/readers/kilo.test.ts`, the contract being replaced;
  - updating count, roster or text pins that enumerate the six capture harnesses, so they enumerate seven;
  - updating pins of `KILO_PARSER_CONTRACT_VERSION === '3'` and of Kilo's `READER_UNMEASURABLE` row.
- *Docs gates:* `docs validate` and `docs drift` stay at zero findings after any edit under `docs/`.

## 6. Test contract (test-first)

Runner for every dash row: `cd dash && npx vitest run <file>`. The full gates are in [section 9](#9-verification-gates).

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1a | `dash/src/refresh/issue-189-sessions.test.ts` (new `describe('kilo live schema')`) or new `dash/src/refresh/kilo-live-schema.test.ts` | `cd dash && npx vitest run src/refresh/issue-189-sessions.test.ts` | A temp `XDG_DATA_HOME/kilo/kilo.db` with the live schema (finding 7: extra `session` columns, `session_message`, `session_input` and `event` tables, a root session with 2 assistant messages carrying tokens and `text` / `tool` parts) refreshed for `kilo-shared-runtime` over a window covering it. The run produces 2 canonical records with harness `kilo-shared-runtime`, the fixture's input and output token counts, and one `source_checkpoint` row with `record_count` 2. | Characterization: run before any source change and capture the output. If it fails, that failure is the RED, and the fix is in T1 scope. If it passes, record the pass as evidence for D1 in the task report. A pass is not a defect. | Same test passes after T1 with no assertion relaxed. |
| T1b | Same file as T1a | Same as T1a | A kilo.db that exists but is not a database (garbage bytes), and separately one missing the `part` table, yields exactly one `PROVIDER_PARSE_ERROR` problem attributed to `kilo-shared-runtime` that names the source. The refresh completes for another harness in the same run. An absent kilo directory yields no problem. | Test fails today: no problem is recorded (silent `continue` in `discoverSqliteSessions`). | Passes. The absent-store case stays problem-free. |
| T2a | `dash/src/synth/readers/kilo.test.ts` (rewritten) | `cd dash && npx vitest run src/synth/readers/kilo.test.ts` | `kiloReader.read('<tmp kilo.db>:<ses>')` yields one turn per token-bearing assistant message, in order, with `nativeRecordId` equal to the message id. `conversation_history` contains the user text. Tool `callID`, name and input surface as `toolCalls`. Completed output surfaces as `tool_result_content` and `toolResults`, and an errored tool sets `isError: true`. No `system_prompt` or `tool_definitions` part appears. A missing DB or session yields zero turns without throwing. | Fails today: the stub yields nothing. | Passes. |
| T2b | `dash/src/providers/kilo-code.test.ts` and `dash/src/providers/opencode.test.ts` | `cd dash && npx vitest run src/providers/kilo-code.test.ts src/providers/opencode.test.ts` | Kilo SQLite calls carry `pairingId` equal to the Kilo message id. OpenCode SQLite calls carry no `pairingId` and are otherwise unchanged. | The Kilo assertion fails today (no `pairingId`). | Both pass. |
| T2c | `dash/src/synth/provider.test.ts` (or `synth.test.ts`) plus the measurability pin in `dash/src/canon/measurability.test.ts` | `cd dash && npx vitest run src/synth/provider.test.ts src/canon/measurability.test.ts` | Ingesting a Kilo load through `PROVIDER_READERS` attaches the reader's parts to the matching call by `pairingId`. The synthesized record's `conversation_history` and `tool_result_content` are measured, while `system_prompt` and `tool_definitions` stay `not_measurable`. The Kilo contract version is `'4'`. | Fails today: the parts are absent, every bucket is unmeasurable, and the version is `'3'`. | Passes. |
| T3a | `dash/src/capture/harnesses/kilo.test.ts` (new), `dash/src/capture/detect.test.ts`, `dash/src/capture/status.test.ts` | `cd dash && npx vitest run src/capture/harnesses/kilo.test.ts src/capture/detect.test.ts src/capture/status.test.ts` | Under a temp HOME, structured status lists `kilo`: `mode: 'status-only'`, `state: 'not-applicable'`, `installed: 'detected'` with one evidence line when any one of the three D3 paths exists, and `'not-detected'` when none does. `statusLines` state that Kilo OTLP carries no model usage and that Kilo is collected from kilo.db. `manualSteps` holds one step, `kilo-folder-import`, with no `env`. Status echoes no text from `kilo.jsonc`. On a win32 `PathEnv`, the paths resolve with win32 separators. | Fails today: no `kilo` entry. | Passes. |
| T3b | `dash/src/capture/capture.test.ts`, `dash/src/capture/actions.test.ts`, `dash/src/server/capture-route.test.ts` | `cd dash && npx vitest run src/capture/capture.test.ts src/capture/actions.test.ts src/server/capture-route.test.ts` | `capture enable --harness kilo` and `disable` write no file under the temp HOME and leave no receipt entry. `GET /api/kyber/capture` includes the `kilo` status-only row. `POST {action:'enable', harness:'kilo', dryRun:true}` returns 400. | The new assertions fail today (unknown harness). | Pass. The existing six-harness pins are updated to seven only where they enumerate the roster (section 5). |
| T4 | No code test. Documentation only. | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .` | The docs listed in section 10 describe the shipped behavior, and both checks report zero findings. | Not applicable: a documentation task. Verified by the two checks and by review against the T1–T3 diffs. | Both checks pass. |

## 7. Tasks

| Task | Objective | Exact files or symbols | Acceptance criteria | Depends on | Required skills |
|---|---|---|---|---|---|
| T1 | Pin and repair the Kilo kilo.db collector job. Make present-but-unreadable stores visible. | `dash/src/providers/sqlite-session-parser.ts` (`discoverSqliteSessions`, `validateSchemaDetailed`), `dash/src/providers/kilo-code.ts`, `dash/src/refresh/source-reader.ts` or the problem-recording seam if a discovery error must be carried, and `dash/src/refresh/issue-189-sessions.test.ts` (or new `kilo-live-schema.test.ts`) | T1a and T1b GREEN. The OpenCode provider tests stay green. The task report states whether T1a was RED or green on first run (D1 evidence). | none | `test-dev` plus TypeScript (Node engine, `dash/src`) implementation |
| T2 | Ship the Kilo baseline content reader with id-based pairing and the contract bump. | `dash/src/synth/readers/kilo.ts`, `dash/src/synth/readers/kilo.test.ts`, `dash/src/providers/sqlite-session-parser.ts` (`SqliteProviderConfig`, the `buildAssistantCall` call site), `dash/src/providers/session-message.ts` if `pairingId` is threaded there, `dash/src/providers/kilo-code.ts`, `dash/src/refresh/registry.ts` (`KILO_PARSER_CONTRACT_VERSION`), `dash/src/canon/measurability.ts` (`READER_UNMEASURABLE`), and the tests named in T2a–T2c | T2a–T2c GREEN. `KILO_NOT_COLLECTABLE_REASON` is removed with no dangling import. `npm --prefix dash run check:reachable` is clean. | T1 (certain file overlap in `sqlite-session-parser.ts` and `kilo-code.ts`) | `test-dev` plus TypeScript (Node engine, `dash/src`) implementation |
| T3 | Add Kilo Code as a status-only capture harness so the Settings drawer shows it honestly. | New `dash/src/capture/harnesses/kilo.ts` and `kilo.test.ts`; `dash/src/capture/harnesses/registry.ts` (`CAPTURE_HARNESS_REGISTRY` and its header comment's harness count); `dash/src/capture/detect.ts` (`groupsFor`); `dash/src/capture/status.ts` (`manualStepsFor`); tests in T3a and T3b | T3a and T3b GREEN. `display-layer.test.ts` green. No file under `dash/web` or `dash/tray` changed. | none | `test-dev` plus TypeScript (Node engine, `dash/src`) implementation |
| T4 | Harvest durable decisions into canonical docs, then close out and archive the plan. | `docs/dash/telemetry-inventory.md` (Kilo row: OTel surface now documented, env-gated and no model spans; reader populated; capture status-only; upstream source links), `docs/dash/architecture.md` (capture registry harness list, Kilo status-only, Kilo reader and `pairingId`, contract `'4'`), `docs/dash/runbook.md` (capture section Kilo row; "Kilo shows no data" troubleshooting: folder import scheduled off, `import-history --harness kilo-shared-runtime`), `docs/adr/0031-…md` (dated amendment, D11), `docs/adr/README.md` row, this plan (completion summary, closeout mapping) and `docs/plans/README.md` (move to Archived, file to `docs/archive/plans/`) | `docs validate --merge-ready` and `docs drift` at zero findings. The closeout mapping lists every D-row's destination or waiver. | T1, T2, T3 | `docs-dev`, `kyber-weave-docs`, `architecture-decision-record` |

Each task has its own `task-reviewer`, which starts when that task's worker finishes. The repository has no TypeScript-specific skill, so the conductor maps the TypeScript implementation work for T1–T3 to the live specialist inventory.

## 8. Dependency graph

```text
T1 ──► T2 ──┐
            ├──► T4
T3 ─────────┘
```

- T1 and T3 start together. Their file scopes are disjoint: `dash/src/providers` and `dash/src/refresh` for T1, `dash/src/capture` for T3.
- T2 waits for T1 only because both edit `sqlite-session-parser.ts` and `kilo-code.ts`.
- T4 consumes the shipped behavior of all three.
- Within each task, RED comes before GREEN, per the Test contract.

## 9. Verification gates

Run after T1–T3 and again after T4:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli -c Release -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .
```

The plan touches no .NET, tray or release path, so the .NET build and test gates and `scripts/update-loop.sh` are not required. Run them if a worker's diff reaches outside `dash/src` or `docs/`.

**Owner gates.** These are live and not automatable, and they are reported as run or not run in closeout:

1. Against a **temporary** store: `KYBERDASH_VERBOSE=1 kyberdash dash import-history --db /tmp/kilo-check.db --harness kilo-shared-runtime --weeks 16`. Expect Kilo records for the 2026-07 to 2026-10 sessions. The owner's live `canon.db` is not touched.
2. In the web dashboard, the Settings drawer shows a read-only **Kilo Code** row with the folder-import step.
3. After a real import, a Kilo session's Context view shows conversation and tool-result content.

## 10. Risks, scope, review and closeout

**Risks.**

- *R1:* T1a may pass on first run. If so, the user-visible gap is operational: scheduled folder import is off. The plan surfaces that in the Settings row and the runbook, but does not flip the global setting (D10).
- *R2:* Kilo's schema keeps evolving (`session_message` exists but is unused for current sessions). If Kilo moves messages there, both parser and reader read nothing for new sessions. T1 pins today's schema, and a future move is a contract bump.
- *R3:* An owner who sets `OTEL_EXPORTER_OTLP_ENDPOINT` for Kilo by hand will see Kilo app spans quarantined under the OpenCode source (D9).
- *R4:* the D12 Windows and other-edition detection is low-confidence.
- *R5:* the contract bump re-parses every Kilo unit in the next window, a one-time cost.

**Out of scope.**

- A Kilo OTLP adapter, or any change to attribution or `RawSpan`.
- Writing any Kilo config key.
- Changing `settings.folder_import.scheduled` defaults.
- Web or tray changes.
- The `kilo-vscode-legacy` Cline-style reader (it keeps the Cline path).
- `session_message` support.

**Review.** Each task gets a per-task `task-reviewer`. A final review checks:

- the display-layer rule;
- ADR 0031's write scope;
- the absence of zero-for-absent figures;
- that no assertion was weakened beyond section 5's authorized pins.

**Closeout (docs-dev, T4).** Harvest these to canonical docs:

| Decision | Destination |
|---|---|
| D2, D3 and D10 | ADR 0031 amendment, architecture.md capture section, runbook capture section |
| D4 and D9 | telemetry-inventory Kilo row and OTLP note |
| D5–D7 | architecture.md synth readers and parser contracts, telemetry-inventory |
| D8 | architecture.md refresh error handling |
| D9 and F5 (R3: a hand-set Kilo `OTEL_EXPORTER_OTLP_ENDPOINT` is quarantined under the OpenCode source) | telemetry-inventory Kilo row and OTLP note, plus runbook capture section (the Kilo row and "Kilo shows no data" troubleshooting) |
| F2 (a `harness:<id>:job` `PROVIDER_PARSE_ERROR` row persists after the store heals) | Runbook troubleshooting note: the row stays until a per-harness Clean, because `reconcileSourceProblems` leaves job rows alone by design. Follow-up in a new `docs/todo/` entry |
| F3 (per-record measurability for `kilo-vscode-legacy`, after the in-run fix) | architecture.md synth readers and measurability: legacy Kilo per-record stamps key on the harness id, while `call.provider` and dedup identity are unchanged |
| D12 / F4 (low-confidence Kilo install detection) | runbook capture section (which paths count as "detected") and the plan completion summary, flagged in the final report |
| D1 and the owner-gate results | Plan completion summary only (transient) |

Waivers:

- `no ADR — ADR waiver: reader, pairing and collector hardening refine ADRs 0009, 0016 and 0029 within existing patterns.` The capture-scope change is recorded by the ADR 0031 amendment.
- No documentation waiver.

## 11. Findings mediation (2026-10-21)

Run findings were mediated by the architect after T1–T3 completed, with the full dash suite at 6182/6182 green. Before dispatch the Ready queue was empty. These dispositions do not change the approved scope or the Test contract in section 6. The two in-run fixes restore the section 5 constraints: the honest-unobservability rule and accurate in-code roster text.

| ID | Finding | Disposition | Reason and worker scope |
|---|---|---|---|
| F1 | `dash/src/capture/index.ts` comments (lines 3 and 62) still say "the six D6 harnesses" | FIX_IN_RUN | T3 made these comments stale. Scope: T3 worker, comment-only edit in `dash/src/capture/index.ts` so the text says the registry roster without a hard-coded count (or "seven"). No behaviour change, no test. Gate: `npm --prefix dash run lint` and `typecheck`. |
| F2 | The job-level `harness:<id>:job` `PROVIDER_PARSE_ERROR` row is not removed when the store heals | DEFER_TO_TODO, plus a runbook note in T4 | Pre-existing for every harness. `reconcileSourceProblems` (`dash/src/canon/store.ts`) leaves the job row alone by documented design. Changing that is a cross-harness contract change outside the approved scope. T1 only makes it reachable for Kilo, and D8's "exactly one problem" acceptance holds. T4 creates `docs/todo/kyberdash-job-problem-reconciliation.md` with its todo-index row, and adds the runbook workaround (per-harness Clean removes the harness's problem rows). |
| F3 | `kilo-vscode-legacy` per-record measurability stamps omit the conversation and tool-result limitation | FIX_IN_RUN | This is an **in-run regression, not a residual**. T2 changed the `kilo-code` `READER_UNMEASURABLE` row, and `synthesizeRecord` stamps `measurabilityFor(call.provider)` (`dash/src/synth/synth.ts`, about line 523). Legacy task-tree records therefore became measurable-by-absence with no parts, which breaks `rules/honest-unobservability` (section 5). The rejected rename of `call.provider` stays rejected because of the identity reason given. Scope: T2 worker. Choose the per-record measurability key from the in-scope `harness` (`harnessIdFor(call, envelope)`) **only** when it is `kilo-vscode-legacy`, or through an equivalently narrow Kilo-family rule. Leave `call.provider`, `deduplicationKey`, `nativeRecordIdentity` and every other harness's stamps untouched. Add a regression assertion in `dash/src/synth/provider.test.ts` or `synth.test.ts`: a legacy envelope record stamps every canonical content bucket `not_measurable`, a `kilo-shared-runtime` record keeps conversation and tool-result content measured, and a non-Kilo envelope's stamps are unchanged. This adds an assertion; nothing is weakened. Gate: the full dash suite. |
| F4 | D12 Kilo install detection is low-confidence | ACCEPTED_RESIDUAL | Already recorded as `autonomous: low-confidence, no authoritative source`, and Kilo documents no Windows or other-edition path. Flag it in the final report. T4 documents which paths count as "detected". |
| F5 | R3: a hand-set Kilo endpoint quarantines app spans under the OpenCode source | DOCUMENT_IN_CLOSEOUT | Out of scope by D9. The T4 mapping above now names both the telemetry-inventory and the runbook. |
| F6 | `dash` needed `npm ci` locally to get declared devDeps (`@testing-library/react`, `happy-dom`) | ACCEPTED_RESIDUAL | The install is deterministic from the lockfile, no tracked file changed, and the declared dependencies are already correct. A fresh clone needs `npm ci` before the dash gates, which is environmental. |

**T4 scope adjustment.**
- Create the F2 todo and its row in `docs/todo/README.md`.
- Add the F2, F4 and F5 runbook notes.
- Add the F3 architecture note, after the F3 fix lands.
- T4 now depends on the F1 and F3 fixes as well as on T1–T3.

## 12. Completion summary and closeout

**Outcome.** All three implementation tasks shipped, both in-run findings are fixed, and the
durable decisions are harvested. Kilo Code now has an honest read-only row in the Settings drawer,
a kilo.db collector that reports a present-but-unreadable store instead of reading it as an empty
history, and a real content reader with id-based pairing under parser contract `'4'`. The
user-visible gap the plan opened with was **operational**, not a schema defect: scheduled folder
import is off on the owner's machine.

### Verification evidence

| Row | Test | RED | GREEN |
|---|---|---|---|
| T1a | `dash/src/refresh/kilo-live-schema.test.ts` (`describe('kilo live schema')`) | **None — GREEN on first run.** The live-schema characterization passed before any source change. | 2 canonical records with harness `kilo-shared-runtime` carrying the fixture's input and output counters, and one `source_checkpoint` row with `record_count` 2. Per the Test contract a first-run pass is not a defect: it settles **D1** as an operational gap and the test stays as a regression pin. |
| T1b | Same file | Fails with the silent `continue` in place: no problem is recorded for a present-but-unreadable store. | A kilo.db holding garbage bytes and, separately, one missing the `part` table each yield exactly one `PROVIDER_PARSE_ERROR` attributed to `kilo-shared-runtime` and naming the database; the companion Codex harness still lands its records; an absent kilo directory stays problem-free. |
| T2a | `dash/src/synth/readers/kilo.test.ts` (rewritten) | Fails: the stub yields nothing. | One turn per token-bearing assistant message, in order, `nativeRecordId` = message id; `conversation_history`, `tool_result_content`, `toolCalls` and `toolResults` populated with `isError` on an errored tool; no `system_prompt` or `tool_definitions` part; a missing DB or session yields zero turns without throwing. |
| T2b | `dash/src/providers/kilo-code.test.ts`, `opencode.test.ts` | Fails: Kilo calls carry no `pairingId`. | Kilo calls carry `pairingId` = the Kilo message id; OpenCode calls carry none and are otherwise unchanged. |
| T2c | `dash/src/synth/provider.test.ts`, `canon/measurability.test.ts` | Fails: parts absent, every bucket unmeasurable, version `'3'`. | Parts attach by `pairingId`; conversation and tool-result content are measured while `system_prompt` and `tool_definitions` stay `not_measurable`; `KILO_PARSER_CONTRACT_VERSION` is `'4'`. |
| T3a | `dash/src/capture/harnesses/kilo.test.ts` (new), `detect.test.ts`, `status.test.ts` | Fails: no `kilo` entry. | `mode: 'status-only'`, `state: 'not-applicable'`, one evidence line when any of the three D3 paths exists and `not-detected` when none does, `statusLines` stating the env gate and the kilo.db route, one `manualSteps` entry `kilo-folder-import` with no `env`, no owner config text echoed, win32 separators. |
| T3b | `dash/src/capture/capture.test.ts`, `actions.test.ts`, `server/capture-route.test.ts` | Fails: unknown harness. | `capture enable`/`disable --harness kilo` write no file and leave no receipt; `GET /api/kyber/capture` includes the status-only row; a dry-run `POST` enable returns **400**. The six-harness roster pins are updated to seven only where they enumerate the roster. |

`npm --prefix dash run test` is **6185/6185**; `typecheck`, `lint` and `check:reachable` are clean.
The council reviewed at 6182/6182; the suite grew by three tests with the post-council remediation
and the F3 regression pin.

### Findings dispositions

| ID | Disposition | Where it landed |
|---|---|---|
| F1 | FIX_IN_RUN | `dash/src/capture/index.ts` comments no longer hard-code a harness count. |
| F2 | DEFER_TO_TODO plus a runbook note | `docs/todo/kyberdash-job-problem-reconciliation.md` — content specified by F2, file creation and its todo-index row listed under "Remaining mechanical steps" below — [runbook §10](../dash/runbook.md#10-kilo-shows-no-data), [architecture](../dash/architecture.md#local-harness-source-refresh). |
| F3 | FIX_IN_RUN | The per-record measurability stamp selects the envelope harness id for `kilo-vscode-legacy` only; regression assertion added; `call.provider`, dedup identity and every other harness's stamps untouched. Recorded in [architecture](../dash/architecture.md#kilo-code-content-reader-and-id-based-pairing). |
| F4 | ACCEPTED_RESIDUAL | Which paths count as detected is documented in [architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031) and the [runbook](../dash/runbook.md#harness-opentelemetry-capture), and flagged in the closeout report. |
| F5 | DOCUMENT_IN_CLOSEOUT | [Telemetry inventory](../dash/telemetry-inventory.md#kilo-code-otlp-surface-documented-not-ingested) and [runbook §10](../dash/runbook.md#10-kilo-shows-no-data). |
| F6 | ACCEPTED_RESIDUAL | Environmental only: a fresh clone needs `npm ci` before the dash gates. No tracked file changed and no repository documentation is needed. |

### Review

- Council verdict: **APPROVE**, risk **MEDIUM**, **KW-REVIEW-024**, with the suite at 6182/6182.
- One **major** finding was raised after the council and remediated in this pass: the busy-store
  marker. A `SQLITE_BUSY` store could open cleanly at parse time, so re-opening would have
  presented a discovery-time failure as an empty store. The marker source now carries
  `unreadableStoreError`, `parse()` throws that recorded reason **unconditionally**, and a
  passthrough-mocked `openDatabase` pins it (`kilo-live-schema.test.ts`, "records exactly one
  `PROVIDER_PARSE_ERROR` for a store busy at discovery even when it opens cleanly at parse time").
  The APPROVE stands with that remediation in place.

### Owner gates

All three owner gates in [section 9](#9-verification-gates) are **NOT RUN**. They are live and not
automatable, and this closeout harness had no shell. They stay recorded as runtime gates in the
telemetry inventory ("Remaining owner/runtime gates", item 6) so they are not lost:

1. `KYBERDASH_VERBOSE=1 kyberdash dash import-history --db /tmp/kilo-check.db --harness kilo-shared-runtime --weeks 16` — not run.
2. The Settings drawer showing a read-only Kilo Code row with the folder-import step — not run.
3. A real Kilo session's Context view showing conversation and tool-result content — not run.

### Closeout mapping

Every decision and finding row maps to a canonical destination. The two blanket statements below
apply to the reader, pairing and collector work as a group.

- **no ADR — ADR waiver: reader, pairing and collector hardening refine ADRs 0009, 0016 and 0029
  within established patterns.**
- The capture-scope change is recorded by the **ADR 0031 amendment of 2026-10-21**, not a new ADR.
- **No documentation waiver** is taken for any decision row.

| Row | Destination in canonical documentation |
|---|---|
| D1 | [Telemetry inventory](../dash/telemetry-inventory.md) Kilo row (collection gated on folder import) and "Remaining owner/runtime gates" item 6; [runbook §10](../dash/runbook.md#10-kilo-shows-no-data). The RED/GREEN evidence is transient and lives in this summary. |
| D2 | [ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) 2026-10-21 amendment; [architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031) "Kilo Code is status-only by decision"; [runbook](../dash/runbook.md#harness-opentelemetry-capture) "Kilo Code row". |
| D3 | [Architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031) capture section (row fields, the three probes, the manual step); [runbook](../dash/runbook.md#harness-opentelemetry-capture) probe table; ADR 0031 amendment. |
| D4 | [Telemetry inventory](../dash/telemetry-inventory.md#kilo-code-otlp-surface-documented-not-ingested) "Kilo Code OTLP surface"; [architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031) (no OTLP adapter is registered for Kilo). |
| D5 | [Architecture](../dash/architecture.md#kilo-code-content-reader-and-id-based-pairing) "Kilo Code content reader and id-based pairing"; [telemetry inventory](../dash/telemetry-inventory.md#kilo-code-otlp-surface-documented-not-ingested) bucket table. |
| D6 | Same architecture section: `pairingIdFromMessageId`, the `pairingId`-vs-`turnId` split, `positionalPairingUnsafe`, OpenCode byte-identical. |
| D7 | Same architecture section: contract `'4'`, the `READER_UNMEASURABLE` rows for `kilo`, `kilo-code`, `kilo-shared-runtime` and `kilo-vscode-legacy`; [telemetry inventory](../dash/telemetry-inventory.md) Kilo row. |
| D8 | [Architecture](../dash/architecture.md#local-harness-source-refresh) "A present-but-unreadable store is a problem, not an empty history"; [runbook §10](../dash/runbook.md#10-kilo-shows-no-data). |
| D9 | [Telemetry inventory](../dash/telemetry-inventory.md#kilo-code-otlp-surface-documented-not-ingested) (OpenCode-attributed quarantine noise, no record); [runbook §10](../dash/runbook.md#10-kilo-shows-no-data) "do not set `OTEL_EXPORTER_OTLP_ENDPOINT`". |
| D10 | [ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) amendment (decision 5 bounds the route to harness telemetry keys); [runbook](../dash/runbook.md#harness-opentelemetry-capture) (the row points at the Maintenance switch). |
| D11 | [ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) 2026-10-21 amendment and its [ADR index](../adr/README.md) row. |
| D12 | [Architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031) and [runbook](../dash/runbook.md#harness-opentelemetry-capture) state exactly which three paths count as detected and that VS Code Insiders, JetBrains and a Windows `%APPDATA%` config are not probed; the low-confidence provenance is carried by F4 above. |
| D13 | The plan index row's development-mode column (test-first, autonomy autonomous). No ADR — ADR waiver: a development-mode choice creates no architectural or operational invariant. |
| AP1 | This plan's status paragraph and the plan index row. Transient approval provenance. |
| F1 | [Architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031) "The registry is seven harnesses" records the roster without a hard-coded count. No ADR — ADR waiver: a comment-only roster text correction. |
| F2 | `docs/todo/kyberdash-job-problem-reconciliation.md` and its todo-index row; [architecture](../dash/architecture.md#local-harness-source-refresh) "The job row is not reconciled after a repair"; [runbook §10](../dash/runbook.md#10-kilo-shows-no-data) workaround. No ADR — ADR waiver: the gap is recorded, not decided, and the repair is deliberately deferred. |
| F3 | [Architecture](../dash/architecture.md#kilo-code-content-reader-and-id-based-pairing) "Per-record measurability keys on the harness id for `kilo-vscode-legacy` only"; [telemetry inventory](../dash/telemetry-inventory.md#kilo-code-otlp-surface-documented-not-ingested) note on the legacy task tree. No ADR — ADR waiver: it restores the existing honest-unobservability rule rather than deciding anything new. |
| F4 | [Architecture](../dash/architecture.md#harness-opentelemetry-capture-adr-0031), [runbook](../dash/runbook.md#harness-opentelemetry-capture) and this summary. |
| F5 | [Telemetry inventory](../dash/telemetry-inventory.md#kilo-code-otlp-surface-documented-not-ingested), [runbook §10](../dash/runbook.md#10-kilo-shows-no-data). |
| F6 | This summary only. Environmental, no tracked change, no repository behaviour to document. |

### Remaining mechanical steps

Not performed in this pass, because the closeout harness had no shell or file-creation tool:

1. Create `docs/todo/kyberdash-job-problem-reconciliation.md` and add its row to
   `docs/todo/README.md` under **Open**. Its content is fixed here so the analysis is not lost:
   today's behaviour (the job row on `harness:<harnessId>:job`, written by the `refreshHarnessSources`
   catch and out of `reconcileSourceProblems`'s documented scope), why it was not fixed in this plan
   (a cross-harness contract change, outside the approved scope, and D8's "exactly one problem"
   acceptance holds with the row present), three candidate repairs — **(a)** delete the job row when
   a job completes without throwing, **(b)** record the job problem on a source-keyed span the
   existing reconcile call owns, **(c)** add an explicit job-unit arm to `reconcileSourceProblems`
   rather than widening its prefix rule — the constraint that a run which skips a harness must not
   clear a job row, the per-harness `dash clean` workaround, and acceptance criteria.
2. `git mv docs/plans/2026-10-21-kyberdash-kilo-otel-collection.md docs/archive/plans/`, set the
   frontmatter `status` to `complete`, remove the Active row from `docs/plans/README.md`, and add
   the Archived row in that table's format (status Complete, dates 2026-10-21 / 2026-10-21,
   canonical-docs and ADR links as mapped above, goal plus outcome summary).
3. Re-run `docs validate . --merge-ready` and `docs drift .` to zero findings.

