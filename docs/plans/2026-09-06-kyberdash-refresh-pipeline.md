---
id: plans/2026-09-06-kyberdash-refresh-pipeline
title: KyberDash Harness-Source Refresh Pipeline
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# KyberDash Harness-Source Refresh Pipeline

## Outcome

Implement `kyber-weave dash refresh [--history-weeks N]` as the production lifecycle that fills KyberDash from local coding-harness history. The default history window is two weeks. One logical job is created for every distinct harness source type, each job processes that source's files or native records through the requested window, successful jobs are committed and derived even when another job fails, and the command exits nonzero when any harness job fails.

This plan is the canonical execution source for local-history refresh. The [KyberDash spine plan](2026-09-06-kyberdash-spine.md) remains authoritative for the dashboard hierarchy and presentation work; it does not define this ingestion lifecycle. Durable store and signal-merging rules remain in [ADR 0008](../adr/0008-kyberdash-single-canonical-store.md), [ADR 0009](../adr/0009-multi-signal-ingestion-span-shaped-record.md), and the [telemetry inventory](../dash/telemetry-inventory.md).

## Decisions already made

These are implementation constraints, not open design questions:

1. The public command is `kyber-weave dash refresh`. It is a `dash` subcommand, not a top-level `refresh` command and not a `kyber refresh` command.
2. `--history-weeks <positive-integer>` selects the history window. Omission means two weeks.
3. The command creates one logical job per **harness source type**, not one job per vendor, provider object, parent dot-folder, or individual file.
4. Within a job, native chat-history files or database records are the units of work. The job continues until every eligible unit in the time window is processed or recorded as failed.
5. Record timestamps determine inclusion. A chat that crosses the cutoff is opened and contributes only its in-window records.
6. Harness surfaces stay distinct whenever the source supplies deterministic client evidence. In particular, `~/.gemini/antigravity`, `~/.gemini/antigravity-cli`, and `~/.gemini/antigravity-ide` are three jobs, identities, rollups, and filters.
7. Gemini is a model/chat/provider identity, not a coding harness. No refreshed canonical record may use `gemini` as its harness merely because an Antigravity session used a Gemini model.
8. A rerun is checkpointed, idempotent, and deduplicated. It processes only new or changed native units plus a newly requested, previously uncovered part of the history window.
9. A failed harness job does not cancel other jobs. Successful jobs persist and derive. The final report has one row per harness source and the process exits nonzero if any row failed.
10. A refresh button is deferred. This plan completes the CLI, storage, derivation, API data, and browser acceptance needed before a UI trigger is designed.
11. Upstream code under `dash/src/**` is vendored. Implementation stays under `dash/kyber/**` and adapts the exported parser/provider/cache seams at that boundary.

## Scope and non-goals

### In scope

- A complete, audited registry of local harness-source descriptors.
- Deterministic client-surface classification without vendor-level collapsing.
- Bounded parallel harness jobs and bounded record/file processing.
- UTC history-window semantics, record-level filtering, and cross-cutoff chats.
- Source checkpoints, record provenance, stable identities, and schema migration.
- Existing synthesis, validation, quarantine, OTLP/file merge, canonical persistence, session/run/execution derivation, harness rollups, and Attention data driven from the split identities.
- Per-harness command reporting, failure isolation, retry behavior, and exit codes.
- Fixture, integration, local-source, API, and browser acceptance tests.
- Runbook, architecture, telemetry-inventory, and durable decision documentation after implementation.

### Not in scope

- A dashboard refresh button, progress modal, scheduler, or background daemon.
- Treating model names such as Gemini, Claude, or GPT as harness identities.
- Uploading local chat history or exposing raw paths/content in command output.
- Editing `dash/src/**` to make the integration convenient.
- Replacing `canon.db`, creating a second dashboard store, or deleting raw canonical history during migration.
- Fabricating a client split when a shared store carries no reliable client discriminator.
- Reworking unrelated dashboard visual changes currently present in the dirty worktree.

## Baseline and interrupted work

The worktree already contains partial, uncommitted refresh edits. They are evidence and possible salvage material; they are not an implemented pipeline.

| Current edit | Current behavior | Disposition |
|---|---|---|
| `dash/kyber/cli/register.ts` | Registers `dash refresh`, opens `canon.db`, exposes `--db` and an unrequested `--provider`, and prints only aggregates. | Salvage the command placement and database-path lifecycle. Replace option parsing and reporting. Do not ship the public `--provider` option; inject source selection only in tests if needed. |
| `dash/kyber/cli/refresh.ts` | Sequentially loops provider objects and all sources, parses whole sources, loads every non-file record for every pass, then rebuilds sessions and rollups. | Rework as the harness-source scheduler. Retain only useful composition concepts; do not preserve provider-level grouping or corpus-wide reads. |
| `dash/kyber/cli/refresh.test.ts` | Proves a small provider loop and failure continuation, but expects merged `antigravity`/`gemini` identities. | Replace the expectations and broaden to window, checkpoint, split-identity, concurrency, and exit-status contracts. |
| `dash/kyber/synth/provider.ts` | Adds per-reader/synthesizer error capture. | Salvage if it remains the common record-level ingest seam; make errors source-unit aware. |
| `dash/kyber/synth/synth.ts` | Adds a missing type import. | Keep only if required after the redesign. |

