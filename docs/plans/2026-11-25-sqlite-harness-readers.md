---
id: plans/2026-11-25-sqlite-harness-readers
title: Read the Kilo, Warp, Cursor and Devin SQLite stores in the C# import
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-25
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberDash
---

# Read the Kilo, Warp, Cursor and Devin SQLite stores in the C# import

**Status: Ready, 2026-11-25.**

Autonomy: autonomous. Development mode: test-first. Run branch `claude/fix/sqlite-harness-readers`,
based on `claude/fix/droid-chart-and-per-harness-e2e` at `715f74c61` (pull request #710), stacked per D1;
first cut from `main` at `2f661f462`.

## Request

The owner's request, verbatim, from the per-harness test work this plan came out of:

> So we need to create a test which loads a sample database at least 10 turns per harness per session in multiple sessions, and then is able to load and check each harness to make sure we have full fidelity and functionality across the board. If there are gaps, we need to know about them and fill them and test them.

Standing owner rule: KyberDash never estimates; a value the harness did not report is not measured.

The conductor's dispatch carried the diagnosis and these plan requirements, verbatim:

> one shared read-only SQLite source reader in Data/Collector (open read-only and immutable/with a cache-copy fallback when the directory is read-only or the db is locked; bounded memory — stream rows, never load a 3 GB file; size/time caps with a recorded problem when hit) that produces the row exports the existing mappers already parse (do not rewrite mapper parsing unless needed); per-harness queries for Kilo (message/part), Warp (agent_conversations/ai_queries/blocks), Cursor (cursorDiskKV bubbles + ItemTable), Devin (sessions metadata + message_nodes conversations); unit tests through an interface seam with in-memory row sources (OD-9), plus SQLite-backed tests in the existing integration style; parser contract version bumps so stored rows are re-read. The e2e assertions already exist on branch claude/fix/droid-chart-and-per-harness-e2e (dash/e2e/file-harness-sqlite.ts, file-harness-dashboard.spec.ts) and currently fail for these four harnesses; this plan's verification runs them after that branch merges (note the dependency) or ports the fixtures.

Also from the diagnosis: say what the Antigravity parser does with a null `.pb` payload and record it as a
gap if unreadable; the silent empty batch must become a recorded problem whenever a source cannot be read.

## Problem and goal

**Problem.** The C# import never reads the SQLite stores of four harnesses: Kilo (`kilo.db`, 3.0 GB on
the owner's machine), Warp (`warp.sqlite`, 63 MB), Cursor (`state.vscdb`, 2.8 GB) and Devin
(`sessions.db`, 196 MB). `HarnessSourceRegistry.ReadLocalUnit` leaves `RawText` null for the `-sqlite`,
`-vscdb` and `-pb` formats (`IsBinaryFormat`), and `KiloMapper.Map`, `WarpMapper.Map`, `CursorMapper.Map`
and `DevinMapper.Map` return an empty batch on a blank `RawText` with no problem recorded. The mappers
parse per-conversation JSON exports of rows, but nothing produces those exports: the TypeScript reader
that did (`dash/src/ingest/sqlite.ts`) was deleted in `fc80ddf9d` (#612). Devin's current CLI keeps its
conversations in `sessions.db` table `message_nodes`, which no reader has ever parsed.
`AntigravityTranscriptMapper.Map` returns an empty batch for every `antigravity-pb` unit, silently.

**Goal.** One shared, read-only, memory-bounded SQLite source reader in `KyberDash.Data/Collector`
turns each conversation in those four databases into its own source unit whose `RawText` is the row
export its mapper already parses (Devin's `message_nodes` get a new parse path, because no existing
parse covers them). Every source that cannot be read, every cap that is hit, and every `.pb` payload
records an `ingest_problem` instead of an empty batch. The per-harness Playwright suite — two sessions
of at least 10 turns per harness, built as real SQLite files in each harness's real schema — passes for
all four harnesses.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| A test loads a sample database, at least 10 turns per harness per session, in multiple sessions | T12–T15 (`dash/e2e/file-harness-sqlite.ts`, two sessions of 10 and 12 turns per harness, real schemas) | `file-harness-dashboard.spec.ts` passes for `kilo-shared-runtime`, `warp`, `cursor`, `devin` |
| Load and check each harness for full fidelity and functionality | T12–T15 (dashboard assertions per harness), T4–T9 (round trip export to mapper) | The spec's per-harness sections render the fixture sessions, turns, tokens and tools |
| Gaps: know about them, fill them, test them | T1–T11 fill the four readers; T10 records the `.pb` gap; Out-of-scope `PR note:` lines name the gaps left | Recorded problems for `.pb`; PR notes list remaining gaps |
| KyberDash never estimates; an unreported value is not measured | D9; base branch D15 removed Cursor and Warp estimation; T6 exports missing bubble fields as `null`; T9 leaves unreported Devin cost and tokens absent | T9 test: a node without `metrics` yields no token rows |
| One shared read-only SQLite source reader in Data/Collector | T1 (seam, open policy, fake), T2 (adapter), T3 (`SqliteHarnessSourceReader`) | `SqliteSourceOpenPolicyTests`, `MicrosoftDataSqliteHarnessSourceSqliteTests`, `SqliteHarnessSourceReaderTests` pass |
| Open read-only and immutable, cache-copy fallback when the directory is read-only or the db is locked | T1 `SqliteSourceOpenPolicy`, T2 `MicrosoftDataSqliteHarnessSource` (D4) | Policy tests cover read-only directory, lock, empty WAL, copy cap; real-file tests cover immutable open and the copy |
| Bounded memory: stream rows, never load a 3 GB file | D3 (one unit per conversation), T2 forward-only reader, T3 bounded export buffer | Export-cap test; streaming test |
| Size and time caps with a recorded problem when hit | T1 limits and codes, T2 copy cap and command timeout, T3 export cap and problems (D6) | Tests for `SQLITE_EXPORT_CAP_EXCEEDED`, `SQLITE_COPY_CAP_EXCEEDED`, command timeout |
| Produce the row exports the existing mappers already parse; do not rewrite mapper parsing unless needed | T4 Kilo, T5 Warp, T6 Cursor (exports match current `Parse` methods); T9 Devin (needed: D8) | Round-trip tests feed each export into the unchanged mapper `Parse` |
| Kilo queries (message/part) | T4 | `KiloSqliteConversationSourceTests`, `KiloSqliteConversationSourceSqliteTests` |
| Warp queries (agent_conversations/ai_queries/blocks) | T5 | `WarpSqliteConversationSourceTests`, `WarpSqliteConversationSourceSqliteTests` |
| Cursor queries (cursorDiskKV bubbles + ItemTable) | T6 (bubbles), T7 (workspace `ItemTable` attribution) | `CursorSqliteConversationSourceTests`, `CursorSqliteConversationSourceSqliteTests`, `CursorWorkspaceAttributionTests` |
| Devin queries (sessions metadata + message_nodes) | T8 (export), T9 (mapper parse) | `DevinSqliteConversationSourceTests`, `DevinSqliteConversationSourceSqliteTests`, `DevinMessageNodesMapperTests` |
| Unit tests through an interface seam with in-memory row sources (OD-9) | T1 fake `InMemoryHarnessSqliteSource`; every non-`SqliteTests` class uses it (D5) | Those classes open no file |
| SQLite-backed tests in the existing integration style | D5: `*SqliteTests` classes in T2, T4, T5, T6, T8 copy `SqliteRelationalStoreParityTests` (real files under `TempDirectory`); T12–T15 end to end | Those classes pass in each task gate; spec run output |
| Parser contract version bumps so stored rows are re-read | T11 `SqliteRowExportParserContractVersion` (D7) | `HarnessSourceRegistryTests` assert the version on every unit of the four formats |
| e2e assertions on the other branch; run them after it merges or port the fixtures | D1 (stack on that branch; a third option, reason recorded), T12–T15 | T15 runs the whole spec on the stacked branch |
| Say what the Antigravity parser does with a null `.pb` payload; record a gap if unreadable | Findings F7; T10 | `AntigravityPbProblemTests` |
| The silent empty batch becomes a recorded problem whenever a source cannot be read | T3 (discovery and read failures), T4/T5/T6/T9 (null `RawText` in each mapper), T10 (`.pb`) | Tests per mapper: null `RawText` yields one warning problem |
| Note merge risk on `HarnessSourceRegistry.cs` | D1, Risks R1 | — |
| Keep tasks small: one harness query per task after the shared reader | Tasks T4–T9, T12–T15 | — |

## Scale

`extends-component` on **KyberDash**: a new collector seam and reader inside the existing import
pipeline, no new component, no canon schema change.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Build on `main`, or stack on `claude/fix/droid-chart-and-per-harness-e2e`? | Stack. The run branch is fast-forwarded onto that branch's head, now `715f74c61` (pull request #710; `ba33a56ef` when this plan was first written), and the pull request targets that branch, retargeted to `main` when it merges. | That branch is 46 commits ahead of `main`, 0 behind. It already adds per-spec `ParserContractVersion` to `HarnessSourceRegistry` (versions `2` and `3`), removes Cursor and Warp token estimation, and holds every e2e helper the SQLite fixtures import (`file-harness-contracts.ts`, `file-harness-jsonl.ts`, `session-dashboard-assertions.ts`). Building on `main` conflicts on `HarnessSourceRegistry.cs`, `CursorMapper.cs`, `WarpMapper.cs` and the e2e files. `docs/plans/README.md` conflicts on the Active Plans table: keep both rows. This departs from the two options the dispatch named (run the spec after that branch merges, or port the fixtures): porting is not possible because the fixtures import helpers that exist only on that branch, and waiting leaves T6–T15 written against code that differs on `main`. Precondition, met before T1: the run branch sits on that branch's head (`origin/claude/fix/droid-chart-and-per-harness-e2e` is `715f74c61`, pushed as #710). Every task's acceptance starts with a precondition check that fails the task as BLOCKED when the base is missing. Source: `docs/plans/README.md` "A slice whose base is unmerged is stacked on it". | autonomous: recommendation accepted; source docs/plans/README.md |
| D2 | Which SQLite client? | `Microsoft.Data.Sqlite.Core` over the system SQLite (`SQLitePCLRaw.provider.sqlite3`), already referenced by `KyberDash.Core` and reached by `KyberDash.Data` through its project reference; no package is added. | The SQLite standard body says the store opens through `Microsoft.Data.Sqlite.Core` and `BaseSqliteRepository`; its rules file still says SQL goes through the `sqlite3` CLI and the package is not added. The rules file is stale (docs-dev fixes it at closeout). Spawning `sqlite3` per query across 3 GB stores would also break the bounded-process rules. | autonomous: recommendation accepted; source docs/standards/sqlite (standard body), dash/server/KyberDash.Core/KyberDash.Core.csproj |
| D3 | One source unit per database, or per conversation? | One unit per conversation. `SourceKey` is `<database full path>#<conversation id>`; `RevisionToken` is per conversation; `SourceRootLabel` is the database file name. | Each mapper parses one conversation per export (Kilo `session_id`, Warp `conversation`, Devin `session_id`, Cursor bubbles grouped by composer). A whole-database export would put gigabytes in one string. Per conversation, an unchanged conversation keeps its checkpoint and is skipped. The deleted TypeScript reader keyed units the same way (`${dbPath}:${row.id}`). | autonomous: recommendation accepted; source archived sqlite-session-parser.ts |
| D4 | Open and fallback rule | Open `Mode=ReadOnly`, `busy_timeout` 1000 ms. On a read-only-directory or sidecar failure (`SQLITE_READONLY*`, `SQLITE_CANTOPEN` with a `-wal` present) or `SQLITE_BUSY`/`SQLITE_LOCKED`: when the `-wal` is absent or empty, reopen as `file:<path>?immutable=1`; otherwise copy the database and its `-wal` into `<directory of canon.db>/sqlite-ro/`, named by a hash of the source path plus its size-and-mtime fingerprint, refuse the copy when the source changed during it, and open the copy read-only. A missing file or a permission denial on the source is not retried: it is a recorded problem. | Mirrors the deleted `openDatabase`/`openReadonlyCache` (`f4fbf7db3^:dash/archived/src/ingest/sqlite.ts`). The decision itself is a pure function (`SqliteSourceOpenPolicy`) so it is unit-tested; the adapter only executes it. The cache sits beside `canon.db` (`CanonDbPath.Resolve`), so `KYBER_CANON_DB` in tests keeps copies out of the user's home. Keep at most the current copy per source; delete the superseded one. | autonomous: low-confidence, no authoritative source (Microsoft.Data.Sqlite URI-filename support is assumed; when the `immutable` open fails, the copy path covers it, as it did in TypeScript) |
| D5 | Where do the SQLite-backed tests live? | Two layers. Logic (open policy, discovery, caps, problems, export shaping) is unit-tested over the in-memory fake `InMemoryHarnessSqliteSource` (OD-9). The adapter and every harness's SQL are tested against real SQLite files in `KyberDash.Data.Tests`, in the style of `KyberDash.Data.Tests.SqliteRelationalStoreParityTests` (a `TempDirectory`, a real database file, `IDisposable`), built by a test helper `SqliteFixtureDatabase` in each harness's real schema. The Playwright suite (T12–T15) is the end-to-end proof. No new test project. | The request asks for SQLite-backed tests in the existing integration style, and `SqliteRelationalStoreParityTests` already opens real `canon.db` files in `KyberDash.Data.Tests`, so this follows an existing pattern; ADR 0090's "no test opens a SQLite file" is already not the practice there. Real-file SQL tests catch a wrong column or join in the task gate instead of at the end-to-end run. | autonomous: recommendation accepted; source the request, dash/server.tests/KyberDash.Data.Tests/SqliteRelationalStoreParityTests.cs |
| D6 | How does a failure become a recorded problem? | Discovery failures (open, schema, enumeration) are `CollectedProblem` rows in `SourceDiscoveryResult.Problems` (severity `warning`, `HarnessId`, `Location` = database path, `ProblemKey` `sqlite-source:<code>:<harness>:<path>`). Read failures throw `IOException` (unreadable, locked after fallback) or `InvalidDataException` (cap hit, conversation gone) whose message starts with the code, so the existing `IsSourceUnitFailure` path records the unit as failed with its problem. Codes: `SQLITE_SOURCE_UNREADABLE`, `SQLITE_SOURCE_SCHEMA_MISSING`, `SQLITE_EXPORT_CAP_EXCEEDED`, `SQLITE_COPY_CAP_EXCEEDED`. A mapper given `RawText == null` returns its batch with one warning problem in its existing malformed code; a whitespace-only `RawText` (an empty file) keeps today's empty batch. | `CanonRefreshEngine` and `TranscriptIngestionService` already write discovery problems and turn `IOException`/`InvalidDataException` into failed units. Fail closed (KW-STD-INPUT-001): a missing table or column refuses the database instead of reading half of it. | autonomous: recommendation accepted; source CanonRefreshEngine.IsSourceUnitFailure, csharp standard KW-STD-INPUT-001 |
| D7 | Parser contract version | Add `HarnessSourceRegistry.SqliteRowExportParserContractVersion = "4"` (the base branch uses `2` and `3`) and stamp it on every unit of `kilo-sqlite`, `warp-sqlite`, `cursor-vscdb` and `devin-sqlite`. | The units are new (D3 changes the `SourceKey`), so the version is belt and braces; it also keeps a checkpoint written by an older build from being reused. | autonomous: recommendation accepted |
| D8 | Devin `message_nodes`: reshape in the reader, or a new mapper parse? | New parse path in `DevinMapper`: the reader exports raw rows (D10 contract) and `DevinMapper.Parse` recognises a root with a `message_nodes` array and maps each `chat_message` into the existing `DevinStep` model. | The request allows a mapper change when needed; no existing parse covers `message_nodes`. Keeping the reader dumb keeps native-format knowledge in the mapper and in its field-register section. Keys come from the live database read 2026-11-25 (Investigation F4). | autonomous: recommendation accepted |
| D9 | Values the harness did not report | Not stored: no estimate, no default. Devin nodes without `metadata.metrics` carry no token rows; Cursor bubbles keep only their own `modelInfo.modelName` (no composer-level fill); Devin ACU, which `sessions.db` holds only per session, is not spread over records. | Standing owner rule. The base branch (its D15) already removed Cursor and Warp estimation. | owner rule |
| D10 | Export contracts (the data the reader hands each mapper) | As the **Export contracts** table under Investigation findings; each harness task repeats its own row in full. | They are the contract between the conversation sources (T4–T8) and the mappers; T8 and T9 both build against the Devin row, so T9 needs nothing from T8 (T9 is ordered after T7 only for the shared field register). | autonomous: recommendation accepted |
| D11 | Limits | `HarnessSqliteLimits`: export cap 64 MiB per unit (the OTLP request bound in `BoundedIngestWriter`), copy cap 4 GiB per database, command timeout 30 s, busy timeout 1000 ms. Each hit is a recorded problem (D6). | Kilo's 3.0 GB store fits under the copy cap; a single conversation over 64 MiB is refused rather than truncated, because a truncated conversation would under-report. | autonomous: low-confidence, no authoritative source (values are local to one record type and cheap to change) |
| D12 | Time window | Discovery skips a conversation whose last activity is before `SourceDiscoveryContext.SinceUtc`: Kilo `MAX(session.time_updated)` over the root's tree, Warp `agent_conversations.last_modified_at`, Devin `sessions.last_activity_at`. Cursor does not filter (no indexed activity column; its revision token skips unchanged composers after the first pass), and lists composers from the bubble keys themselves, so a composer whose `composerData:` row is missing is still read. | Reading every Cursor bubble's `createdAt` would scan all 82,036 bubble values on each discovery; a key-only range scan over the unique key index does not read the values. | autonomous: low-confidence, no authoritative source |
| D13 | Kilo subagent sessions | A unit is a root session (`parent_id IS NULL`, `time_archived IS NULL`); its export holds the messages and parts of the root and every non-archived descendant, each row keeping its own `session_id`. | Mirrors the deleted `sqlite-session-parser.ts` recursive `session_tree`. 411 of 510 sessions on the owner's machine are children. | autonomous: recommendation accepted; source archived sqlite-session-parser.ts |
| D14 | Cursor workspace attribution (`ItemTable`) | Discovery builds a composer-to-workspace map from each `Cursor/User/workspaceStorage/<hash>/state.vscdb` `ItemTable` row `composer.composerData` (`allComposers[].composerId`) and sets the unit's `CompanionKey` to that directory's `workspace.json`. The reader returns its text as `CompanionText`; `CursorMapper.MapBubbles` sets the session's `Workspace.WorkingDirectory` from its `folder` URI. | Reuses the existing companion mechanism (Droid `.settings.json`). Mirrors deleted `cursor.ts:214`. A composer in no workspace keeps no workspace. Amended in review cycle 1 (finding `reviewer/cursor-workspace-no-composerdata-warns`): a workspace database with no `composer.composerData` row is a workspace that never used chat, so it holds no composers and adds no problem; only a database that fails to open, or a row that is not valid JSON, is skipped with one warning problem. | autonomous: recommendation accepted; source archived cursor.ts; review-cycle-1 amendment, autonomous: recommendation accepted |
| D18 | Review cycle 1 fix rounds: which task owns each finding? | Each finding is fixed under the task whose files and Test-contract row it touches, and that row names the added case: T3 `WriteJsonColumn` non-finite real and `byte[]` cases; T2 immutable-path escaping, busy/locked and permission-denied classification against real files; T7 a workspace with no `composer.composerData` row stays silent (D14); T9 Devin `metrics.total_time_ms` becomes the record's reported duration, and the field register stops listing it as dropped. | The delivery contract treats review fix rounds as expected work; recording each case in its owner's row keeps every fix dispatch inside one task's scope. `record_reported_duration` already exists and ten mappers set `ReportedDurationMs`, so the T9 change follows an existing pattern; `ttft_ms` stays dropped (no column). | autonomous: recommendation accepted (review cycle 1) |
| D16 | The conversation-source interface, frozen before T4–T8 run in parallel | `ISqliteConversationSource { IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns; IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context); void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer); }` with `SqliteConversationContext(IHarnessSqliteDatabase Database, string DatabasePath, string HarnessId, DateTimeOffset? SinceUtc, string HomeDirectory, IHarnessFileSystem FileSystem, IHarnessSqliteSource Sqlite, ICollection<CollectedProblem> Problems)` and `SqliteConversation(string Id, string RevisionToken, DateTimeOffset? LastActivityUtc, string? CompanionKey)`. | T7 (Cursor workspaces) needs the file system, the opener and a problem sink; fixing the signature in T3 keeps T4–T8 from implementing two shapes. | autonomous: recommendation accepted |
| D17 | SQLite provider initialisation | Extract `BaseSqliteRepository.EnsureSqliteProviderInitialized` into a public static `SqliteProvider.EnsureInitialized()` in `KyberDash.Core/Repositories/SqliteProvider.cs`; `BaseSqliteRepository` and the new adapter both call it. | One registration of `SQLite3Provider_sqlite3` per process; a second copy would duplicate the lock and the `DllNotFoundException` handling. | autonomous: recommendation accepted; source dash/server/KyberDash.Core/Repositories/BaseSqliteRepository.cs |
| D15 | Antigravity `.pb` | `AntigravityTranscriptMapper.Map` records one warning problem `ANTIGRAVITY_PB_UNREADABLE` per `antigravity-pb` unit, naming the file, instead of the silent empty batch. Decoding stays out of scope: the repository has no schema for these files. | Request: record the gap. | autonomous: recommendation accepted |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (plan review findings fixed, PLAN_READY), 2026-11-25 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberDash | architecture | Static harness sources | The SQLite source reader: one unit per conversation, read-only open and fallback rule (D4), limits (D11), problem codes (D6), the four conversation sources and their export contracts (D10). |
| KyberDash | reference | Field register, Devin section | `message_nodes` parse path: which `chat_message` keys map to which columns, and what is dropped (D8, D9). |
| KyberDash | reference | Field register, Cursor section | Workspace attribution from `workspaceStorage` `ItemTable` (D14). |
| KyberDash | operations | Runbook, harness import | The four recorded problem codes and what to do for each, including the macOS privacy permission Warp's folder needs (F6). |
| KyberDash | reference | Telemetry inventory | Kilo, Warp, Cursor and Devin SQLite stores move from "not read" to read; `.pb` stays an unreadable gap (D15). |

## Investigation findings

Read at run base `2f661f462` (CodeGraph on the run worktree, branch `claude/fix/sqlite-harness-readers`)
and, for the base branch D1 stacks on, `claude/fix/droid-chart-and-per-harness-e2e` at `ba33a56ef` via
`git show` (not indexed). Live database schemas were read read-only on 2026-11-25 by a platform
researcher.

- **F1 Pipeline.** `TranscriptIngestionService.ProcessRelationalSourceUnit` calls `reader.Read` then
  `mapper.Map`, one `CollectedBatch` per unit, and records an `IsSourceUnitFailure` exception
  (`IOException`, `InvalidDataException`, …) as a failed unit with a problem. Discovery problems from
  `HarnessSourceRegistry.DiscoverAll` are written by both `CanonRefreshEngine` and the ingestion service.
- **F2 Registry.** Kilo (`kilo-sqlite`), Warp (`warp-sqlite`) and Devin (`devin-sqlite`) are
  `SpecHarnessSourceReader` explicit-file specs; Cursor's `state.vscdb` is inside the private
  `CursorHarnessSourceReader` beside its agent-transcript JSONL. All read through `ReadLocalUnit`,
  which skips the text read for `IsBinaryFormat`. On the base branch `SourceRootSpec` carries
  `ParserContractVersion`.
- **F3 Seam.** `IHarnessFileSystem` (in `IHarnessSourceReader.cs`) has no SQLite access; the readers
  are constructed inside `HarnessSourceRegistry()` with no injection point.
- **F4 Live schemas (owner's machine).**
  - Devin `sessions.db`: `sessions(id PK, working_directory, model, title, created_at, last_activity_at, hidden, metadata JSON, …)`;
    `message_nodes(row_id PK, session_id → sessions.id, node_id, parent_node_id, chat_message JSON, created_at, metadata NULL)`,
    18,559 rows, roles user/assistant/tool/system. `chat_message` keys: `message_id, role, content, tool_calls[]{id,name,arguments,index,kind}, tool_call_id, thinking, metadata{num_tokens, is_user_input, request_id, created_at, started_generation_at, generation_model, finish_reason, metrics{input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, ttft_ms, total_time_ms, …}}`.
    ACU appears only in `sessions.metadata.total_acu_cost`.
  - Kilo `kilo.db`: `session(id PK, parent_id, directory, title, time_created, time_updated, time_archived, …)`, 510 rows, 411 with `parent_id`;
    `message(id PK, session_id, time_created, time_updated, data JSON)`; `part(id PK, message_id, session_id, time_created, time_updated, data JSON)`.
  - Cursor `state.vscdb`: `cursorDiskKV(key TEXT UNIQUE, value BLOB)` with 82,036 `bubbleId:` and 862 `composerData:` keys; `ItemTable(key, value)`.
    Bubble keys present on every sampled row: `tokenCount.inputTokens/outputTokens`, `createdAt`, `requestId`, `type`; `modelInfo.modelName` on 16 of 2,000.
  - Warp `warp.sqlite`: unreadable here — macOS denies the terminal access to the group container (`authorization denied`). Schema taken from the deleted `warp.ts` queries (`agent_conversations`, `ai_queries`, `blocks` with `ai_metadata`).
- **F5 Mapper contracts.** `KiloMapper.ParseSharedRuntime` reads `{session_id, title, messages[], parts[]}`;
  `WarpMapper.Parse` reads `{conversation{conversation_id, conversation_data, last_modified_at}, queries[], blocks[]}`;
  `CursorBubbleDocument.Parse` reads a JSON array of `CursorBubbleRow` (the deleted `BUBBLE_QUERY_BASE`
  projection); `DevinMapper.Parse` reads `{session_id, agent{model_name}, steps[]}`.
- **F6 Warp permission.** A machine whose terminal or app lacks access to the Warp group container
  gets `SQLITE_SOURCE_UNREADABLE` for Warp; only the owner can grant that macOS permission.
- **F7 Antigravity `.pb`.** `AntigravityTranscriptMapper.Map` returns `new CollectedBatch(source, [], [], [])`
  for every `antigravity-pb` unit (and for null `RawText`): silent. No schema for these files exists in the repository.
- **F8 e2e fixtures (base branch, `443c2707b`).** `file-harness-sqlite.ts` builds two sessions of 10 and
  12 turns per harness, but in guessed schemas: Kilo's `session` lacks `time_updated`, `time_archived`
  and `directory`; Devin builds only a `sessions` table with invented columns and no `message_nodes`;
  Warp's `blocks` lacks `ai_metadata`; Cursor has no `composerData` or workspace storage.
- **F12 Expected-failure marker (base `715f74c61`).** `dash/e2e/file-harness-dashboard.spec.ts` lines
  92–98 hold `SQLITE_READER_EXPECTED_FAILURES` (`kilo-shared-runtime`, `warp`, `cursor`, `devin`),
  `SQLITE_READER_EXPECTED_FAILURE_REASON`, and a `test.fail(SQLITE_READER_EXPECTED_FAILURES.has(harness), …)`
  call in the per-harness test. While a harness is in that set, Playwright reports its test as failed when
  it passes ("passed unexpectedly"), so T12–T15 each take their own harness out of the set first, and T15
  removes the set, the reason constant and the `test.fail` call.
- **F9 Standards.** csharp: interface first, in-memory unit tests, `sealed` by default, explicit types,
  XML docs on public members, catch named exceptions, never swallow. sqlite: rules file stale against
  the standard body (D2). ADR 0090 OD-9: no C# test opens a SQLite file.
- **F10 Divergence.** `docs/reference/seams.md` is the seam register, but no KyberDash collector seam
  (`IHarnessFileSystem` included) is registered there; new seams here follow the existing KyberDash
  placement and the gap is a PR note.
- **F11 Real-file tests already exist.** `KyberDash.Data.Tests.SqliteRelationalStoreParityTests` opens real
  `canon.db` files under `TempDirectory`, so ADR 0090's "no test opens a SQLite file" is not the practice (D5).

Discovery: codegraph 8, docs 5, direct reads 9 — `HarnessSourceRegistry.cs` and `IHarnessSourceReader.cs`
(whole private reader classes, returned only in part), `BaseSqliteRepository.cs` lines 30–55 and
`SqliteRelationalStoreParityTests.cs` (confirm one detail each), ADR 0090 lines 183–210 and
`docs/plans/README.md` (docs, read for exact text), `.kyber-weave/kyber-weave.yml` (config, not indexed);
plus `git show` of the base branch and of the deleted TypeScript files (not indexed: `projectPath` and
the CLI index answer only for `claude/fix/sqlite-harness-readers` at `2f661f462`).

### Export contracts (D10)

Each export is UTF-8 JSON written with `Utf8JsonWriter` into the bounded buffer (T2). A JSON column is
written as a raw JSON value when it parses, otherwise as a string; a NULL column is written as JSON `null`.

| Harness | Unit (conversation id) | Revision token | Export shape | Rows and order |
|---|---|---|---|---|
| Kilo | root `session.id` (`parent_id IS NULL AND time_archived IS NULL`) | `<max time_updated over the tree>-<message count over the tree>` | `{"session_id": root id, "title": root title, "messages": [{"id","session_id","time_created","data"}], "parts": [{"id","message_id","session_id","time_created","data"}]}` | Tree = root plus non-archived descendants by `parent_id`; messages `ORDER BY time_created, id`; parts `ORDER BY message_id, id` |
| Warp | `agent_conversations.conversation_id` having at least one `ai_queries` row | `last_modified_at` | `{"conversation": {"conversation_id","conversation_data","last_modified_at"}, "queries": [{"exchange_id","conversation_id","start_ts","input","working_directory","output_status","model_id","planning_model_id","coding_model_id"}], "blocks": [{"block_id","start_ts","stylized_command"}]}` | queries `WHERE conversation_id = ? ORDER BY start_ts`; blocks `WHERE ai_metadata` non-empty `AND json_extract(ai_metadata,'$.conversation_id') = ? ORDER BY start_ts` |
| Cursor | composer id taken from the `bubbleId:<composerId>:<bubbleId>` keys (key-only range scan `key >= 'bubbleId:' AND key < 'bubbleId;'`, grouped by composer) | `<bubble count>-<max bubble rowid>` | JSON array of `{"bubble_key","input_tokens","output_tokens","model","created_at","request_id","user_text","text_length","bubble_type","context_window","code_blocks"}` (`user_text` = first 500 characters of `$.text`) | `key >= 'bubbleId:<id>:' AND key < 'bubbleId:<id>;' ORDER BY ROWID` (index range, no `LIKE`); `CompanionKey` = the composer's `workspace.json` (D14) |
| Devin | `sessions.id` with at least one `message_nodes` row | `<last_activity_at>-<node count>` | `{"session_id","model","title","working_directory","created_at","last_activity_at","session_metadata","message_nodes": [{"node_id","parent_node_id","created_at","chat_message"}]}` | nodes `WHERE session_id = ? ORDER BY row_id` |

Required columns are checked at discovery (D6 `SQLITE_SOURCE_SCHEMA_MISSING`): exactly the columns the
row's queries name.

## Test contract (development-mode: test-first)

Runner for every C# row: `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~<class>"`,
one filter per class the row names. All C# test classes are in `dash/server.tests/KyberDash.Data.Tests`.
Classes ending `SqliteTests` open real SQLite files under a `TempDirectory` (D5, copy
`KyberDash.Data.Tests.SqliteRelationalStoreParityTests`); every other class uses `InMemoryHarnessSqliteSource`
and opens no file. An existing test keeps its assertions; a worker may narrow a locator or fixture query
the new work made ambiguous and names each such edit in its report. The e2e rows (T12–T15) run the
Playwright spec as their evidence step, in the shape of the base branch plan's e2e tasks.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `Collector/SqliteSourceOpenPolicyTests.cs` (`KyberDash.Data.Tests.Collector.SqliteSourceOpenPolicyTests`); `Collector/InMemoryHarnessSqliteSourceTests.cs` (`KyberDash.Data.Tests.Collector.InMemoryHarnessSqliteSourceTests`). Copy the fake style of `StoreRebuildTests.FakeRelationalStoreFileSystem`. | runner with `SqliteSourceOpenPolicyTests` and `InMemoryHarnessSqliteSourceTests` | Policy: plain open succeeds → no action; read-only-directory failure with empty or absent WAL → `Immutable`; same with WAL bytes > 0 → `CacheCopy`; busy/locked → `CacheCopy` (or `Immutable` when WAL empty); copy when database + WAL > copy cap → refuse with `SQLITE_COPY_CAP_EXCEEDED`; missing file → refuse `SQLITE_SOURCE_UNREADABLE`; permission denied on the source → refuse `SQLITE_SOURCE_UNREADABLE` (no copy attempted). Fake: canned rows returned by query name in insertion order; `GetColumns` of an unknown table is empty; a path configured to fail throws `IOException`. | Missing types `SqliteSourceOpenPolicy`, `IHarnessSqliteSource`, `InMemoryHarnessSqliteSource` | All cases pass |
| T2 | `Collector/MicrosoftDataSqliteHarnessSourceSqliteTests.cs` (`KyberDash.Data.Tests.Collector.MicrosoftDataSqliteHarnessSourceSqliteTests`), using the new helper `Collector/SqliteFixtureDatabase.cs` | runner with `MicrosoftDataSqliteHarnessSourceSqliteTests` and `SqliteRelationalStoreParityTests` (the provider extraction must keep it green) | Real files: a query streams rows in order and `GetColumns` lists a table's columns (empty for a missing table); opening never changes the source file's bytes or mtime and creates no sidecar beside a database with no WAL; a database in a read-only directory with an empty WAL opens through `immutable=1`; a database in a read-only directory with a non-empty WAL is read through a copy under the cache root and the copy holds the WAL's rows; a second open of the unchanged source reuses that copy and a changed source replaces it (the superseded copy is gone); a copy over the copy cap → `InvalidDataException` starting `SQLITE_COPY_CAP_EXCEEDED`; a missing file → `IOException` starting `SQLITE_SOURCE_UNREADABLE`; a statement over the command timeout → `IOException` naming the timeout. Classification and escaping (D18): a database in a read-only directory whose name contains `#`, `?` and `%` opens through `immutable=1` and reads its rows; a database held under an exclusive write lock by another connection is classified busy and read through the immutable or copy fallback, not refused; a source file the process cannot read → `IOException` starting `SQLITE_SOURCE_UNREADABLE` and no copy under the cache root. Read-only-directory and permission cases are skipped on Windows with the reason in the skip message. | Missing `MicrosoftDataSqliteHarnessSource`, `SqliteProvider`, `SqliteFixtureDatabase` | All pass; `SqliteRelationalStoreParityTests` still green |
| T3 | `Collector/SqliteHarnessSourceReaderTests.cs` (`KyberDash.Data.Tests.Collector.SqliteHarnessSourceReaderTests`), with a test-local `ISqliteConversationSource` stub | runner with `SqliteHarnessSourceReaderTests` | Discover: one unit per listed conversation with `SourceKey` `<path>#<id>`, the given format, parser id, contract version and revision; no database file → no units, no problem; open failure → one `SQLITE_SOURCE_UNREADABLE` problem, no units; a required column missing → one `SQLITE_SOURCE_SCHEMA_MISSING` problem naming table and column, no units; `SinceUtc` passed through. Read: `RawText` is the stub's export; export over the cap → `InvalidDataException` starting `SQLITE_EXPORT_CAP_EXCEEDED`; database unreadable at read time → `IOException` starting `SQLITE_SOURCE_UNREADABLE`; `CompanionKey` present → `CompanionText` read through `IHarnessFileSystem`; a `SourceKey` without `#` → `InvalidDataException`. `WriteJsonColumn` (D18): a non-finite real (`double.NaN`, infinity) → `null`; a `byte[]` → a base64 string. | Missing `SqliteHarnessSourceReader` | All pass |
| T4 | `Harnesses/KiloSqliteConversationSourceTests.cs` (`KyberDash.Data.Tests.Harnesses.KiloSqliteConversationSourceTests`) and `Harnesses/KiloSqliteConversationSourceSqliteTests.cs` (`KyberDash.Data.Tests.Harnesses.KiloSqliteConversationSourceSqliteTests`, a real `kilo.db` in the F4 schema); extend `KyberDash.Data.Tests.Harnesses.KiloMapperTests` | runner with `KiloSqliteConversationSourceTests`, `KiloSqliteConversationSourceSqliteTests` and `KiloMapperTests` | Roots only listed (child and archived excluded); revision from tree max `time_updated` and message count; `SinceUtc` excludes a tree whose latest update is older; export of a root with one child holds both sessions' messages and parts, each with its own `session_id`, in the D10 order; `data` written raw when JSON, as a string when not. Round trip: the export through `KiloMapper.Map` yields the fixture's turns. `KiloMapper.Map` with `RawText` null → one warning `KILO_MALFORMED_LINE` problem naming the source; whitespace text → empty batch, no problem. | Missing `KiloSqliteConversationSource`; mapper null case returns no problem | All pass |
| T5 | `Harnesses/WarpSqliteConversationSourceTests.cs` (`KyberDash.Data.Tests.Harnesses.WarpSqliteConversationSourceTests`) and `Harnesses/WarpSqliteConversationSourceSqliteTests.cs` (`KyberDash.Data.Tests.Harnesses.WarpSqliteConversationSourceSqliteTests`, a real `warp.sqlite` in the schema of the T5 task); extend `KyberDash.Data.Tests.Harnesses.DevinWarpMapperTests` for Warp | runner with `WarpSqliteConversationSourceTests`, `WarpSqliteConversationSourceSqliteTests` and `DevinWarpMapperTests` | Conversations without queries not listed; revision = `last_modified_at`; `SinceUtc` filter; export shape and order per D10; blocks only those whose `ai_metadata.conversation_id` matches; round trip through `WarpMapper.Map` yields one record per exchange. `WarpMapper.Map` null `RawText` → one warning `WARP_MALFORMED_DOCUMENT` problem; whitespace → today's behaviour. | Missing class; null case silent | All pass |
| T6 | `Harnesses/CursorSqliteConversationSourceTests.cs` (`KyberDash.Data.Tests.Harnesses.CursorSqliteConversationSourceTests`) and `Harnesses/CursorSqliteConversationSourceSqliteTests.cs` (`KyberDash.Data.Tests.Harnesses.CursorSqliteConversationSourceSqliteTests`, a real `state.vscdb`); extend `KyberDash.Data.Tests.Harnesses.CursorMapperTests` | runner with `CursorSqliteConversationSourceTests`, `CursorSqliteConversationSourceSqliteTests` and `CursorMapperTests` | One unit per composer found in bubble keys, including a composer with no `composerData:` row; non-bubble keys ignored; revision = count and max rowid; export is the D10 array, `user_text` cut at 500 characters, `text_length` the full length, NULL token fields written as `null`; a bubble with no `modelInfo.modelName` exports `model: null` (no fill, D9). Round trip through `CursorMapper.Map` yields the reported tokens. `CursorMapper.Map` null `RawText` → one warning problem in `CursorMapper.MalformedLineCode`; whitespace → today's behaviour. | Missing class; null case silent | All pass |
| T7 | `Harnesses/CursorWorkspaceAttributionTests.cs` (`KyberDash.Data.Tests.Harnesses.CursorWorkspaceAttributionTests`); extend `KyberDash.Data.Tests.Harnesses.CursorMapperTests` | runner with `CursorWorkspaceAttributionTests`, `CursorMapperTests` and `KyberDash.Data.Tests.Collector.FieldRegisterTests` | Two workspace databases each claiming one composer → each unit's `CompanionKey` is that workspace's `workspace.json`; a composer in no workspace → `CompanionKey` null; a workspace database with no `composer.composerData` row → no composers and no problem (D14, review cycle 1); a workspace database that fails to open, or whose row is malformed JSON → skipped with one warning problem, other workspaces still attributed. Mapper: `CompanionText` `{"folder":"file:///Users/x/repo"}` → session `Workspace.WorkingDirectory` `/Users/x/repo` (percent-decoded); no companion → no workspace; a `folder` that is not a `file:` URI → no workspace and one warning problem. | Missing attribution; mapper ignores companion | All pass |
| T8 | `Harnesses/DevinSqliteConversationSourceTests.cs` (`KyberDash.Data.Tests.Harnesses.DevinSqliteConversationSourceTests`) and `Harnesses/DevinSqliteConversationSourceSqliteTests.cs` (`KyberDash.Data.Tests.Harnesses.DevinSqliteConversationSourceSqliteTests`, a real `sessions.db` in the F4 schema) | runner with `DevinSqliteConversationSourceTests` and `DevinSqliteConversationSourceSqliteTests` | Sessions with no nodes not listed (hidden sessions are listed); revision `<last_activity_at>-<count>`; `SinceUtc` on `last_activity_at`; export per D10 with nodes in `row_id` order, `chat_message` raw, `session_metadata` raw or `null`. | Missing class | All pass |
| T9 | `Harnesses/DevinMessageNodesMapperTests.cs` (`KyberDash.Data.Tests.Harnesses.DevinMessageNodesMapperTests`), which also holds the Devin null and whitespace `RawText` cases | runner with `DevinMessageNodesMapperTests`, `DevinWarpMapperTests` (unchanged, must stay green) and `KyberDash.Data.Tests.Collector.FieldRegisterTests` | A D10 export of 2 user + 2 assistant (+ tool, system) nodes → one `LlmInvoke` record per assistant node with `metrics` (input, output, cache read, cache creation from `metadata.metrics`), model from `metadata.generation_model` else the export's `model`, start time from `metadata.created_at`, the preceding user `content` as user message, one tool record per `tool_calls[]` entry (`id`, `name`, `arguments`); an assistant node without `metrics` → record with no token rows (D9); tool and system nodes → no record; no per-record cost; session title and working directory from the export; a node whose `chat_message` is not an object → one `DEVIN_MALFORMED_DOCUMENT` problem, others still mapped; transcript-format input unchanged (existing tests stay green). `DevinMapper.Map` null `RawText` → one warning problem. An assistant node whose `metadata.metrics.total_time_ms` is a number → the record's `ReportedDurationMs` holds it; absent → no reported duration (D9, D18). `FieldRegisterTests` passes with the register updated. | Parse rejects `message_nodes` root; null case silent | All pass, plus `KyberDash.Data.Tests.Collector.FieldRegisterTests` |
| T10 | `Harnesses/AntigravityPbProblemTests.cs` (`KyberDash.Data.Tests.Harnesses.AntigravityPbProblemTests`) | runner with `AntigravityPbProblemTests` and `AntigravityTranscriptMapperTests` | An `antigravity-pb` unit → empty records and one warning `ANTIGRAVITY_PB_UNREADABLE` problem naming the file; a JSONL unit with null `RawText` → one warning problem; a JSONL unit with whitespace text → today's empty batch. | Mapper returns no problem | All pass |
| T11 | extend `KyberDash.Data.Tests.Collector.HarnessSourceRegistryTests` | runner with `HarnessSourceRegistryTests` | With an `InMemoryHarnessSqliteSource` at each harness's explicit path: Kilo, Warp, Devin (sqlite) and Cursor (vscdb) discover one unit per conversation with version `SqliteRowExportParserContractVersion`, and `GetReader(h).Read` returns the export; Devin transcripts and Cursor agent JSONL still discover as before; an unreadable Warp database → one `SQLITE_SOURCE_UNREADABLE` discovery problem from `DiscoverAll`; `ReadLocalUnit` no longer treats `-sqlite`/`-vscdb` specially. | Registry still uses spec readers for the four stores | All pass |
| T12 | `dash/e2e/file-harness-sqlite.ts` (Kilo builder); `dash/e2e/file-harness-dashboard.spec.ts` (`kilo-shared-runtime` out of `SQLITE_READER_EXPECTED_FAILURES`, F12) | Gate: `npm --prefix dash run typecheck && npm --prefix dash run lint`. Evidence: `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/file-harness-dashboard.spec.ts --grep "^kilo-shared-runtime:"` | `kilo-shared-runtime` is no longer marked `test.fail`; the Kilo fixture is built in the real schema: `session(id, parent_id, directory, title, time_created, time_updated, time_archived)`, `message(id, session_id, time_created, time_updated, data)`, `part(id, message_id, session_id, time_created, time_updated, data)`; two root sessions of 10 and 12 turns, one with a child session; the `kilo-shared-runtime` spec test passes with its existing assertions | Evidence RED, taken after removing `kilo-shared-runtime` from the set and before the fixture change: the `kilo-shared-runtime` test fails as a plain failure, not an expected one (record the failing test name and the first failing assertion) | Gate passes; the `kilo-shared-runtime` test passes as a normal test (not expected-to-fail) |
| T13 | `dash/e2e/file-harness-sqlite.ts` (Warp builder); `dash/e2e/file-harness-dashboard.spec.ts` (`warp` out of the set) | Gate as T12. Evidence: same spec with `--grep "^warp:"` | `warp` is no longer marked `test.fail`; the Warp fixture's `blocks` table gains `ai_metadata` holding `{"conversation_id": …}` for each command block; `agent_conversations` and `ai_queries` keep their columns; the `warp` test passes | Evidence RED, after removing `warp` from the set and before the fixture change: the `warp` test fails (record name and first failing assertion) | Gate passes; the `warp` test passes as a normal test |
| T14 | `dash/e2e/file-harness-sqlite.ts` (Cursor builder); `dash/e2e/file-harness-dashboard.spec.ts` (`cursor` out of the set) | Gate as T12. Evidence: same spec with `--grep "^cursor:"` | `cursor` is no longer marked `test.fail`; the Cursor fixture adds a `composerData:<id>` row per composer and one `Cursor/User/workspaceStorage/<hash>/` directory holding a `state.vscdb` whose `ItemTable` row `composer.composerData` lists one composer and a `workspace.json` with a `folder` URI; the `cursor` test passes | Evidence RED, after removing `cursor` from the set and before the fixture change: the `cursor` test fails (record name and first failing assertion) | Gate passes; the `cursor` test passes as a normal test |
| T15 | `dash/e2e/file-harness-sqlite.ts` (Devin builder); `dash/e2e/file-harness-dashboard.spec.ts` (delete `SQLITE_READER_EXPECTED_FAILURES`, `SQLITE_READER_EXPECTED_FAILURE_REASON` and the `test.fail` call; Devin contract values only where the real schema now carries them) | Gate as T12. Evidence: same spec with `--grep "^devin:"`, then the whole spec once | No harness test is marked `test.fail`; the Devin fixture is built in the real schema: `sessions(id, working_directory, model, title, created_at, last_activity_at, hidden, metadata)` and `message_nodes(row_id, session_id, node_id, parent_node_id, chat_message, created_at, metadata)`, each session holding user, assistant (with `metadata.metrics` input/output/cache tokens, `generation_model`, `created_at`, `tool_calls`), tool and system nodes, 10 and 12 turns; the `devin` test passes; the whole spec passes | Evidence RED, after deleting the set, the reason constant and the `test.fail` call and before the fixture change: the `devin` test fails (record name and first failing assertion) | Gate passes; the `devin` test and the whole spec pass, with no `test.fail` left in the spec |

## Tasks

Every task's first acceptance line is the D1 precondition: `HarnessSourceRegistry.NoEstimatedTokensParserContractVersion`
exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts`
exists. When either is missing, the run branch is not on its base: stop and return BLOCKED naming D1.

### T1: SQLite source seam, open policy and in-memory fake

- **Objective:** Add the read-only SQLite seam, the pure open policy that decides the fallback, the limits and problem codes, and the in-memory fake every unit test uses.
- **Capabilities:** C# .NET library code with xUnit unit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `reader`
- **Depends on:** none
- **Files:**
  - `dash/server/KyberDash.Data/Collector/IHarnessSqliteSource.cs`: new, namespace `KyberDash.Data.Collector.Sources`. Interfaces `IHarnessSqliteSource` (`IHarnessSqliteDatabase Open(string databasePath)`) and `IHarnessSqliteDatabase : IDisposable` (`IEnumerable<IReadOnlyDictionary<string, object?>> Query(HarnessSqliteQuery query)`, yielding one row at a time; `IReadOnlySet<string> GetColumns(string table)`, empty when the table is absent). Records `HarnessSqliteQuery(string Name, string Sql, IReadOnlyList<object?> Parameters)` and `HarnessSqliteLimits(long ExportCapBytes = 67_108_864, long CopyCapBytes = 4_294_967_296, int CommandTimeoutSeconds = 30, int BusyTimeoutMilliseconds = 1000)`.
  - `dash/server/KyberDash.Data/Collector/SqliteSourceOpenPolicy.cs`: new `static class SqliteSourceOpenPolicy` with the problem-code constants `SourceUnreadableCode = "SQLITE_SOURCE_UNREADABLE"`, `SchemaMissingCode = "SQLITE_SOURCE_SCHEMA_MISSING"`, `ExportCapExceededCode = "SQLITE_EXPORT_CAP_EXCEEDED"`, `CopyCapExceededCode = "SQLITE_COPY_CAP_EXCEEDED"`, an enum `SqliteOpenFailure` (`None`, `ReadOnlyDirectory`, `MissingSidecar`, `Busy`, `Missing`, `PermissionDenied`), and `SqliteOpenAction Decide(SqliteOpenFailure failure, long walBytes, long databaseBytes, long copyCapBytes)` returning `Open`, `Immutable`, `CacheCopy` or `Refuse(code)`. The rule (D4): `None` → `Open`; `ReadOnlyDirectory`, `MissingSidecar` or `Busy` with `walBytes == 0` → `Immutable`; the same with `walBytes > 0` → `CacheCopy` when `databaseBytes + walBytes <= copyCapBytes`, else `Refuse(CopyCapExceededCode)`; `Missing` or `PermissionDenied` → `Refuse(SourceUnreadableCode)`.
  - `dash/server.tests/KyberDash.Data.Tests/Collector/InMemoryHarnessSqliteSource.cs`: new `sealed` fake: databases keyed by path, each with table columns and canned rows per query `Name` (returned in insertion order, parameters recorded for assertions), and a per-path switch that makes `Open` throw `IOException` with a `SQLITE_SOURCE_UNREADABLE` message.
  - `dash/server.tests/KyberDash.Data.Tests/Collector/SqliteSourceOpenPolicyTests.cs`, `dash/server.tests/KyberDash.Data.Tests/Collector/InMemoryHarnessSqliteSourceTests.cs`: new tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T1 --red`
  - `kyber-weave review gates . --agent <you> --task T1`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T1 passes; the build has no new warning; no package reference is added.

### T2: Microsoft.Data.Sqlite read-only adapter with immutable and cache-copy fallback

- **Objective:** Implement `IHarnessSqliteSource` over `Microsoft.Data.Sqlite.Core` (already referenced through `KyberDash.Core`; add no package), executing `SqliteSourceOpenPolicy`, and prove it against real files in the style of `KyberDash.Data.Tests.SqliteRelationalStoreParityTests`.
- **Capabilities:** C# .NET library code over SQLite with xUnit tests on real temporary SQLite files
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `adapter`
- **Depends on:** T1 (the seam, policy, limits and codes)
- **Failure mode:** a copy taken while the harness writes its WAL can pair a database file and a `-wal` from different moments, so the copy silently reads torn or stale rows, and only under timing.
- **Mitigations:**
  - Fingerprint the source (database size and mtime plus `-wal` size and mtime) before copying; copy into temporary names; fingerprint again after; when they differ, delete the temporaries and throw `IOException` (`SQLITE_SOURCE_UNREADABLE`, "changed while copying").
  - Publish the `-wal` copy before the database copy, each by rename, so a reader never sees the database without its WAL.
  - Name the copy `<sha256(source path)[..32]>.<sha256(fingerprint)[..16]>.db`, so a changed source gets a new file and a reader of the old copy is never overwritten; delete the superseded copy for the same source after publishing.
  - Open the copy `Mode=ReadOnly` too; never open the source with any write mode.
- **Files:**
  - `dash/server/KyberDash.Core/Repositories/SqliteProvider.cs`: new `public static class SqliteProvider` with `EnsureInitialized()`, moved from `BaseSqliteRepository.EnsureSqliteProviderInitialized` (same lock, same `SQLite3Provider_sqlite3`, same `SqliteExecutionException` on a missing library), so the provider is registered once per process.
  - `dash/server/KyberDash.Core/Repositories/BaseSqliteRepository.cs`: `EnsureSqliteProviderInitialized` calls `SqliteProvider.EnsureInitialized()`.
  - `dash/server/KyberDash.Data/Collector/MicrosoftDataSqliteHarnessSource.cs`: new `sealed class MicrosoftDataSqliteHarnessSource : IHarnessSqliteSource`, constructor `(HarnessSqliteLimits limits, string cacheRoot)`, plus a static `CreateDefault()` whose cache root is `<directory of CanonDbPath.Resolve()>/sqlite-ro`. Opens `Data Source=<path>;Mode=ReadOnly` (immutable: `Data Source=file:<escaped path>?immutable=1;Mode=ReadOnly`), sets `PRAGMA busy_timeout`, `CommandTimeout`, and streams each query through one forward-only `SqliteDataReader`. Maps `SqliteException` codes to `SqliteOpenFailure` (`SQLITE_READONLY*` → `ReadOnlyDirectory`; `SQLITE_CANTOPEN` with a `-wal` present → `MissingSidecar`; `SQLITE_BUSY`/`SQLITE_LOCKED` → `Busy`; no file → `Missing`; `UnauthorizedAccessException` or `SQLITE_AUTH`/`authorization denied` → `PermissionDenied`), asks the policy, and throws `IOException` (unreadable) or `InvalidDataException` (cap) whose message starts with the code and names the path. `<remarks>` say why: the system SQLite through `SQLitePCLRaw.provider.sqlite3` (no bundled binary, no `sqlite3` process per query), and the fallback order mirrors the deleted TypeScript reader (`git show 'f4fbf7db3^:dash/archived/src/ingest/sqlite.ts'`, `openDatabase` and `openReadonlyCache`).
  - `dash/server.tests/KyberDash.Data.Tests/Collector/SqliteFixtureDatabase.cs`: new test helper: creates a SQLite file under a given directory with `Mode=ReadWriteCreate`, runs DDL and parameterised inserts, optionally leaves WAL mode on with uncheckpointed rows, and disposes the connection.
  - `dash/server.tests/KyberDash.Data.Tests/Collector/MicrosoftDataSqliteHarnessSourceSqliteTests.cs`: new tests per the Test contract (copy `KyberDash.Data.Tests.SqliteRelationalStoreParityTests`: `TempDirectory`, `IDisposable`).
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T2 --red`
  - `kyber-weave review gates . --agent <you> --task T2`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T2 passes; `KyberDash.Data.Tests.SqliteRelationalStoreParityTests` still passes.

### T3: Generic conversation reader with bounded export and recorded problems

- **Objective:** Add `SqliteHarnessSourceReader`, the `IHarnessSourceReader` that turns each conversation an `ISqliteConversationSource` lists into a unit and reads it into a capped JSON export, recording every failure (D3, D6, D16).
- **Capabilities:** C# .NET library code with xUnit unit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `reader`
- **Depends on:** T1 (the seam, fake, limits and problem codes)
- **Files:**
  - `dash/server/KyberDash.Data/Collector/ISqliteConversationSource.cs`: new, exactly as D16: `ISqliteConversationSource { IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; } IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context); void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer); }`, records `SqliteConversationContext(IHarnessSqliteDatabase Database, string DatabasePath, string HarnessId, DateTimeOffset? SinceUtc, string HomeDirectory, IHarnessFileSystem FileSystem, IHarnessSqliteSource Sqlite, ICollection<CollectedProblem> Problems)` and `SqliteConversation(string Id, string RevisionToken, DateTimeOffset? LastActivityUtc, string? CompanionKey)`.
  - `dash/server/KyberDash.Data/Collector/SqliteHarnessSourceReader.cs`: new `sealed class SqliteHarnessSourceReader : IHarnessSourceReader`; constructor `(string harnessId, string providerId, string parserId, string format, string parserContractVersion, IReadOnlyList<string> explicitRelativeDatabasePaths, ISqliteConversationSource conversations, IHarnessSqliteSource sqlite, HarnessSqliteLimits limits)`. Discover: for each existing database path, open, check `RequiredColumns` (missing → `SQLITE_SOURCE_SCHEMA_MISSING` problem naming table and column, no units), list conversations, and build one `SourceUnit` each with `SourceKey` `<full path>#<id>`, `SourceRootLabel` the database file name, `RevisionToken`, `LastWriteUtc` = `LastActivityUtc`, `CompanionKey`, `Kind = SourceKind.File`; any `IOException`/`InvalidDataException` becomes a `SQLITE_SOURCE_UNREADABLE` problem (D6 shape: severity `warning`, `HarnessId`, `Location` the path, `ProblemKey` `sqlite-source:<code>:<harness>:<path>`). Read: split `SourceKey` at the last `#` (none → `InvalidDataException`), open, write the export through a `Utf8JsonWriter` over a bounded `Stream` that throws `InvalidDataException` (`SQLITE_EXPORT_CAP_EXCEEDED`) past `ExportCapBytes`, read `CompanionText` through `IHarnessFileSystem` when `CompanionKey` exists, and return `NativeSourceUnit` with `SizeBytes` the export length. A public static helper `WriteJsonColumn(Utf8JsonWriter writer, string propertyName, object? value)` writes a value as raw JSON when it is text that parses as a JSON object or array, as a string otherwise, as a number for integers and finite reals, `null` for a non-finite real, a base64 string for a `byte[]`, and `null` for NULL; the conversation sources call it.
  - `dash/server.tests/KyberDash.Data.Tests/Collector/SqliteHarnessSourceReaderTests.cs`: new tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T3 --red`
  - `kyber-weave review gates . --agent <you> --task T3`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T3 passes; discovery never throws for a bad database: it returns the problem.

### T4: Kilo conversation source and Kilo null-payload problem

- **Objective:** Add the Kilo `message`/`part` conversation source (D10 Kilo row, D13) and make `KiloMapper.Map` record a problem for a null payload.
- **Capabilities:** C# .NET library code with SQLite queries, xUnit unit tests and real-file SQLite tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `kilo`
- **Depends on:** T2 (adapter and `SqliteFixtureDatabase`), T3 (`ISqliteConversationSource`, fake, `WriteJsonColumn`)
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Kilo/KiloSqliteConversationSource.cs`: new `sealed class KiloSqliteConversationSource : ISqliteConversationSource`. Schema (owner's machine, 2026-11-25): `session(id PK, parent_id, directory, title, time_created, time_updated, time_archived, …)`, `message(id PK, session_id, time_created, time_updated, data JSON)`, `part(id PK, message_id, session_id, time_created, time_updated, data JSON)`; `RequiredColumns` lists exactly the columns below. Units: root sessions (`parent_id IS NULL AND time_archived IS NULL`). Tree: `WITH RECURSIVE session_tree(id) AS (SELECT id FROM session WHERE id = ? UNION SELECT child.id FROM session child JOIN session_tree parent ON child.parent_id = parent.id WHERE child.time_archived IS NULL)` (the deleted reader's shape: `git show 'f4fbf7db3^:dash/archived/src/providers/sqlite-session-parser.ts'`, around line 265). Revision `<MAX(time_updated) over the tree>-<COUNT(message) over the tree>`; `LastActivityUtc` = that max (milliseconds since epoch); skip a root whose max is before `SinceUtc`. Export: `{"session_id": <root id>, "title": <root title>, "messages": [{"id","session_id","time_created","data"}], "parts": [{"id","message_id","session_id","time_created","data"}]}`, messages `ORDER BY time_created, id`, parts `ORDER BY message_id, id`, each row keeping its own `session_id`, `data` through `WriteJsonColumn`. Parameters bound, never concatenated.
  - `dash/server/KyberDash.Data/Harnesses/Kilo/KiloMapper.cs`: `Map`: `RawText == null` → the batch carries one warning problem in `MalformedLineCode` whose message names the source key ("carried no readable content"); whitespace text keeps today's empty batch.
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/KiloSqliteConversationSourceTests.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/KiloSqliteConversationSourceSqliteTests.cs`: new; `dash/server.tests/KyberDash.Data.Tests/Harnesses/KiloMapperTests.cs`: add the null and whitespace cases.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T4 --red`
  - `kyber-weave review gates . --agent <you> --task T4`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T4 passes; `KiloMapper.ParseSharedRuntime` is unchanged.

### T5: Warp conversation source and Warp null-payload problem

- **Objective:** Add the Warp `agent_conversations`/`ai_queries`/`blocks` conversation source (D10 Warp row) and make `WarpMapper.Map` record a problem for a null payload.
- **Capabilities:** C# .NET library code with SQLite queries, xUnit unit tests and real-file SQLite tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `warp`
- **Depends on:** T2 (adapter and `SqliteFixtureDatabase`), T3 (`ISqliteConversationSource`, fake, `WriteJsonColumn`)
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Warp/WarpSqliteConversationSource.cs`: new `sealed class WarpSqliteConversationSource : ISqliteConversationSource`. Schema (from the deleted `warp.ts`; the owner's live database is not readable without a macOS permission): `agent_conversations(conversation_id, conversation_data, last_modified_at)`, `ai_queries(exchange_id, conversation_id, start_ts, input, working_directory, output_status, model_id, planning_model_id, coding_model_id)`, `blocks(block_id, start_ts, stylized_command, ai_metadata)`. Units: conversations with at least one `ai_queries` row; revision and `LastActivityUtc` = `last_modified_at`; skip when before `SinceUtc`. Export: `{"conversation": {"conversation_id","conversation_data","last_modified_at"}, "queries": [<the nine ai_queries columns>], "blocks": [{"block_id","start_ts","stylized_command"}]}`; queries `WHERE conversation_id = ? ORDER BY start_ts ASC`; blocks `WHERE ai_metadata IS NOT NULL AND ai_metadata <> '' AND json_extract(ai_metadata, '$.conversation_id') = ? ORDER BY start_ts ASC`; `conversation_data` and every other value written as a string (the mapper reads them as strings).
  - `dash/server/KyberDash.Data/Harnesses/Warp/WarpMapper.cs`: `Map`: `RawText == null` → one warning problem in `MalformedDocumentCode` naming the source key; whitespace keeps today's behaviour.
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/WarpSqliteConversationSourceTests.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/WarpSqliteConversationSourceSqliteTests.cs`: new; `dash/server.tests/KyberDash.Data.Tests/Harnesses/DevinWarpMapperTests.cs`: add the Warp null and whitespace cases.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T5 --red`
  - `kyber-weave review gates . --agent <you> --task T5`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T5 passes; `WarpMapper.Parse` is unchanged.

### T6: Cursor bubble conversation source and Cursor null-payload problem

- **Objective:** Add the Cursor `cursorDiskKV` bubble conversation source, one unit per composer (D10 Cursor row), and make `CursorMapper.Map` record a problem for a null payload.
- **Capabilities:** C# .NET library code with SQLite queries, xUnit unit tests and real-file SQLite tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `cursor`
- **Depends on:** T2 (adapter and `SqliteFixtureDatabase`), T3 (`ISqliteConversationSource`, fake, `WriteJsonColumn`)
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Cursor/CursorSqliteConversationSource.cs`: new `sealed class CursorSqliteConversationSource : ISqliteConversationSource`. Schema: `cursorDiskKV(key TEXT UNIQUE, value BLOB)`; keys `bubbleId:<composerId>:<bubbleId>` (82,036 on the owner's machine) and `composerData:<composerId>`. Units: composers grouped from a key-only range scan `WHERE key >= 'bubbleId:' AND key < 'bubbleId;'` (no `LIKE`, no value read); revision `<bubble count>-<MAX(rowid)>`; no `SinceUtc` filter (D12). Export: a JSON array, rows `WHERE key >= 'bubbleId:<id>:' AND key < 'bubbleId:<id>;' ORDER BY ROWID`, each `{"bubble_key": key, "input_tokens": $.tokenCount.inputTokens, "output_tokens": $.tokenCount.outputTokens, "model": $.modelInfo.modelName, "created_at": $.createdAt, "request_id": $.requestId, "user_text": substr($.text, 1, 500), "text_length": length($.text), "bubble_type": $.type, "context_window": $.contextWindow, "code_blocks": $.codeBlocks as text}` via `json_extract(value, …)`; a missing value is JSON `null`, never a default (D9). These keys are `CursorBubbleRow`'s `JsonPropertyName`s.
  - `dash/server/KyberDash.Data/Harnesses/Cursor/CursorMapper.cs`: `Map`: `RawText == null` → one warning problem in `MalformedLineCode` naming the source key; whitespace keeps today's behaviour.
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/CursorSqliteConversationSourceTests.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CursorSqliteConversationSourceSqliteTests.cs`: new; `dash/server.tests/KyberDash.Data.Tests/Harnesses/CursorMapperTests.cs`: add the null and whitespace cases.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T6 --red`
  - `kyber-weave review gates . --agent <you> --task T6`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T6 passes; `CursorBubbleDocument.Parse` is unchanged.

### T7: Cursor workspace attribution from workspace `ItemTable`

- **Objective:** Attribute each Cursor composer to its workspace through the workspace databases' `ItemTable` and `workspace.json` (D14), and set the session workspace in `CursorMapper.MapBubbles`.
- **Capabilities:** C# .NET library code with SQLite queries and xUnit unit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `cursor`
- **Depends on:** T6 (the same conversation source and mapper)
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Cursor/CursorSqliteConversationSource.cs`: in `ListConversations`, build the composer-to-`workspace.json` map once from every `<directory of the global database>/../../workspaceStorage/<hash>/state.vscdb` (that is `Cursor/User/workspaceStorage`), opened through `context.Sqlite`, reading `SELECT value FROM ItemTable WHERE key = 'composer.composerData'` and its `allComposers[].composerId`; the composer's `CompanionKey` is the `workspace.json` in that same hash folder when the context's file system says it exists. A workspace database that fails to open or holds malformed JSON adds one warning problem to the context's problem list and is skipped; one with no `composer.composerData` row holds no composers and adds no problem (D14).
  - `dash/server/KyberDash.Data/Harnesses/Cursor/CursorMapper.cs`: `MapBubbles`: when `CompanionText` holds `{"folder": "file:///…"}`, each composer session gets `Workspace.WorkingDirectory` = the percent-decoded local path; a `folder` that is not a `file:` URI → no workspace and one warning problem; no companion → no workspace.
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/CursorWorkspaceAttributionTests.cs`: new; `dash/server.tests/KyberDash.Data.Tests/Harnesses/CursorMapperTests.cs`: companion cases.
  - `docs/dash/field-register.md`: Cursor section, a row for the workspace companion, when `FieldRegisterTests` requires one.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T7 --red`
  - `kyber-weave review gates . --agent <you> --task T7`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T7 passes, including `KyberDash.Data.Tests.Collector.FieldRegisterTests`.

### T8: Devin sessions and message_nodes conversation source

- **Objective:** Add the Devin `sessions` + `message_nodes` conversation source (D10 Devin row).
- **Capabilities:** C# .NET library code with SQLite queries, xUnit unit tests and real-file SQLite tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `devin-source`
- **Depends on:** T2 (adapter and `SqliteFixtureDatabase`), T3 (`ISqliteConversationSource`, fake, `WriteJsonColumn`)
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Devin/DevinSqliteConversationSource.cs`: new `sealed class DevinSqliteConversationSource : ISqliteConversationSource`. Schema (owner's machine): `sessions(id TEXT PK, working_directory, model, title, created_at, last_activity_at, hidden, metadata JSON, …)`, `message_nodes(row_id PK, session_id → sessions.id, node_id, parent_node_id, chat_message JSON, created_at, metadata)`. Units: sessions with at least one node (hidden sessions included); revision `<last_activity_at>-<node count>`; `LastActivityUtc` = `last_activity_at`; skip when before `SinceUtc`. Export: `{"session_id","model","title","working_directory","created_at","last_activity_at","session_metadata": <sessions.metadata>, "message_nodes": [{"node_id","parent_node_id","created_at","chat_message"}]}`, nodes `WHERE session_id = ? ORDER BY row_id`, `session_metadata` and `chat_message` through `WriteJsonColumn`.
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/DevinSqliteConversationSourceTests.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/DevinSqliteConversationSourceSqliteTests.cs`: new.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T8 --red`
  - `kyber-weave review gates . --agent <you> --task T8`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T8 passes.

### T9: Devin mapper parses the message_nodes export

- **Objective:** Teach `DevinMapper` the Devin export so `message_nodes` conversations become records (D8, D9), and record a problem for a null payload.
- **Capabilities:** C# .NET library code with xUnit unit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `devin-mapper`
- **Depends on:** T7 (both edit `docs/dash/field-register.md`; this task's Devin rows land after the Cursor row). The mapper work needs only the D10 contract and can run beside the Devin source task.
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Devin/DevinMapper.cs`: input contract: `{"session_id","model","title","working_directory","created_at","last_activity_at","session_metadata","message_nodes": [{"node_id","parent_node_id","created_at","chat_message": {message_id, role, content, tool_calls[]{id,name,arguments,index,kind}, tool_call_id, thinking, metadata{num_tokens, is_user_input, request_id, created_at, started_generation_at, generation_model, finish_reason, metrics{input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens, ttft_ms, total_time_ms, …}}}}]}`. `Parse` recognises a root holding a `message_nodes` array and maps each node to a `DevinStep`: role `user` → `IsUserInput` true and `MessageText` from `content`; role `assistant` → `ModelName` from `metadata.generation_model` else the export's `model`, `DevinStepMetadata(CreatedAt: metadata.created_at, GenerationModel, InputTokens: metrics.input_tokens, OutputTokens: metrics.output_tokens, CacheCreationTokens: metrics.cache_creation_tokens, CacheReadTokens: metrics.cache_read_tokens)`, `metrics.total_time_ms` carried to the record's `ReportedDurationMs` (D18), and `DevinToolCall(id, name, arguments raw)` per `tool_calls[]`; roles `tool` and `system` → no step. No cost is set (D9). The export's `title` and `working_directory` reach the session the mapper builds. `Map`: `RawText == null` → one warning problem in `MalformedDocumentCode` naming the source key; whitespace keeps today's behaviour. Transcript-format input is unchanged.
  - `docs/dash/field-register.md`: Devin section rows for each new property on a native Devin type and the dropped `chat_message` keys (`thinking`, `num_tokens`, `ttft_ms`, `request_id`, `finish_reason`, `extensions`, `telemetry`) with their reasons; `metrics.total_time_ms` is mapped to the reported duration, not dropped (D18).
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/DevinMessageNodesMapperTests.cs`: new, including the Devin null and whitespace cases (`DevinWarpMapperTests.cs` is not edited here; T5 edits it).
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T9 --red`
  - `kyber-weave review gates . --agent <you> --task T9`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T9 passes, including `KyberDash.Data.Tests.Collector.FieldRegisterTests` and the unchanged `DevinWarpMapperTests`.

### T10: Antigravity `.pb` records an unreadable-payload problem

- **Objective:** Replace the silent empty batch for `antigravity-pb` units and null payloads in `AntigravityTranscriptMapper.Map` with a recorded warning problem (D15).
- **Capabilities:** C# .NET library code with xUnit unit tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `antigravity`
- **Depends on:** none
- **Files:**
  - `dash/server/KyberDash.Data/Harnesses/Antigravity/AntigravityTranscriptMapper.cs`: `Map`, new public constant `PbUnreadableCode = "ANTIGRAVITY_PB_UNREADABLE"`; problem built with the mapper's existing `BuildProblems` shape.
  - `dash/server.tests/KyberDash.Data.Tests/Harnesses/AntigravityPbProblemTests.cs`: new.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T10 --red`
  - `kyber-weave review gates . --agent <you> --task T10`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T10 passes, plus `KyberDash.Data.Tests.Harnesses.AntigravityTranscriptMapperTests`.

### T11: Register the four SQLite readers

- **Objective:** Replace the Kilo, Warp, Devin-sqlite and Cursor-vscdb file specs in `HarnessSourceRegistry` with `SqliteHarnessSourceReader` instances over the four conversation sources, stamp `SqliteRowExportParserContractVersion` (D7), and inject the SQLite seam.
- **Capabilities:** C# .NET library code with xUnit unit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `reader`
- **Depends on:** T2, T3, T4, T5, T7, T8 (the adapter, the reader and the four conversation sources)
- **Files:**
  - `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs`: constructor gains an optional `IHarnessSqliteSource? sqlite = null` (default `MicrosoftDataSqliteHarnessSource.CreateDefault()`); constant `SqliteRowExportParserContractVersion = "4"` beside the base branch's `2` and `3`, with a `<remarks>` saying why (D7); the `kilo-shared-runtime` (`kilo`, `kilo-sqlite-reader`, `kilo-sqlite`) and `warp` (`warp`, `warp-sqlite-reader`, `warp-sqlite`) registrations become `SqliteHarnessSourceReader`s with their current explicit paths; `devin` keeps its `devin-transcript-json` spec and adds a `SqliteHarnessSourceReader` for `.local/share/devin/cli/sessions.db` (`devin`, `devin-reader`, `devin-sqlite`) inside a private composite reader, shaped like `AntigravityHarnessSourceReader`, whose `Read` routes by `unit.Format`; `CursorHarnessSourceReader` delegates its `VscdbFiles` to a `SqliteHarnessSourceReader` (`cursor`, `cursor-vscdb-reader`, `cursor-vscdb`) and keeps the agent-transcript discovery, routing `Read` by format; `IsBinaryFormat` keeps only `-pb`.
  - `dash/server.tests/KyberDash.Data.Tests/Collector/HarnessSourceRegistryTests.cs`: cases per the Test contract, with `InMemoryHarnessSqliteSource` passed to the constructor.
- **Commands:**
  - `kyber-weave review gates . --agent <you> --task T11 --red`
  - `kyber-weave review gates . --agent <you> --task T11`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T11 passes; every caller of `new HarnessSourceRegistry()` compiles unchanged.

### T12: Kilo fixture in its real schema

- **Objective:** Rebuild the Kilo SQLite fixture in `dash/e2e/file-harness-sqlite.ts` in the real `kilo.db` schema and make the `kilo-shared-runtime` spec test pass.
- **Capabilities:** TypeScript Playwright end-to-end tests
- **Difficulty:** easy
- **Technology:** test
- **Session:** `e2e`
- **Depends on:** T11 (the import reads the store)
- **Files:**
  - `dash/e2e/file-harness-dashboard.spec.ts`: first step, before the fixture change: remove `'kilo-shared-runtime'` from `SQLITE_READER_EXPECTED_FAILURES` (F12) and run the evidence command for the RED record; no assertion changes.
  - `dash/e2e/file-harness-sqlite.ts`: the Kilo builder creates `session(id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT, title TEXT, time_created INTEGER, time_updated INTEGER, time_archived INTEGER)`, `message(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL)` and `part(id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT NOT NULL)`; keeps the two root sessions of 10 and 12 turns and their contracts; adds one child session (with `parent_id`) under the first root; every token and tool value is a literal reported in the fixture.
- **Commands:**
  - `npx --prefix dash playwright install --list` (browser precondition; when the isolated config's Chromium is not listed, stop the evidence steps and report `NOT VERIFIED: <reason>`)
  - `dotnet build dash/server/KyberDash.Cli -c Release`
  - `npm --prefix dash run build:web`
  - `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/file-harness-dashboard.spec.ts --grep "^kilo-shared-runtime:"` (evidence RED before the fixture change, GREEN after; record the test names)
  - `kyber-weave review gates . --agent <you> --task T12`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T12 passes; in `file-harness-dashboard.spec.ts` the `kilo-shared-runtime` entry of the `test.fail` set is removed and no assertion is changed.

### T13: Warp fixture in its real schema

- **Objective:** Rebuild the Warp SQLite fixture with `blocks.ai_metadata` and make the `warp` spec test pass.
- **Capabilities:** TypeScript Playwright end-to-end tests
- **Difficulty:** easy
- **Technology:** test
- **Session:** `e2e`
- **Depends on:** T12 (same file)
- **Files:**
  - `dash/e2e/file-harness-dashboard.spec.ts`: first step, before the fixture change: remove `'warp'` from `SQLITE_READER_EXPECTED_FAILURES` (F12) and run the evidence command for the RED record; no assertion changes.
  - `dash/e2e/file-harness-sqlite.ts`: the Warp builder's `blocks` table becomes `blocks(block_id TEXT PRIMARY KEY, start_ts TEXT, stylized_command TEXT, ai_metadata TEXT)` and each command block stores `ai_metadata` `{"conversation_id": <its conversation>}`; `agent_conversations` and `ai_queries` keep their columns.
- **Commands:**
  - `dotnet build dash/server/KyberDash.Cli -c Release`
  - `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/file-harness-dashboard.spec.ts --grep "^warp:"` (evidence RED, then GREEN)
  - `kyber-weave review gates . --agent <you> --task T13`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T13 passes; in `file-harness-dashboard.spec.ts` the `warp` entry of the `test.fail` set is removed and no assertion is changed.

### T14: Cursor fixture with composers and a workspace

- **Objective:** Add `composerData:` rows and one workspace storage directory to the Cursor fixture and make the `cursor` spec test pass.
- **Capabilities:** TypeScript Playwright end-to-end tests
- **Difficulty:** easy
- **Technology:** test
- **Session:** `e2e`
- **Depends on:** T13 (same file), T7 (workspace attribution)
- **Files:**
  - `dash/e2e/file-harness-dashboard.spec.ts`: first step, before the fixture change: remove `'cursor'` from `SQLITE_READER_EXPECTED_FAILURES` (F12) and run the evidence command for the RED record; no assertion changes.
  - `dash/e2e/file-harness-sqlite.ts`: the Cursor builder (see the build steps below).
- **Build steps:**
  - Add one `cursorDiskKV` row per composer: key `composerData:` followed by the composer id; value a JSON object with `composerId` and `createdAt`.
  - Create a workspace storage folder named `workspaceStorage`, beside `globalStorage` in the same Cursor `User` folder, with one fixed hash-named child folder.
  - In that child folder, create a `state.vscdb` with table `ItemTable(key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)` and one row: key `composer.composerData`, value `{"allComposers": [{"composerId": "<first composer id>"}]}`.
  - In the same child folder, write a `workspace.json` whose `folder` is a `file:///` URI of the fixture project path.
- **Commands:**
  - `dotnet build dash/server/KyberDash.Cli -c Release`
  - `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/file-harness-dashboard.spec.ts --grep "^cursor:"` (evidence RED, then GREEN)
  - `kyber-weave review gates . --agent <you> --task T14`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T14 passes; in `file-harness-dashboard.spec.ts` the `cursor` entry of the `test.fail` set is removed and no assertion is changed.

### T15: Devin fixture with sessions and message_nodes

- **Objective:** Rebuild the Devin SQLite fixture in the real `sessions.db` schema with `message_nodes`, make the `devin` spec test pass, and run the whole spec once.
- **Capabilities:** TypeScript Playwright end-to-end tests
- **Difficulty:** easy
- **Technology:** test
- **Session:** `e2e`
- **Depends on:** T14 (same file), T9 (the mapper parses the export)
- **Files:**
  - `dash/e2e/file-harness-sqlite.ts`: `buildDevinSessions` creates `sessions(id TEXT PRIMARY KEY, working_directory TEXT, model TEXT, title TEXT, created_at TEXT, last_activity_at TEXT, hidden INTEGER, metadata TEXT)` and `message_nodes(row_id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, node_id TEXT NOT NULL, parent_node_id TEXT, chat_message TEXT NOT NULL, created_at TEXT, metadata TEXT, UNIQUE(session_id, node_id))`; per turn one `user` node (`content`, `metadata.is_user_input: true`, `metadata.created_at`), one `assistant` node (`metadata.generation_model`, `metadata.created_at`, `metadata.metrics` with `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_creation_tokens`, and `tool_calls` `[{id, name, arguments}]` on tool turns) and one `tool` node per call; one `system` node per session; two sessions of 10 and 12 turns; the contracts updated to the values the fixture reports.
  - `dash/e2e/file-harness-dashboard.spec.ts`: first step, before the fixture change: delete `SQLITE_READER_EXPECTED_FAILURES`, `SQLITE_READER_EXPECTED_FAILURE_REASON` and the `test.fail(…)` call in the per-harness test (F12; `devin` is the last entry), then run the evidence command for the RED record. Beyond that, change a Devin contract value only when the real schema now carries it; no assertion is changed.
- **Commands:**
  - `dotnet build dash/server/KyberDash.Cli -c Release`
  - `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/file-harness-dashboard.spec.ts --grep "^devin:"` (evidence RED, then GREEN)
  - `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/file-harness-dashboard.spec.ts` (the whole spec once)
  - `kyber-weave review gates . --agent <you> --task T15`
- **Acceptance:**
  - Precondition, checked first: `NoEstimatedTokensParserContractVersion` exists in `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs` and `dash/e2e/file-harness-sqlite.ts` exists; otherwise return BLOCKED (the run branch is not on `claude/fix/droid-chart-and-per-harness-e2e`).
  - Test-contract row T15 passes: the four SQLite harness tests and every other harness test in the spec pass; the `test.fail` marker, its set and its reason constant are gone from `file-harness-dashboard.spec.ts`, and no assertion is changed.

## Dependency graph

```text
T1 ─┬─▶ T2 ─┬──────────────┐
    └─▶ T3 ─┼─▶ T4 ────────┤
            ├─▶ T5 ────────┤
            ├─▶ T6 ─▶ T7 ──┼──────────┐
            │          └─▶ T9 ───────────────────────────────▶ T15
            └─▶ T8 ────────┴─▶ T11 ─▶ T12 ─▶ T13 ─▶ T14 ─▶ T15
T10 (independent)
```

T10 starts at once beside T1. T2 and T3 run in parallel after T1; T4, T5, T6 and T8 run in
parallel after both (four workers, the configured cap). T4, T5, T6 and T8 need T2 only for their
`SqliteTests` classes and `SqliteFixtureDatabase`. T9 waits for T7 because both edit
`docs/dash/field-register.md`; it then runs beside T8, T11 and the e2e chain.

## Risks

- **R1 Merge risk on `HarnessSourceRegistry.cs`.** The base branch edits its parser contract versions and
  `SourceRootSpec`; T11 edits the same constructor. Mitigation: D1 stacks this run on that branch, so
  T11 edits its version of the file; version `4` follows its `2` and `3`.
- **R2 Base branch moves.** The base may gain commits after D1. Mitigation: the conductor rebases the run
  branch only between tasks, never mid-task; T11 and T12–T15 re-read the base's registry and fixtures.
- **R7 Base not in place.** If the run branch is not moved onto the base (D1), T1 onward would build
  against `main`. Mitigation: every task's first acceptance line checks the base and returns BLOCKED.
- **R3 Microsoft.Data.Sqlite URI support.** If `file:…?immutable=1` is refused, the policy's copy path
  covers it (D4).
- **R4 Large Cursor composers.** A composer over the 64 MiB export cap is refused with a problem, not
  imported. Mitigation: the problem names the composer, and D11's cap is one constant.
- **R5 Copies of large stores.** A locked 3 GB Kilo store is copied once beside `canon.db`. Mitigation:
  the copy cap, fingerprint naming and superseded-copy removal (D4).
- **R6 Orphaned whole-file units.** Checkpoints and empty source rows keyed by the bare database path
  (the old unit) are no longer discovered. They carry no records.

## Out-of-scope boundaries

- No canon schema change; no new C# test project (D5).
- PR note: `dash/server/KyberDash.Data/Harnesses/Devin/DevinMapper.cs` — Devin ACU cost exists only per session (`sessions.metadata.total_acu_cost`) and the canon store has no session-level reported cost, so Devin cost stays unreported — carrying it needs a schema decision from the owner.
- PR note: `dash/server/KyberDash.Data/Harnesses/Antigravity/AntigravityTranscriptMapper.cs` — `.pb` conversations are recorded as unreadable, not decoded; the repository has no schema for them — decoding needs an evidenced format.
- PR note: Warp on macOS — the group container needs the importing process to have the macOS privacy permission (F6); without it Warp records `SQLITE_SOURCE_UNREADABLE` — only the owner can grant it.
- PR note: `dash/server/KyberDash.Data/Harnesses/Cursor/CursorMapper.cs` — 16 of 2,000 sampled Cursor bubbles carry a model name; the rest import with the mapper's fallback label, and the composer-level `modelConfig.modelName` is not used (D9) — whether a composer's configured model counts as reported is the owner's call.
- PR note: `dash/server/KyberDash.Data/Harnesses/Devin/DevinMapper.cs` and `dash/server/KyberDash.Data/Harnesses/Cursor/CursorMapper.cs` — Devin falls back to the session-level `sessions.model` when an assistant node has no `metadata.generation_model` (T9), while Cursor refuses a composer-level model fill (D9); both values are ones the harness reported, but the two rules differ — the owner to confirm the difference is intended.
- PR note: the D4 lock path (`SQLITE_BUSY` from a live writer) is proven by policy unit tests only; T2's real-file tests cover the read-only-directory and copy paths but hold no live writer lock — a check against a store a running harness is writing is the remaining proof.
- PR note: `docs/adr/0090-kyberdash-relational-store.md` decision 11 says no test opens a SQLite file, while `SqliteRelationalStoreParityTests` and this plan's `*SqliteTests` do (D5) — the ADR needs the owner to restate or supersede that line.
- PR note: `docs/reference/seams.md` — no KyberDash collector seam is registered (F10); registering them all is a separate documentation change.
- PR note: the old whole-database source rows and checkpoints (R6) stay until a clean; they hold no records.

## Verification gates

- Each task's own `kyber-weave review gates` run (RED then GREEN), per the Test contract.
- The `*SqliteTests` classes (T2, T4, T5, T6, T8) prove the adapter and each harness's SQL against real
  files in the task gates; T12–T15's Playwright runs prove the whole import end to end, and T15 runs the
  whole spec once.
- `kyber-weave docs validate .` and `kyber-weave docs drift .` after docs-dev closeout.

## Review

The end-of-run council reviews the whole change under the `code-review` skill, measured against the
run's base (the stacked branch, D1).

## docs-dev closeout

- Harvest per the Design harvest table into `docs/dash/architecture.md`, `docs/dash/field-register.md`,
  `docs/dash/runbook.md` and `docs/dash/telemetry-inventory.md`.
- Fix the stale SQLite rules file under `docs/standards/sqlite/` so it matches the standard body
  (`Microsoft.Data.Sqlite.Core` over the system SQLite), D2.
- No ADR: ADR waiver — the reader refines the existing collector architecture and reuses the deleted
  TypeScript reader's design.
- Run `kyber-weave docs standards migrate` for the standards warning period, committed with the closeout.