The focused `refresh.test.ts` and `register.test.ts` tests currently pass, but that green result proves only the partial behavior above. It is not an acceptance gate for this plan. Unrelated modified frontend, Playwright, ADR, archive, telemetry-inventory, and spine-plan files are user-owned work and must not be reverted, reformatted, or bundled into implementation commits.

## Confirmed current call graph

CodeGraph and line-level source inspection confirm the intended and partial paths:

```text
CLI main
  -> registerKyberCommands
     -> dash refresh                         [partial, uncommitted]
        -> refreshLocalProviders             [partial, uncommitted]
           -> getAllProviders
           -> Provider.discoverSessions
           -> Provider.createSessionParser
           -> ingestProviders
              -> Synthesizer
              -> validation/quarantine
              -> deduplicate
           -> CanonStore.upsertMany
           -> buildSessions
              -> buildRuns
           -> buildHarnessRollup

Upstream production parser lifecycle
  -> parseAllSessions
     -> loadCache / monthScopeForRange
     -> provider discovery
     -> Claude special scan or parseProviderSources
     -> per-native-unit fingerprint/change detection
     -> record timestamp slicing
     -> partial/complete cache publication
```

The upstream parser already supplies much of the expensive lifecycle: safe discovery, `DateRange`, record-level range filtering, cross-cutoff chat slicing, month-sharded `SessionCache`, native-file fingerprints, partial-progress saves, refresh locking, and provider-specific parse workers. The implementation must reuse those exported seams through a Kyber-owned adapter rather than duplicate them.

The exact missing runtime links are:

- The partial command does not parse or pass `--history-weeks` or a `DateRange`.
- `getAllProviders()` returns provider objects, not independently classified harness-source jobs.
- The loops are sequential and have no bounded scheduler or writer backpressure.
- Claude's generic `createSessionParser()` path is intentionally empty because the upstream parser owns its project-directory scan; the partial command therefore produces no Claude calls.
- `normalizeHarnessName` and `SURVEYED_HARNESSES` still merge or omit several client surfaces, so persisted rows and rollups cannot back correct filters.
- Deduplication can pair by session position, which is not a safe identity for a partial window or changed file.
- `CanonStore` schema version 9 has canonical and derived rows, but no native-source checkpoint or record-provenance relation.
- The partial command calls `store.listAll()` for OTLP data on every source, making work proportional to the entire corpus rather than the changed unit.
- Discovery/parser failure is stored as a generic provider problem, but there is no per-harness status, retry/checkpoint rule, summary row, or command exit contract.
- The current dashboard cannot distinguish data that was never refreshed, a source not installed, a source that failed, and a measured zero.

## Confirmed harness-source inventory

The inventory below comes from the actual registered provider list, its discovery/root functions, source classifiers, and a live discovery audit on the development host. “Independent” means the existing source supplies deterministic evidence from which a separate job can be built. “Boundary work” means the upstream provider/parser exists but Kyber still needs the registry/classifier/provenance adapter described in this plan.

### Required independent identities

| Canonical harness id | User-facing label | Native root or discriminator | Native format | Parser status | Independent? | Constraint |
|---|---|---|---|---|---|---|
| `antigravity` | Antigravity | `~/.gemini/antigravity` | protobuf state and local SQLite/session records | Registered and discovered; boundary work | Yes | Never normalize to Gemini. |
| `antigravity-cli` | Antigravity CLI | `~/.gemini/antigravity-cli` | protobuf state and local SQLite/session records | Registered under upstream Antigravity provider; boundary work | Yes | Separate job, records, rollup, and filter. |
| `antigravity-ide` | Antigravity IDE | `~/.gemini/antigravity-ide` | protobuf state and local SQLite/session records | Registered under upstream Antigravity provider; boundary work | Yes | Separate job, records, rollup, and filter. |
| `copilot-cli` | Copilot CLI | `~/.copilot`, `~/.copilot/session-state`; `sourceType=jsonl|session-store` | JSONL and session-store SQLite | Registered and discoverable; boundary work | Yes | Do not merge with editor or agent telemetry. |
| `copilot-vscode` | Copilot for VS Code | VS Code, Insiders, and VSCodium workspace/global storage; `sourceType=chatsession|transcript` | `.chatSession` and transcript records | Registered and discoverable; boundary work | Yes | VS Code distributions are roots of one client surface, not separate harnesses. |
| `copilot-jetbrains` | Copilot for JetBrains | `~/.config/github-copilot`; `sourceType=jetbrains` | JetBrains Copilot conversation records | Discovery exists; alias/registry mapping missing | Yes | Add the missing client alias and fixtures before enabling the job. |
| `copilot-agent` | Copilot Agent | Copilot OTLP source; `sourceType=otel` | OTLP SQLite/records | Registered and discoverable; merge work | Yes | It is a harness-source job only when the trace identifies this client; generic OTLP is not force-attributed. |
| `codex-cli` | Codex CLI | `~/.codex/sessions`, `~/.codex/archived_sessions`; `session_meta.originator` identifying CLI | JSONL | Registered; classifier/provenance work | Yes | Root alone does not distinguish it from Desktop. |
| `codex-desktop` | Codex Desktop | Same Codex roots; `session_meta.originator="Codex Desktop"` or another explicitly mapped Desktop value | JSONL | Registered; classifier/provenance work | Yes | Persist the source header; do not infer from file location. |
| `codex-unclassified` | Codex (unclassified client) | Same Codex roots; missing or unknown originator | JSONL | Registered; classification fallback required | Yes, as quarantine/report bucket | Unknown originators must not be guessed into CLI or Desktop. |
| `claude-cli` | Claude Code CLI | `~/.claude/projects`; record/session `entrypoint=cli` | Claude project JSONL | Upstream special parser path exists; Kyber boundary missing | Yes | Classification is record/session evidence, not root alone. |
| `claude-desktop` | Claude Code Desktop | macOS Claude local-agent sessions and Claude project records with `entrypoint=claude-desktop` | JSONL/native desktop session records | Upstream special parser path exists; Kyber boundary missing | Yes | `~/.claude/projects` can contain Desktop entrypoints. |
| `claude-unclassified` | Claude Code (unclassified client) | Claude roots with missing or unknown entrypoint | JSONL/native records | Classification fallback required | Yes, as quarantine/report bucket | Do not silently choose CLI. |
| `cursor` | Cursor IDE | Cursor `state.vscdb` / workspace state | SQLite virtual sessions | Registered and discovered; boundary work | Yes | Remains separate from Cursor Agent. |
| `cursor-agent` | Cursor Agent | `~/.cursor/projects` and Cursor agent/code-tracking data | JSONL and SQLite | Registered as a distinct provider; boundary work | Yes | Separate job, records, rollup, and filter. |
| `cline` | Cline | VS Code-family Cline global storage and `~/.cline/data` | task/history JSON and provider records | Registered and discoverable; boundary work | Yes | Remains separate from Cline CLI. |
| `cline-cli` | Cline CLI | `~/.cline/data/sessions` | native session records | Registered as a distinct provider; boundary work | Yes | Separate job and filter. |
| `kiro-cli` | Kiro CLI | `~/.kiro/sessions/cli` | native CLI session records | Registered under Kiro; path classifier required | Yes | Separate from IDE roots. |
| `kiro-ide` | Kiro IDE | Kiro IDE global/workspace storage and `~/.kiro/sessions/<project>` | IDE storage and native session records | Registered under Kiro; path classifier required | Yes | Multiple IDE roots are one client surface. |
| `kilo-shared-runtime` | Kilo shared runtime | `~/.local/share/kilo/kilo.db` | SQLite | Registered and discovered; Kyber reader exists | No finer split is safe | The current database is shared by Kilo clients and has no confirmed client column. Do not manufacture CLI/IDE attribution. |
| `kilo-vscode-legacy` | Kilo for VS Code (legacy) | VS Code-family `globalStorage/kilocode.kilo-code` | legacy task/history store | Registered and discovered; boundary work | Yes | Separately attributable by native root. |
| `pi` | Pi | `~/.pi/agent/sessions` | JSONL | Registered; Kyber reader exists | Yes | Must emit a job row even when absent or unchanged. |
| `opencode` | OpenCode | `~/.local/share/opencode` | current SQLite and legacy native files | Registered; Kyber reader exists | Yes | Treat legacy/current storage as revisions of one harness unless the source identifies different clients. |

Kilo's identity limitation is a source fact, not a product-policy preference. Kilo documents the current database as a shared runtime/session store, and the registered schema provides no reliable client discriminator. The implementation therefore uses `kilo-shared-runtime` until a source-native field can prove a narrower split; the legacy VS Code store remains independently attributable.

### Remaining registered provider sources

Every registered local provider below also gets a descriptor and logical job. Several were not installed on the audited host; their status is still confirmed from the registered discovery/parser implementation, not inferred from directory names. Alternate install channels and legacy renamed roots stay in one job unless the native record identifies a distinct client surface.

| Canonical harness id | Registered provider | Root(s) / format family | Parser status | Independently processable | Known limitation |
|---|---|---|---|---|---|
| `codewhale` | `codewhale` | `~/.codewhale/sessions`, legacy `~/.deepseek/sessions`; native session files | Registered, not observed locally | Yes | Primary/legacy roots are one migrated product. |
| `codebuff` | `codebuff` | `~/.config/manicode`, `manicode-dev`, `manicode-staging`; native session files | Registered, not observed locally | Yes | Channels are one client identity unless records prove otherwise. |
| `devin` | `devin` | `~/.local/share/devin/cli/transcripts`, `sessions.db`; transcript/SQLite | Registered, not observed locally | Yes | CLI transcript and DB duplication must deduplicate by native identity. |
| `droid` | `droid` | `~/.factory/sessions`; JSONL | Registered and observed locally | Yes | None known beyond boundary work. |
| `dsh` | `dsh` | `~/.dsh/sessions`; native session records | Registered, not observed locally | Yes | None known beyond boundary work. |
| `hermes` | `hermes` | `~/.hermes`; native session records | Registered, not observed locally | Yes | Confirm parser revision token before checkpointing. |
| `ibm-bob` | `ibm-bob` | IBM Bob IDE global storage; editor-native records | Registered, not observed locally | Yes | Multiple editor distributions remain one Bob client surface. |
| `kimi` | `kimi` | `~/.kimi/sessions`; native session records | Registered, not observed locally | Yes | Distinct from Kimi Code. |
| `kimi-code` | `kimicode` | `~/.kimi-code` and Kimi desktop runtime data; native records | Registered, not observed locally | Yes | Normalize provider name `kimicode` to harness id `kimi-code`. |
| `lingtai-tui` | `lingtai-tui` | `~/.lingtai`, `~/.lingtai-tui`; native records | Registered, not observed locally | Yes | Legacy/current roots are one TUI client. |
| `mistral-vibe` | `mistral-vibe` | `~/.vibe/logs/session`; session logs | Registered, not observed locally | Yes | None known beyond boundary work. |
| `mux` | `mux` | `~/.mux`; native session records | Registered, not observed locally | Yes | Confirm source revision token in fixtures. |
| `openclaw` | `openclaw` | `~/.openclaw` and legacy `.clawdbot`, `.moltbot`, `.moldbot`; native records | Registered, not observed locally | Yes | Aliases are product renames, not separate harnesses. |
| `openclaude` | `openclaude` | `~/.openclaude/projects`; project session files | Registered, not observed locally | Yes | Distinct from Claude Code. |
| `open-design` | `open-design` | Open Design OS application data; native records | Registered, not observed locally | Yes | Resolve platform root through the provider, never a Kyber hard-coded guess. |
| `omp` | `omp` | `~/.omp/agent/sessions`; JSONL/native agent sessions | Registered, not observed locally | Yes | Distinct from Pi despite a similar layout. |
| `qwen` | `qwen` | `~/.qwen/projects`; project session files | Registered, not observed locally | Yes | Model/provider metadata remains separate from harness id. |
| `quickdesk` | `quickdesk` | `~/.quickwork` plus profile roots; native records | Registered, not observed locally | Yes | Profiles are source units within one client job. |
| `roo-code` | `roo-code` | VS Code-family Roo Code global storage; task/history records | Registered, not observed locally | Yes | Editor distributions are roots of one client surface. |
| `zerostack` | `zerostack` | platform Zerostack `sessions` data; native records | Registered, not observed locally | Yes | Resolve platform root through provider discovery. |
| `grok` | `grok` | `~/.grok/sessions`; native session records | Registered, not observed locally | Yes | Model name is metadata, not a separate harness. |
| `forge` | `forge` | `~/.forge/.forge.db`; SQLite | Registered, not observed locally | Yes | Checkpoint by native row revision/digest. |
| `goose` | `goose` | `~/.local/share/goose/sessions/sessions.db`; SQLite | Registered, not observed locally | Yes | Checkpoint by native row revision/digest. |
| `crush` | `crush` | `~/.local/share/crush/projects.json`; JSON | Registered, not observed locally | Yes | A changed project file can contain multiple native sessions. |
| `warp` | `warp` | stable/preview Warp application SQLite stores | Registered, not observed successfully | Yes | Discovery can fail on protected/read-only DBs; report failure without stopping other jobs. |
| `zcode` | `zcode` | `~/.zcode/cli/db/db.sqlite`; SQLite | Registered, not observed locally | Yes | Checkpoint by native row revision/digest. |
| `zed` | `zed` | Zed threads SQLite store | Registered, not observed locally | Yes | Resolve platform root through provider discovery. |

Two registered provider entries are deliberately excluded from harness jobs:

| Provider | Reason |
|---|---|
| `gemini` | It represents Gemini chat history/model usage. It can contribute model/provider metadata where a coding harness record references Gemini, but it is not a harness filter or rollup. |
| `vercel-gateway` | It is a network provider source, not a local coding-harness chat-history store. Network ingestion remains on its existing path. |

The registry test must fail whenever `getAllProviders()` adds or removes a provider without an explicit `job`, `excluded-with-reason`, or `alias-of` disposition. That turns this inventory into an enforceable audit rather than a one-time list.

## Target lifecycle

```text
command start (UTC anchor, validated options)
  -> audit HarnessSourceRegistry coverage
  -> discover native source units per descriptor
  -> enqueue one logical job per descriptor in bounded executor
       -> walk eligible native files/records in deterministic order
       -> compare revision and covered interval to source checkpoint
       -> parse only new/changed/uncovered native units
       -> classify client surface from source-native evidence
       -> slice records to the UTC history window
       -> synthesize + validate + quarantine problems
       -> identity-aware OTLP/file merge and dedup
       -> enqueue bounded canonical/provenance/checkpoint writes
       -> mark checkpoint only after that source unit commits
  -> wait for every job and the single writer queue
  -> derive sessions -> runs -> executions from committed canonical rows
  -> derive harness rollups and Attention inputs from stored harness ids
  -> print per-harness table and derived summary
  -> exit 0 when all jobs succeeded/skipped; exit 1 when any job failed
```

### Boundary with the upstream parser

Create a Kyber-owned source adapter under `dash/kyber/refresh/**` around the exported provider, parser, and cache contracts. It must reuse:

- `getAllProviders()` and native provider discovery;
- `parseProviderSources()` for providers whose normal parser path is supported;
- `parseAllSessions()`/`SessionCache` as the Claude-compatible special parse seam;
- the upstream `DateRange`, cache fingerprint, change-detection, cross-cutoff slicing, refresh lock, and provider-specific parser workers;
- existing Kyber content readers when they add raw content or token detail absent from the upstream cached call.

The adapter may project upstream cached/native calls into a Kyber `SourceRecordEnvelope`, but it must not fork the parser implementation. If a needed value is not exported, the implementation must first prove that it can be read through an existing exported cache/provider type. Any proposed edit to `dash/src/**` stops execution for a boundary review; it is not pre-authorized by this plan.

## Command contract

### Syntax

```text
kyber-weave dash refresh [--history-weeks <positive-integer>] [--db <path>]
```

- `--history-weeks` defaults to `2`.
- `0`, negative, fractional, non-numeric, overflow, or duplicate conflicting values are usage errors detected before opening the database.
- `--db` retains the existing explicit-store/testing seam; omission uses `~/.kyberdash/canon.db`.
- All registered harness-source jobs run. A public `--provider` or vendor selector is not part of this contract.
- Usage error exits `2`; completed refresh with any failed harness job exits `1`; complete success, including absent sources, exits `0`.

### Stable summary

Default output is concise and contains no chat content or raw local paths:

```text
Harness                 Units  Changed  Skipped  New  Updated  Problems  Status
antigravity                 24        1       23   18        0         0  ok
antigravity-cli            475        2      473   31        4         1  partial
antigravity-ide              2        0        2    0        0         0  unchanged
pi                          74        0       74    0        0         0  unchanged
...
Derived: 49 sessions, 47 runs, 63 executions, 38 harness rollups
Refresh completed with 1 failed harness job.
```

Every descriptor has one row, including `unavailable` when its roots are not installed and `unchanged` when all eligible units match checkpoints. `partial`/`failed` rows include a short safe diagnostic on stderr; detailed local paths and parser errors remain in the problems/provenance tables. Column names and status values are snapshot-tested so scripts can consume them without relying on incidental log messages.

## Time-window semantics

1. Capture one `commandStartedAt` in UTC before discovery. The closed interval is `[commandStartedAt - N*7 days, commandStartedAt]`.
2. A native record's own event/message/turn timestamp is authoritative. File modification time is only a discovery optimization for formats whose adapter documents monotonic mtime behavior.
3. A source unit with any possible in-window record is opened. Records before the lower bound are ignored; records after `commandStartedAt` are quarantined as future-dated rather than imported.
4. Missing, malformed, or ambiguous record timestamps produce source problems and are not imported. They do not inherit file mtime as a semantic timestamp.
5. A chat beginning before the lower bound and continuing after it contributes its in-window records. Stable session identity connects those records to the native chat without importing earlier turns.
6. Range expansion, for example from two weeks to six, processes only the newly uncovered interval plus new/changed units in the already covered interval.
7. UTC interval arithmetic makes results independent of the host timezone and daylight-saving transitions.

## Source checkpoints, provenance, and schema migration

Bump the canonical schema from version 9 through the existing migration mechanism. The migration is additive and transactionally creates two relations; it does not rewrite or delete `records.raw` or derived history.

### `source_checkpoint`

Key: `(harness_id, source_key)`.

Required fields:

- canonical harness id and stable source-unit key;
- provider/parser id, parser contract version, format, and privacy-safe source-root label;
- native revision token/fingerprint;
- `covered_from_utc` and `covered_through_utc`;
- last attempt and last successful completion timestamps;
- last status/error code and unit/record counters.

A checkpoint advances only in the same transaction that commits all accepted canonical rows and provenance for that source unit. A failed or cancelled unit retains its last successful checkpoint so the next run retries it. A parser contract-version change invalidates the affected checkpoint without deleting prior canonical rows.

Revision rules are source-native:

- regular files use the existing device/inode/mtime/size fingerprint, with an append offset only where the parser proves append-only behavior;
- database virtual sessions use a native update/message revision or a deterministic metadata digest;
- sources without a cheap revision use a deterministic content digest;
- discovery may read metadata to decide whether a unit changed, but payload processing occurs only for a new, changed, or newly uncovered unit.

### `record_provenance`

Key: canonical `span_id`; foreign key to `(harness_id, source_key)`.

Required fields include source-native session id, source-native record/turn/message id when available, source revision, parser version, imported timestamp, and a privacy-safe location token. The full local path may appear only in the protected problems/provenance detail surface already intended for local diagnostics, never in default CLI output.

Provenance makes targeted replacement possible when a mutable native unit changes. Rows proven to belong to the changed unit are reconciled transactionally; unrelated raw history is preserved. A source disappearing from discovery never causes automatic deletion.

## Stable record identity and signal merging

- A split surface is part of identity. The canonical source identity is equivalent to `<harness-id>:<native-session-id>:<native-record-id>` and the synthetic span id derives from that stable key.
- When no native record id exists, the adapter uses a documented deterministic record digest containing native session identity, semantic timestamp, role/kind, and source-native ordinal within that immutable revision. It must not use refresh time or database row number.
- Reprocessing an unchanged unit produces zero inserts/updates. Reprocessing a changed unit updates only semantically changed rows and preserves unrelated span ids.
- Partial-window processing never joins records merely because they occupy the same position in two arrays.
- OTLP counters remain authoritative; file content fills fields that telemetry did not provide. Duplicate counters are never summed.
- OTLP/file association requires compatible canonical harness id, native session evidence, and native turn/message evidence. Generic OTLP without a deterministic client surface remains unclassified/quarantined rather than being merged into a guessed split harness.
- `gemini-*`, `claude-*`, and other model values stay in model/provider fields. They do not override the classified harness id.

Add indexed store accessors for the relevant harness/session/time/native identity subset. The refresh path must not materialize `store.listAll()` once per source.

## Concurrency, backpressure, and failure semantics

- Build every logical job up front, then execute through one bounded global pool. Default concurrency is `max(1, min(4, availableParallelism() - 1))`; tests inject a deterministic limit.
- A queued descriptor is still a separate job. Bounded concurrency must not collapse jobs into provider/vendor batches.
- Each job walks its native units in deterministic source-key order. Existing provider-specific worker pools may be reused, but no nested unbounded `Promise.all` is allowed.
- Parsed envelopes enter a bounded writer queue. One store writer batches canonical, provenance, problem, and checkpoint changes to avoid SQLite writer contention and uncontrolled memory growth.
- A unit failure is recorded and the job continues with its remaining units when safe. Discovery failure, parser-wide failure, or writer failure marks that harness job failed. A failed checkpoint is not advanced.
- All jobs use settled-result collection. One rejection never cancels queued or running jobs.
- After successful writes drain, derivation runs once over the resulting store. Successful harnesses are visible even if another harness failed.
- A derivation failure is a command failure and is reported separately from source jobs. It never rolls back already committed raw canonical history.
- Interrupt handling stops admitting new native units, drains/rolls back the active write transaction, leaves completed checkpoints valid, closes the store, and exits nonzero. The next run resumes safely.
- “Source not installed” is `unavailable`, not a failure. “Installed but unreadable” is a failure. Records quarantined by expected validation are counted as problems; a configured fatality policy decides whether the job is `partial` or `failed`, and is locked in tests.

## Derivation and dashboard lifecycle

After all source jobs settle and writes drain:

1. `buildSessions` groups accepted records by canonical split harness and native session identity.
2. `buildRuns` and execution derivation rebuild their dependent rows without merging client surfaces.
3. `buildHarnessRollup` emits one stored rollup for every observed or registered harness source. It does not seed a Gemini harness.
4. Attention tabs, KPI cards, and filters derive from persisted harness rows and availability/status data. A selected split filter must query its canonical id rather than a vendor alias.
5. A missing measurement remains unavailable; it is not displayed as zero. A truly measured zero remains distinct from unavailable, unchanged, or failed refresh state.

The API/browser acceptance in this plan verifies the stored lifecycle and filtering. It does not add a refresh endpoint or button.

## Execution task graph

Each task starts with a failing focused test. `conductor` owns sequencing and review. Core TypeScript tasks require no invented specialist skill; use the repository instructions and a TypeScript implementation/review worker. Documentation closeout uses `app-docs-standard` and `kyber-weave-docs`; browser acceptance uses `playwright`.

| Task | Exact scope and symbols | Depends on | Parallel ownership | Test-first acceptance |
|---|---|---|---|---|
| T0 — Baseline and salvage lock | Inspect/stage-map only: `dash/kyber/cli/register.ts`, untracked `refresh.ts` and tests, `dash/kyber/synth/provider.ts`, `synth.ts`; record unrelated dirty paths. No production edit. | None | Conductor | A path ownership map identifies salvage/rework and prevents other tasks from touching user-owned frontend/docs work. |
| T1 — Harness-source registry and identity contracts | Add `dash/kyber/refresh/types.ts`, `registry.ts`, `registry.test.ts`; adapt `dash/src/providers/index.js` and `SessionSource` only through imports. Define `HarnessSourceDescriptor`, exclusions, classifiers, safe labels, parser contract versions, and source keys. | T0 | Identity worker | Registry exhaustively accounts for every actual provider; Antigravity, Copilot, Codex, Claude, Cursor, Cline, Kiro, Kilo, Pi, and OpenCode cases match the inventory; Gemini/Vercel exclusions have reasons; unknown provider fails the audit. |
| T2 — Checkpoint/provenance migration | Extend `dash/kyber/canon/store.ts` migration chain and typed accessors; add `dash/kyber/canon/source-state.ts`; extend `migration.test.ts`, `store.test.ts`, and new `source-state.test.ts`. | T1 | Store worker | v9 fixture migrates forward without changing/deleting raw records; checkpoint+rows commit atomically; failure leaves prior checkpoint; coverage expansion and targeted provenance lookup work; reopen is stable. |
| T3 — Native-unit boundary adapter | Add `dash/kyber/refresh/source-reader.ts` and tests/fixtures. Reuse exported upstream Provider/`DateRange`/`SessionCache`/parser seams and existing Kyber readers. Do not edit `dash/src/**`. | T1 | Parser worker | Fixtures prove record slicing for cross-cutoff chats, Claude special-path recovery, Codex originator split, Antigravity root split, Copilot source-type split, Kiro path split, Kilo shared fallback, changed-unit detection, and bounded iteration. |
| T4 — Identity-aware synthesis and dedup | Update `dash/kyber/synth/synth.ts`, `provider.ts`, `dedup.ts`, reader types, and focused tests. Add `SourceRecordEnvelope` provenance fields without weakening validation. | T1 | Synthesis worker | Stable split-surface ids rerun unchanged; changed record updates in place; position-only matches do not join; OTLP counters win and file content only fills gaps; generic telemetry never creates Gemini or guessed client attribution. |
| T5 — Bounded scheduler, writer queue, and CLI | Replace the partial `dash/kyber/cli/refresh.ts` with `dash/kyber/refresh/orchestrator.ts`, `scheduler.ts`, `writer.ts`, and report formatter; update `dash/kyber/cli/register.ts`, `register.test.ts`, and refresh tests. | T2, T3, T4 | Orchestration worker | Help/validation contract is exact; all descriptors get jobs; active jobs never exceed injected limit; source units stream with bounded queue depth; one failure does not cancel others; output contains every row; exit 0/1/2 is correct; store always closes. |
| T6 — Split-aware derivation and availability | Update `dash/kyber/canon/measurability.ts`, `harnesses.ts`, `sessions.ts`, `runs.ts`, affected analysis/data/store accessors, and their tests. | T1, T4 | Derivation worker | No normalization collapses required surfaces; registered/observed rollups are separate; Gemini is absent as a harness; selected harness filters return their rows; unavailable KPI values remain unavailable rather than zero. |
| T7 — End-to-end refresh fixtures | Add `dash/kyber/refresh/refresh.integration.test.ts` plus minimal sanitized fixtures for file and virtual-DB sources. Extend canonical integration tests where necessary. | T2–T6 | Integration worker | Default two weeks, explicit wider window, cross-cutoff record slicing, unchanged rerun, one changed unit, range expansion, restart after partial failure, one unreadable harness, and raw-history preservation all pass against a temporary `canon.db`. |
| T8 — API and live browser acceptance, no refresh UI | Exercise existing `dash/kyber/server/routes.ts`, `bridge.ts`, dashboard data tests, and Playwright flows. Production UI edits are allowed only if a hard-coded alias blocks correct stored filters; no button/endpoint is added. | T6, T7 | Browser worker using `playwright` | Each installed split harness appears and filters independently; Pi/OpenCode/Cursor/Kilo rows and KPIs render from the temp refreshed DB; empty means unavailable with a reason; Attention filtering never returns a record from another harness. |
| T9 — Documentation and closeout | Update [architecture](../dash/architecture.md), [runbook](../dash/runbook.md), [telemetry inventory](../dash/telemetry-inventory.md), this plan's status/evidence, and add/harvest an ADR for source identity/checkpoint rules if the final implementation makes those durable. Update the plan index/archive only at completion. | T7, T8 | Docs worker using `app-docs-standard` and `kyber-weave-docs` | CodeGraph index, docs validate, docs drift, links, commands, identity vocabulary, and operational recovery instructions pass. |
| T10 — Independent review and delivery | Review only the implementation-owned diff, run complete gates, resolve high-confidence defects, then produce a scoped commit/PR if separately requested. | T9 | Conductor + independent reviewer | No vendored-source edit, no unrelated dirty file, no identity collapse, no unbounded corpus read, and no green-test claim without the required command/browser evidence. |

T2, T3, and T4 may run in parallel after T1. T6 may begin after T1/T4 while T5 integrates T2/T3/T4. T7 is the convergence gate; T8 and T9 do not begin from a partial pipeline. Parallel workers must receive disjoint file ownership, and a single integration owner resolves shared-file changes.

## Required verification

### Focused TypeScript gates

Run the actual scripts declared by `dash/package.json`; do not substitute a unit-only approximation:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash test
npm --prefix dash run build:cli
node dash/dist/cli.js dash refresh --help
```

Run focused refresh/canonical tests during implementation, then the full suite. Exact test paths are created by the task graph:

```bash
npx --prefix dash vitest run kyber/refresh kyber/cli/refresh.test.ts kyber/cli/register.test.ts
npx --prefix dash vitest run kyber/canon kyber/synth
```

### Deterministic CLI acceptance

Use sanitized fixture roots and a temporary database. Never point destructive or mutation tests at the user's production store.

1. Refresh the default two-week fixture window and assert the per-harness rows and canonical counts.
2. Refresh the same immutable inputs again and assert `New=0`, `Updated=0`, unchanged checkpoints, stable span ids, and stable derived counts.
3. Change one native unit and assert only its checkpoint/provenance/canonical rows change.
4. Repeat with `--history-weeks 6` and assert only the newly uncovered interval plus genuinely changed records is processed.
5. Make one harness unreadable and assert other harness records/rollups persist, the failed checkpoint does not advance, and exit is `1`.
6. Validate `--history-weeks 0`, `-1`, `1.5`, and non-numeric values exit `2` before the DB file exists.
7. Assert no canonical or rollup row uses harness `gemini` and every required split identity remains separate.

### Local-source smoke acceptance

With explicit owner consent for local-history access, run against a temporary database and the real installed roots. The command may read local history but must not mutate those roots:

```bash
refresh_tmp_dir="$(mktemp -d)"
node dash/dist/cli.js dash refresh --history-weeks 2 --db "$refresh_tmp_dir/canon.db"
sqlite3 "$refresh_tmp_dir/canon.db" "select harness, count(*) from records group by harness order by harness;"
sqlite3 "$refresh_tmp_dir/canon.db" "select harness, count(*) from harness_rollup group by harness order by harness;"
```

Expected installed-source evidence includes separate Antigravity variants, separate Copilot surfaces supported by local roots, Codex/Claude classifications, Cursor and Cursor Agent, Kilo's truthful shared/legacy classification, Pi, and OpenCode. An absent installed surface is a defect to investigate, not evidence that the KPI should be blank.

### API/browser acceptance

Start the dashboard against the temporary refreshed DB and use Playwright against the live app:

- verify the All Harnesses totals reconcile with the stored split rows;
- select every available harness and assert its run list is non-empty when the DB contains rows for it;
- specifically exercise Antigravity, Antigravity CLI, Antigravity IDE, Copilot surfaces, Cursor, Cursor Agent, Pi, OpenCode, and Kilo;
- verify selecting one identity cannot show another identity's records;
- verify KPI cards display measured values or an explicit unavailable reason, never unexplained blanks;
- capture the request/response and screenshot evidence for the Attention screen and at least one harness/run drill-down.

No refresh control is added during this acceptance phase.

### Repository governance gates

Before delivery, run the repository-required gates, including the declared review suite:

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
/Users/dave/.local/bin/codegraph index .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json
```

## Risks and mitigations

| Risk | Mitigation / release gate |
|---|---|
| A provider root contains multiple clients. | Require a source-native classifier; use an explicit unclassified/shared identity when evidence is absent. Never infer from vendor or model. |
| Upstream cache semantics and Kyber checkpoints drift apart. | Store parser contract version and upstream revision/fingerprint in the checkpoint; invalidate affected units on version change; add cache-boundary fixtures. |
| A broad history window exhausts memory. | Stream native units, use bounded pools/queues and batched writes, query only dedup candidates, and forbid per-source `listAll()`. |
| SQLite writers contend. | One writer queue, short transactions, existing WAL/busy-timeout behavior, and injected backpressure tests. |
| A mutable source removes or rewrites records. | Reconcile only rows proven by provenance to belong to that source revision; preserve unrelated and unmatched raw history; never delete on disappearance alone. |
| Local app databases are locked or protected. | Provider-specific safe-copy/read-only behavior, per-job failure isolation, no checkpoint advance, actionable retry diagnostics. |
| OTLP and files double-count a turn. | Stable client/session/turn identity, OTLP-counter precedence, content fill-only rule, and disagreement problems. |
| A new provider silently disappears from refresh. | Registry-exhaustiveness test against `getAllProviders()` with explicit job/exclusion/alias dispositions. |
| Dirty worktree changes are overwritten or bundled. | T0 ownership map, path-scoped staging, independent diff review, and no cleanup/reset of unrelated files. |
| Browser shows blanks despite stored rows. | API/filter/KPI contract tests plus live Playwright acceptance against the same temporary refreshed DB. |

## Rollback and recovery

- The schema migration is additive. Before production-store migration, create the same recoverable database backup used by existing canonical-store operations.
- If refresh code must be rolled back, the older binary can continue reading pre-existing canonical rows only after schema compatibility is confirmed; do not downgrade or rewrite the database in place.
- A failed refresh leaves last-successful checkpoints and already committed raw rows intact. Fix the adapter and rerun; do not clear the corpus.
- If a classifier mapping is wrong, ship a versioned classifier migration/backfill that uses provenance to reattribute only proven affected rows, then rebuild derived tables.
- Removing an experimental partial file is allowed only when its useful edits have been deliberately salvaged into the final owned paths and its untracked ownership is confirmed.

## Completion criteria

This plan is complete only when all of the following are true:

- `kyber-weave dash refresh` defaults to two weeks and accepts a validated `--history-weeks N`.
- One bounded logical job runs for every registry harness-source descriptor and processes every eligible native unit in its requested interval.
- Required surfaces remain separate; Gemini is never a harness; Kilo is split only to the granularity its data proves.
- Reruns, changed units, widened windows, partial failures, interrupts, and migration are proven idempotent and recoverable.
- Successful jobs persist canonical/provenance/checkpoint rows and drive sessions, runs, executions, rollups, Attention filters, and KPI availability.
- Per-harness output and exit codes match the command contract.
- The full TypeScript, local CLI, API/browser, documentation, CodeGraph, and repository governance evidence is recorded.
- No `dash/src/**` file or unrelated dirty path is changed by this implementation.

There is no remaining product decision required before execution. Unsupported client separation must resolve to an explicit shared/unclassified identity until native evidence exists; it must not be guessed during implementation.
