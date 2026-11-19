---
id: specs/kyberdash-relational-store/tasks
title: KyberDash relational store tasks
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
component: KyberDash
---

# Implementation Plan

> **Not approved for execution.** This task list was written under an autonomous approval for
> specification drafting only (AP-D3). No task may start until the owner approves execution. Three
> blockers are open and are listed first, so that no reader misses them.

## Open blockers

| Blocker | What it is | What it blocks | What it does not block | Resolved by |
|---|---|---|---|---|
| **BL-1** | OD-7 deletes the database and says every harness's history is re-ingested. History that reached the store only over OTLP has no harness file, so it cannot be re-ingested (requirements, BL-1; design, "Blocker BL-1") | Gate 0.1, and through it task 16.2 (the deleting schema-version bump), closeout 18.1 and every destructive rollout | Every other task: they run against in-memory fakes and delete nothing | An owner decision recorded in the decisions table. The options are listed there; none is chosen here |
| **BL-2** | Which 17 harness ids the owner's store holds is unproven; the code gives candidates only | Gate 0.2, and through it task 16.2, closeout 18.1 and every destructive rollout | Development: tasks 1.1 to 16.4 except 10.11, 10.12 and 16.2. Tasks 6.1 to 10.10 cover every candidate | One proof route from design, "Closing the 17-id gap", accepted by the owner. The owner's own list is optional; no agent, test or code reads the store for it |
| **BL-3** | `windsurf` and `aider` have no evidenced history format | Gate 0.3, and through it tasks 10.11, 10.12 and 16.2, closeout 18.1 and every destructive rollout | Their discovery entries, which report `SOURCE_FORMAT_UNEVIDENCED` (task 5.1) | Public format evidence accepted by the owner, or the owner's authorisation to exclude. The specification grants neither |

The blockers are **not work an agent can do**. Each is resolved only by the owner, recorded in the
decisions table of [requirements](requirements.md#decisions) with the status `RESOLVED BY OWNER`.
So that the task graph enforces them rather than prose, each blocker has one owner-only gate task in
group 0 (0.1 for BL-1, 0.2 for BL-2, 0.3 for BL-3). Every task a blocker holds back names its gate
on its `Depends on:` line, so the task file never puts it in the ready queue before the gate is
done. A gate task's command passes only once the owner's resolution is recorded. A worker given a
gate task must not write, choose or infer a resolution: it runs the command, and if the command
fails it reports `BLOCKED` and leaves the gate open.

**Limit of this enforcement.** The dependencies are machine-enforced. That only the owner closes a
gate is not: the task-file format has no owner-only field, and a role that names no shipped agent
falls back to a developer tier at dispatch (`TaskPromptService.cs`, lines 90–91). The gate is held
by the gate's own acceptance criteria, by its command, and by review, which must reject any change
that records `RESOLVED BY OWNER` without the owner's own instruction.

**No destructive rollout until BL-1, BL-2 and BL-3 are all resolved.** "Destructive rollout" means
merging to a release, publishing, or running on any machine a build that contains task 16.2's
deletion, or any other build that deletes or replaces an existing `canon.db`. It stays prohibited
until the owner has recorded resolutions of all three blockers. Finishing every task does not lift
this prohibition, and neither does this task list.

Nothing in this plan deletes, moves or reads the owner's database. Task 16.2 writes the deleting
code and its tests against a fake file system, and even that code must not be written until BL-1
is resolved.

## How to read this plan

Every task is test-first. Write the failing unit test named in the task, see it fail, then write
the code. Tests run over in-memory fakes of the store interfaces. No test opens, reads or writes a
SQLite file, and no test reads private session data (OD-9, Requirement 10). Fixtures are synthetic
and use only the event types and fields the design's per-harness support matrix cites (Requirement
11.7).

Each task names its files, the abilities it needs, the command that proves it, and its acceptance
criteria. A task is done only when its command passes and the gates for the paths it touched pass:
`dotnet test` for `dash/server.tests`, the `dash` npm scripts for `dash/web`, and `docs validate`
plus `docs drift` for `docs/`. The commands are listed in `AGENTS.md`.

`InMemoryRelationalStore` lives in the Application test project and is linked as a compiled file
into the Data test project, following the existing per-project `Fakes.cs` pattern.

Every mapper task also updates that harness's section of `docs/dash/field-register.md` in the same
change (Requirement 6.6). Task 15.1 creates the register and the test that enforces it, so mapper
tasks depend on it.

## Tasks

### 0. Owner resolution gates

These tasks hold work back until the owner has decided. They write no code. A worker must not
author their resolution; if the command fails, the task is reported `BLOCKED` and stays open.

- [ ] 0.1 Gate: the owner has resolved BL-1
  Difficulty: easy
  Role: owner
  Depends on: none
  Capabilities: owner decision verification only; no authoring, no code, no delivery
  Files: `docs/specs/kyberdash-relational-store/requirements.md`
  Command: `grep -Eq "^[|] BL-1 [|].*[|] RESOLVED BY OWNER" docs/specs/kyberdash-relational-store/requirements.md`
  Acceptance criteria: the BL-1 row of the decisions table records the owner's chosen option and the status `RESOLVED BY OWNER`, written on the owner's own instruction; the command passes. If it fails, report `BLOCKED` and change nothing.
  - No agent, specification or conductor chooses among the BL-1 options, and none records loss
    as accepted on the owner's behalf.
  - _Requirements: 7.1, 7.6_
- [ ] 0.2 Gate: the owner has resolved BL-2
  Difficulty: easy
  Role: owner
  Depends on: none
  Capabilities: owner decision verification only; no authoring, no code, no delivery
  Files: `docs/specs/kyberdash-relational-store/requirements.md`
  Command: `grep -Eq "^[|] BL-2 [|].*[|] RESOLVED BY OWNER" docs/specs/kyberdash-relational-store/requirements.md`
  Acceptance criteria: the BL-2 row records the proof route that named the harness ids and the status `RESOLVED BY OWNER`; the command passes. If it fails, report `BLOCKED` and change nothing.
  - No agent, test or code reads the owner's store to resolve it.
  - _Requirements: 6.5, 11.1_
- [ ] 0.3 Gate: the owner has resolved BL-3
  Difficulty: easy
  Role: owner
  Depends on: none
  Capabilities: owner decision verification only; no authoring, no code, no delivery
  Files: `docs/specs/kyberdash-relational-store/requirements.md`
  Command: `grep -Eq "^[|] BL-3 [|].*[|] RESOLVED BY OWNER" docs/specs/kyberdash-relational-store/requirements.md`
  Acceptance criteria: the BL-3 row records, for both Windsurf and Aider, either a cited public format document or the owner's authorisation to exclude, and the status `RESOLVED BY OWNER`; the command passes. If it fails, report `BLOCKED` and change nothing.
  - Evidence an agent finds is offered to the owner; it does not resolve the gate by itself.
  - _Requirements: 6.2, 8.1_

### 1. Schema catalog

- [ ] 1.1 Define the record tables in `RelationalSchemaCatalog` and generate their DDL
  Difficulty: hard
  Technology: csharp
  Depends on: none
  Capabilities: C# domain modelling and SQLite DDL generation, with xUnit tests
  Files: `dash/server/KyberDash.Domain/Schema/RelationalSchemaCatalog.cs`, `dash/server/KyberDash.Domain/Schema/SchemaDdlGenerator.cs`, `dash/server.tests/KyberDash.Domain.Tests/Schema/RelationalSchemaCatalogTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Domain.Tests --filter FullyQualifiedName~RelationalSchemaCatalog`
  Acceptance criteria: the catalog holds every record, session and turn table in the design's "Record tables", and the structure tests pass over the catalog and the generated DDL text only.
  - The catalog holds keys, foreign keys and positions for every table.
  - Tests first: every column is `NOT NULL` and typed `INTEGER`, `REAL` or `TEXT`. No `BLOB`, no
    `_json` suffix, no `payload` or `raw`. Every foreign key has `ON DELETE CASCADE`. Every
    repeated-item table has a typed `position`. No table has an entity-attribute-value or
    attribute-bag shape (a table whose columns are a name and a value).
  - _Requirements: 2.1, 2.2, 2.5, 3.1, 3.2, 3.3, 3.5, 3.7, 6.3_
- [ ] 1.2 Add the projection, operational and catalog tables and the replacement map
  Difficulty: medium
  Technology: csharp
  Depends on: 1.1
  Capabilities: C# domain modelling and relational normalisation, with xUnit tests
  Files: `dash/server/KyberDash.Domain/Schema/RelationalSchemaCatalog.cs`, `dash/server/KyberDash.Domain/Schema/LegacyTableReplacementMap.cs`, `dash/server.tests/KyberDash.Domain.Tests/Schema/LegacyTableReplacementMapTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Domain.Tests --filter FullyQualifiedName~Schema`
  Acceptance criteria: every one of today's 22 tables maps to replacement tables or to "removed" with a reason, and every projection detector's fields live in a child table for that detector.
  - Add every table in the design's "Projection, operational and catalog tables", with DD-13's
    rule (analysis outputs only) and DD-14's exclusive reason tables.
  - _Requirements: 3.4, 4.1, 4.2, 4.3, 4.4, 4.5_

### 2. Collector DTOs and the JSON text guard

- [ ] 2.1 Define the collector DTOs
  Difficulty: medium
  Technology: csharp
  Depends on: 1.1
  Capabilities: C# record types and reflection-based structure tests, with xUnit
  Files: `dash/server/KyberDash.Data/Collector/CollectedTypes.cs`, `dash/server.tests/KyberDash.Data.Tests/Collector/CollectedTypesTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CollectedTypes`
  Acceptance criteria: a reflection test fails on any DTO property typed `JsonElement`, `JsonNode`, `object` or a dictionary, and every DTO property maps to a catalog column.
  - Add `CollectedBatch`, `CollectedSession`, `CollectedRecord`, `CollectedToolCall`,
    `CollectedToolContent`, `CollectedContentPart`, `CollectedWithheldText`,
    `CollectedToolDefinition`, `CollectedRecordSignals`, `CollectedAgentInvocation`,
    `CollectedModelUsage` and `CollectedWorkspace`.
  - _Requirements: 1.1, 1.2, 1.3, 1.4_
- [ ] 2.2 Add `JsonDocumentText`, `ToolContentMeasurer` and the defined-field list
  Difficulty: medium
  Technology: csharp
  Depends on: 2.1
  Capabilities: C# text classification, SHA-256 fingerprinting and token measurement, with xUnit tests
  Files: `dash/server/KyberDash.Data/Collector/JsonDocumentText.cs`, `dash/server/KyberDash.Data/Collector/ToolContentMeasurer.cs`, `dash/server/KyberDash.Data/Collector/DefinedFieldList.cs`, `dash/server.tests/KyberDash.Data.Tests/Collector/ToolContentMeasurerTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~ToolContentMeasurer`
  Acceptance criteria: a JSON document keeps only defined-list fields, sizes, measured tokens and a fingerprint with no text row; prose, including prose that quotes JSON, is kept unchanged; a defined-list entry with no catalog column fails a test.
  - Cases: an object, an array, a nested JSON string, a JSON scalar, prose quoting a JSON
    fragment, an empty value and an oversized plain result.
  - _Requirements: 2.3, 2.4, 2.7, 6.1, 6.4_

### 3. Store interfaces and the in-memory fake

- [ ] 3.1 Declare the store interfaces
  Difficulty: easy
  Technology: csharp
  Depends on: 2.1
  Capabilities: C# interface design, with xUnit reflection tests
  Files: `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreWriter.cs`, `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`, `dash/server/KyberDash.Contracts/Repositories/IProjectionStore.cs`, `dash/server/KyberDash.Contracts/Repositories/IOperationalStore.cs`, `dash/server.tests/KyberDash.Domain.Tests/Contracts/StoreInterfaceShapeTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Domain.Tests --filter FullyQualifiedName~StoreInterfaceShape`
  Acceptance criteria: the four interfaces take and return only collector DTOs and typed read models, and a test fails if any member exposes a JSON or dictionary type.
  - _Requirements: 1.1, 5.1, 10.1_
- [ ] 3.2 Build `InMemoryRelationalStore`
  Difficulty: hard
  Technology: csharp
  Depends on: 1.2, 2.2, 3.1
  Capabilities: C# in-memory fakes that enforce relational constraints from a catalog, with xUnit tests
  Files: `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`, `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStoreTests.cs`, `dash/server.tests/KyberDash.Data.Tests/KyberDash.Data.Tests.csproj`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~InMemoryRelationalStore`
  Acceptance criteria: the fake implements all four interfaces; it rejects a duplicate key, an orphan child and a missing column; a delete cascades; it refuses a JSON document in any `TEXT` column with `STORE_JSON_TEXT_REFUSED`; an unreported value leaves no child row.
  - _Requirements: 2.6, 3.1, 3.2, 3.5, 10.1, 10.5_

### 4. SQLite adapter

- [ ] 4.1 Write `SqliteRelationalStore` for writes and schema creation
  Difficulty: hard
  Technology: csharp
  Depends on: 3.2
  Capabilities: C# SQLite adapter over the `sqlite3` process seam, with xUnit seam tests
  Files: `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.cs`, `dash/server.tests/KyberDash.Data.Tests/Repositories/SqliteRelationalStoreWriteTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~SqliteRelationalStoreWrite`
  Acceptance criteria: the adapter applies the generated DDL and writes one transaction per source unit; it refuses JSON text with the fake's error code; a test fails if any statement serializes or deserializes JSON; no database file is opened.
  - Test through a subclass that overrides `ExecuteSqliteCore`, records statements and returns
    canned tab-separated hex output (DD-11).
  - _Requirements: 2.3, 2.6, 6.4, 10.1, 10.4_
- [ ] 4.2 Implement the reader queries
  Difficulty: hard
  Technology: csharp
  Depends on: 4.1
  Capabilities: C# SQL aggregate queries and typed row parsing, with xUnit tests over fakes and seams
  Files: `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`, `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`, `dash/server.tests/KyberDash.Data.Tests/Repositories/SqliteRelationalStoreReadTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~SqliteRelationalStoreRead`
  Acceptance criteria: token totals by class, counts, model lists, tools offered and invoked, request lists, reconciliation and per-tool counts are computed at read time on both the adapter and the fake, with matching values (DD-13).
  - _Requirements: 3.4, 3.7, 5.1, 10.4_

### 5. Discovery and the record writer

- [ ] 5.1 Replace `DiscoverLocalSources` with a reader registry
  Difficulty: medium
  Technology: csharp
  Depends on: 2.1
  Capabilities: C# source discovery over a fake file system, with xUnit tests
  Files: `dash/server/KyberDash.Data/Collector/IHarnessSourceReader.cs`, `dash/server/KyberDash.Data/Collector/IHarnessMapper.cs`, `dash/server/KyberDash.Data/Collector/HarnessSourceRegistry.cs`, `dash/server/KyberDash.Data/Repositories/TranscriptReaderRepository.cs`, `dash/server.tests/KyberDash.Data.Tests/Collector/HarnessSourceRegistryTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~HarnessSourceRegistry`
  Acceptance criteria: the registry has one entry per candidate harness in the support matrix; Claude, Codex and Copilot CLI sources are found; a harness whose reader is not built, or whose format is unevidenced (BL-3), produces an `ingest_problem` with `SOURCE_FORMAT_UNEVIDENCED`, never a silent zero.
  - _Requirements: 1.1, 1.5, 8.1_
- [ ] 5.2 Write mapped batches through the writer
  Difficulty: medium
  Technology: csharp
  Depends on: 3.2, 5.1
  Capabilities: C# ingestion services over store interfaces, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/Services/TranscriptIngestionService.cs`, `dash/server/KyberDash.Application/CanonIngestPipeline.cs`, `dash/server.tests/KyberDash.Application.Tests/Ingest/RelationalIngestTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~RelationalIngest`
  Acceptance criteria: mapper output reaches the store only through `IRelationalStoreWriter`; a malformed unit writes `SOURCE_UNREADABLE` and the others continue; an unknown event type is counted, with no payload kept.
  - _Requirements: 1.1, 6.4, 6.5_

### 6. File harnesses with C# readers today

- [ ] 6.1 Map Claude Code files
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# JSONL parsing into typed DTOs, with xUnit tests over synthetic fixtures
  Files: `dash/server/KyberDash.Data/Harnesses/Claude/ClaudeTranscriptMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/ClaudeTranscriptMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~ClaudeTranscriptMapper`
  Acceptance criteria: the tests assert every DTO property filled and every absent child row for fixtures with all token classes, none, `tool_use` and `tool_result` pairs, a JSON-document tool result, a nested JSON string, prose with a JSON fragment, and numbers and booleans where text is expected.
  - Port the mapping from `CanonSynth` to `IHarnessMapper`.
  - _Requirements: 1.2, 2.4, 2.7, 6.1, 6.2, 11.1, 11.4, 11.6, 11.7_
- [ ] 6.2 Map Codex files, with a tool fixture
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# JSONL parsing into typed DTOs, with xUnit tests over synthetic fixtures
  Files: `dash/server/KyberDash.Data/Harnesses/Codex/CodexRolloutMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CodexRolloutMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CodexRolloutMapper`
  Acceptance criteria: `function_call` and `custom_tool_call`, paired with their outputs by call id (including the `id` fallback and `arguments` versus `input`), become `tool.invoke` records whose parent is the model call; JSON arguments keep only measurements.
  - _Requirements: 1.2, 2.4, 11.1, 11.3, 11.6, 11.7_
- [ ] 6.3 Map Codex `local_shell_call`
  Difficulty: medium
  Technology: csharp
  Depends on: 6.2
  Capabilities: C# JSONL parsing into typed DTOs from a public API reference, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Codex/CodexRolloutMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CodexLocalShellTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CodexLocalShell`
  Acceptance criteria: `completed` gives a success outcome row, `incomplete` a failure row and `in_progress` no row; `action` is kept as size and fingerprint only; plain `output` is kept as result text; the field register cites OpenAI's "Local shell" reference.
  - First step: confirm the item shape against that reference and map only the fields it
    documents (DD-17a).
  - _Requirements: 2.4, 2.7, 6.1, 6.2, 11.7_

### 7. Copilot CLI journal

- [ ] 7.1 Read `events.jsonl` and map model calls
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# JSONL event-journal parsing into typed DTOs, with xUnit tests over synthetic fixtures
  Files: `dash/server/KyberDash.Data/Harnesses/CopilotCli/CopilotCliJournalReader.cs`, `dash/server/KyberDash.Data/Harnesses/CopilotCli/CopilotCliMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CopilotCliMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CopilotCliMapper`
  Acceptance criteria: every `assistant.message` becomes an `llm.invoke` record, with or without `outputTokens`; an output token row exists only where reported and no input row exists; `pending-session:*` is skipped.
  - Read `~/.copilot/session-state/<id>/events.jsonl` using only the event types and fields in
    the design's Copilot CLI mapping table.
  - Fixtures also cover reasoning text, `user.message`, `system.message`, model change and
    context change.
  - _Requirements: 8.1, 8.2, 8.5, 11.1, 11.6_
- [ ] 7.2 Map Copilot CLI tool calls
  Difficulty: medium
  Technology: csharp
  Depends on: 7.1
  Capabilities: C# event correlation by call id into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/CopilotCli/CopilotCliMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CopilotCliToolTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CopilotCliTool`
  Acceptance criteria: each `toolRequests[]` entry, joined to `tool.execution_start` and `tool.execution_complete`, is a `tool.invoke` record whose parent is the requesting message; a JSON-document result stores only `tool_call_result_size`; a tool with no completion has no end or outcome rows.
  - Fixtures: object `arguments`, a JSON-document result, a plain result, a failed tool with an
    error code, an MCP tool, and a `skill` request with `skill.invoked`.
  - _Requirements: 2.4, 2.7, 8.3, 8.5, 11.3, 11.7_
- [ ] 7.3 Map Copilot CLI sub-agents
  Difficulty: medium
  Technology: csharp
  Depends on: 7.2
  Capabilities: C# event correlation into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/CopilotCli/CopilotCliMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CopilotCliSubagentTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CopilotCliSubagent`
  Acceptance criteria: `subagent.started`, `subagent.completed` and `subagent.failed` give `agent.invoke` records whose parent is the spawning tool call; sub-agent records are linked through `record_agent_instance` only when a documented-shape fixture proves the `agentId` join (DD-8), and otherwise have no link.
  - _Requirements: 8.4, 8.5, 11.3_
- [ ] 7.4 Map Copilot CLI session usage and workspace
  Difficulty: easy
  Technology: csharp
  Depends on: 7.1
  Capabilities: C# mapping of nested usage objects to typed rows, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/CopilotCli/CopilotCliMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CopilotCliSessionUsageTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CopilotCliSessionUsage`
  Acceptance criteria: two shutdown snapshots give two `session_model_usage` sets in disjoint token classes; a session with no shutdown has none; the context change gives workspace, repository and branch rows.
  - _Requirements: 3.1, 3.2, 8.5_

### 8. OTLP span mappers

- [ ] 8.1 Add `OtlpSpanNative` and map Claude spans
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# OTLP span attribute mapping into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Otlp/OtlpSpanNative.cs`, `dash/server/KyberDash.Data/Harnesses/Otlp/ClaudeSpanMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/ClaudeSpanMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~ClaudeSpanMapper`
  Acceptance criteria: model, tool and `invoke_agent` spans with `client` and `internal` kinds give the matching ops and kinds, with every DTO property asserted.
  - `OtlpSpanNative` is built from the `CanonAdapters.cs` key lists; fixtures use the existing
    span builders.
  - _Requirements: 1.2, 11.1, 11.3, 11.6_
- [ ] 8.2 Map Codex spans
  Difficulty: easy
  Technology: csharp
  Depends on: 8.1
  Capabilities: C# OTLP span attribute mapping, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Otlp/CodexSpanMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CodexSpanMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CodexSpanMapper`
  Acceptance criteria: only the allowlisted Codex keys are mapped, and every other attribute is absent from the DTOs.
  - _Requirements: 1.2, 11.6_
- [ ] 8.3 Map Copilot spans
  Difficulty: easy
  Technology: csharp
  Depends on: 8.1
  Capabilities: C# OTLP span attribute mapping, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Otlp/CopilotSpanMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CopilotSpanMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CopilotSpanMapper`
  Acceptance criteria: the `copilot` adapter's keys are mapped, including `gen_ai.tool.name` and `invoke_agent` where present; no fixture asserts Copilot Chat `agent.invoke` beyond what the adapter evidences.
  - _Requirements: 1.2, 11.6, 11.7_
- [ ] 8.4 Map Antigravity and OpenCode spans
  Difficulty: medium
  Technology: csharp
  Depends on: 8.1
  Capabilities: C# OTLP span attribute mapping, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Otlp/AntigravitySpanMapper.cs`, `dash/server/KyberDash.Data/Harnesses/Otlp/OpenCodeSpanMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/AntigravityOpenCodeSpanMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~AntigravityOpenCodeSpanMapper`
  Acceptance criteria: Antigravity adapter keys are mapped; OpenCode `step`, `tool` and `request` spans give `llm.invoke`, `tool.invoke` and `agent.invoke`.
  - _Requirements: 1.2, 11.3, 11.6_
- [ ] 8.5 Map Droid and Pi spans, and keep Gemini excluded
  Difficulty: medium
  Technology: csharp
  Depends on: 8.1
  Capabilities: C# OTLP span attribute mapping, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Otlp/DroidSpanMapper.cs`, `dash/server/KyberDash.Data/Harnesses/Otlp/PiSpanMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/DroidPiSpanMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~DroidPiSpanMapper`
  Acceptance criteria: Droid `factory.turn`, `factory.tool` and `factory.session` and Pi's adapter keys are mapped; a Gemini-attributed span produces no record.
  - _Requirements: 1.2, 11.3, 11.6_

### 9. OTLP logs and Codex tool events

- [ ] 9.1 Map OTLP logs on receipt and remove the log tables
  Difficulty: medium
  Technology: csharp
  Depends on: 1.2, 5.2
  Capabilities: C# OTLP log handling in a collector service, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/Services/OtlpCollectorService.cs`, `dash/server/KyberDash.Data/Harnesses/Otlp/OtlpLogMapper.cs`, `dash/server.tests/KyberDash.Application.Tests/Otlp/OtlpLogMapperTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~OtlpLogMapper`
  Acceptance criteria: no log is written to `pending_logs`, `quarantined_logs` or `enriched_logs`; a log the mapper cannot map is counted in `ingest_problem` with no payload kept (DD-10).
  - _Requirements: 4.5, 6.4_
- [ ] 9.2 Map Codex `codex.tool_result` to `tool.invoke`
  Difficulty: medium
  Technology: csharp
  Depends on: 8.2, 9.1
  Capabilities: C# OTLP log-event mapping from a vendor telemetry document, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Otlp/CodexToolResultMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CodexToolResultMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CodexToolResultMapper`
  Acceptance criteria: tool name, call id, success and duration map to a `tool.invoke` record, and arguments and the output excerpt are dropped with reasons in the register; the register cites the `openai/codex` repository's own telemetry document. If that document does not describe the event, the task stops and reports, with no mapper merged.
  - First step: confirm the event and attribute names against that document. An attribute it
    does not name is not mapped. Secondary sources are not enough (DD-17b).
  - _Requirements: 2.4, 6.1, 6.2, 11.3, 11.7_

### 10. Harnesses with no C# reader (DD-16)

Each port maps only the fields its deleted TypeScript provider read, from
`fc80ddf9^:dash/src/providers/*`, with fixtures that mirror the shapes that provider parsed.

- [ ] 10.1 Port the Copilot VS Code journal reader
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of a TypeScript file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/CopilotVscode/CopilotVscodeMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CopilotVscodeMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CopilotVscodeMapper`
  Acceptance criteria: fixtures mirroring `copilot-vscode.ts`'s parsed shapes give the records that provider produced, and every field it read has a register row.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.2 Port the Antigravity transcript reader
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of a TypeScript file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Antigravity/AntigravityTranscriptMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/AntigravityTranscriptMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~AntigravityTranscriptMapper`
  Acceptance criteria: transcript fixtures give `llm.invoke` records, and `PLANNER_RESPONSE.tool_calls` give `tool.invoke` records.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.3 Port the Droid session reader
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of a TypeScript file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Droid/DroidSessionMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/DroidSessionMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~DroidSessionMapper`
  Acceptance criteria: fixtures mirroring the `~/.factory/sessions` shapes give the records the deleted provider produced.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.4 Port the Pi reader
  Difficulty: easy
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of a TypeScript file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Pi/PiMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/PiMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~PiMapper`
  Acceptance criteria: fixtures mirroring `pi.ts`'s parsed shapes give the records that provider produced.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.5 Port the Cursor reader
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of a TypeScript file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Cursor/CursorMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CursorMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CursorMapper`
  Acceptance criteria: fixtures give usage-bearing `llm.invoke` records only, as the deleted provider produced.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.6 Port the Kilo readers
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of TypeScript file readers into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Kilo/KiloMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/KiloMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~KiloMapper`
  Acceptance criteria: `kilo-shared-runtime` and `kilo-vscode-legacy` fixtures each give the records their deleted provider produced, under their own harness ids.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.7 Port the CommandCode reader
  Difficulty: easy
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of a TypeScript file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/CommandCode/CommandCodeMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/CommandCodeMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~CommandCodeMapper`
  Acceptance criteria: fixtures give `llm.invoke` records, and `tool_use` items give `tool.invoke` records.
  - _Requirements: 1.2, 6.2, 11.1, 11.2, 11.3_
- [ ] 10.8 Port the Devin and Warp readers
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of TypeScript file readers into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Devin/DevinMapper.cs`, `dash/server/KyberDash.Data/Harnesses/Warp/WarpMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/DevinWarpMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~DevinWarpMapper`
  Acceptance criteria: each harness's fixtures give the records its deleted provider produced.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.9 Port the Kimi Code and Roo Code readers
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of TypeScript file readers into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/KimiCode/KimiCodeMapper.cs`, `dash/server/KyberDash.Data/Harnesses/RooCode/RooCodeMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/KimiRooMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~KimiRooMapper`
  Acceptance criteria: each harness's fixtures give the records its deleted provider produced.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.10 Port the Cline readers
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 15.1
  Capabilities: C# port of TypeScript file readers into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Cline/ClineMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/ClineMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~ClineMapper`
  Acceptance criteria: `cline` and `cline-cli` fixtures each give the records their deleted provider produced, under their own harness ids.
  - _Requirements: 1.2, 6.2, 11.1, 11.2_
- [ ] 10.11 Establish a Windsurf source
  Difficulty: hard
  Technology: csharp
  Depends on: 5.1, 0.3
  Capabilities: public-format research, then a C# file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Windsurf/WindsurfMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/WindsurfMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~WindsurfMapper`
  Acceptance criteria: either a public format document is cited and the mapper passes fixtures built from it, or the task stops with `SOURCE_FORMAT_UNEVIDENCED` still reported and the gap reported to the owner, with no reader invented and the harness not dropped.
  - First step: look for a public format document for Windsurf's local history. Read no private
    file.
  - _Requirements: 6.2, 8.1, 11.2_
- [ ] 10.12 Establish an Aider source
  Difficulty: hard
  Technology: csharp
  Depends on: 5.1, 0.3
  Capabilities: public-format research, then a C# file reader into typed DTOs, with xUnit tests
  Files: `dash/server/KyberDash.Data/Harnesses/Aider/AiderMapper.cs`, `dash/server.tests/KyberDash.Data.Tests/Harnesses/AiderMapperTests.cs`, `docs/dash/field-register.md`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~AiderMapper`
  Acceptance criteria: the same as 10.11, for Aider.
  - _Requirements: 6.2, 8.1, 11.2_

### 11. Projections

- [ ] 11.1 Rewrite the session projector over the reader
  Difficulty: hard
  Technology: csharp
  Depends on: 1.2, 4.2
  Capabilities: C# analysis engine refactoring over store interfaces, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/CanonSessionProjectorEngine.cs`, `dash/server.tests/KyberDash.Application.Tests/Projection/SessionProjectionTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~SessionProjection`
  Acceptance criteria: the projector reads only `IRelationalStoreReader` and writes typed rows to `IProjectionStore` for context pressure per turn, schema ranking, timeline sub-agent and auxiliary marks, and exclusive unmeasurable-reason rows; schema cost comes from `tool_definition.schema_tokens`.
  - _Requirements: 3.4, 3.7, 4.3_
- [ ] 11.2 Project runs and executions
  Difficulty: medium
  Technology: csharp
  Depends on: 11.1
  Capabilities: C# projection logic into typed tables, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/CanonSessionProjectorEngine.cs`, `dash/server.tests/KyberDash.Application.Tests/Projection/RunExecutionProjectionTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~RunExecutionProjection`
  Acceptance criteria: run grouping and execution linkage are typed rows, and no `parent_linkage_json` or equivalent exists.
  - _Requirements: 4.2, 4.3_
- [ ] 11.3 Project harness rollups
  Difficulty: medium
  Technology: csharp
  Depends on: 11.1
  Capabilities: C# aggregation engine over store interfaces, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/CanonRollupsEngine.cs`, `dash/server.tests/KyberDash.Application.Tests/Projection/RollupProjectionTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~RollupProjection`
  Acceptance criteria: rollups are typed metric rows, and a harness with no records has no metric rows rather than zeros.
  - _Requirements: 3.1, 4.3_
- [ ] 11.4 Project findings and predictions
  Difficulty: medium
  Technology: csharp
  Depends on: 11.1
  Capabilities: C# detector output into typed child tables, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/KyberBridgeEngine.cs`, `dash/server.tests/KyberDash.Application.Tests/Projection/FindingPredictionProjectionTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~FindingPredictionProjection`
  Acceptance criteria: findings have per-detector child tables and typed evidence links; predictions have comparison, observed-delta and calibration child rows only where set.
  - _Requirements: 3.3, 4.3_

### 12. Operational tables

- [ ] 12.1 Replace `metadata` and the operational tables
  Difficulty: medium
  Technology: csharp
  Depends on: 3.2
  Capabilities: C# typed operational state over store interfaces, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Operational.cs`, `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`, `dash/server.tests/KyberDash.Data.Tests/Repositories/OperationalStoreTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~OperationalStore`
  Acceptance criteria: settings, schema version, write marker, projection state and clean stamp are typed; `refresh_run`, `source_checkpoint`, `ingest_problem`, `quarantine` (no raw span), `token_cache`, the model context-window catalog and `metric_point` are typed tables with no key-value bag.
  - _Requirements: 4.4, 4.5_

### 13. Analytics

- [ ] 13.1 Put one error boundary around every analytics operation
  Difficulty: easy
  Technology: csharp
  Depends on: none
  Capabilities: C# HTTP error handling in an engine, with xUnit tests using a throwing fake
  Files: `dash/server/KyberDash.Application/KyberBridgeEngine.cs`, `dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsErrorEnvelopeTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~AnalyticsErrorEnvelope`
  Acceptance criteria: with the real `KyberBridgeEngine`, a store failure answers `ANALYTICS_STORE_UNAVAILABLE`, a bad argument `ANALYTICS_INVALID_ARGUMENT` and anything else `ANALYTICS_INTERNAL`, each in a non-empty contract-v1 error envelope, never an empty 500.
  - This task is independent of the schema and fixes the visible symptom first.
  - _Requirements: 5.3_
- [ ] 13.2 Run `AnalyticsService` over the reader
  Difficulty: hard
  Technology: csharp
  Depends on: 4.2, 11.1, 11.2, 11.3, 11.4, 13.1
  Capabilities: C# analytics service refactoring onto store interfaces, with xUnit tests over fakes
  Files: `dash/server/KyberDash.Application/Services/AnalyticsService.cs`, `dash/server/KyberDash.Application/Services/CanonProjectionService.cs`, `dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsReadPathTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~AnalyticsReadPath`
  Acceptance criteria: `AnalyticsService` depends only on `IRelationalStoreReader` and `IProjectionStore`, with no harness-format or JSON parse of store values; a failed session projection writes `PROJECTION_FAILED` and the operation answers from the rest; cost is harness-reported, priced at read time, or a stated reason, never blended.
  - _Requirements: 3.6, 5.1, 5.4, 5.5, 10.3_
- [ ] 13.3 Assemble the all-harness fixture set
  Difficulty: medium
  Technology: csharp
  Depends on: 6.3, 7.3, 7.4, 8.3, 8.4, 8.5, 9.2, 10.1, 10.2, 10.3, 10.4, 10.5, 10.6, 10.7, 10.8, 10.9, 10.10
  Capabilities: C# test fixture composition over in-memory fakes, with xUnit
  Files: `dash/server.tests/KyberDash.Application.Tests/Analytics/AllHarnessFixtureSet.cs`, `dash/server.tests/KyberDash.Application.Tests/Analytics/AllHarnessFixtureSetTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~AllHarnessFixtureSet`
  Acceptance criteria: every mapper's synthetic fixtures load into one `InMemoryRelationalStore` and together produce `llm.invoke`, `tool.invoke` and `agent.invoke`, kinds `internal` and `client`, every candidate harness id with an evidenced format, and the value shapes that broke today's reader.
  - _Requirements: 11.2, 11.3, 11.4_
- [ ] 13.4 Test `status`, `catalog`, `query` and `search-sessions`
  Difficulty: medium
  Technology: csharp
  Depends on: 13.2, 13.3
  Capabilities: C# engine-level analytics tests over in-memory fakes, with xUnit
  Files: `dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsOperationsReadTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~AnalyticsOperationsRead`
  Acceptance criteria: the real engine and service over the fixture set answer HTTP 200 with the contract-v1 envelope and expected values for all four operations, and a harness with no `tool.invoke` reports "not reported", not zero.
  - Existing tests that mock `IAnalyticsService` stay for routing and no longer count as
    analytics coverage.
  - _Requirements: 5.2, 10.2, 10.3, 11.5_
- [ ] 13.5 Test `inspect`, `findings`, `compare` and `outcomes`
  Difficulty: medium
  Technology: csharp
  Depends on: 13.2, 13.3
  Capabilities: C# engine-level analytics tests over in-memory fakes, with xUnit
  Files: `dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsOperationsDetailTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~AnalyticsOperationsDetail`
  Acceptance criteria: all four operations answer HTTP 200 with expected values over the fixture set; an injected store failure answers the error envelope; one failing session projection leaves the operation answering from the others.
  - _Requirements: 5.2, 5.3, 5.4, 10.3, 11.5_

### 14. Stored-fields view

- [ ] 14.1 Serve `GET /api/sessions/{sessionKey}/stored-fields`
  Difficulty: medium
  Technology: csharp
  Depends on: 4.2, 7.3
  Capabilities: C# ASP.NET minimal API endpoint over store interfaces, with xUnit tests
  Files: `dash/server/KyberDash.Api/Endpoints/DashEndpointMappings.cs`, `dash/server.tests/KyberDash.Presentation.Tests/StoredFieldsEndpointTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Presentation.Tests --filter FullyQualifiedName~StoredFieldsEndpoint`
  Acceptance criteria: the response lists every catalog column reachable from a Copilot session, labelled `table.column`, with absent child rows marked not reported; a test fails if a reachable column is missing.
  - _Requirements: 9.1, 9.2, 9.3, 9.4_
- [ ] 14.2 Show the Stored fields panel on the session page
  Difficulty: medium
  Technology: typescript
  Depends on: 14.1
  Capabilities: React components in TypeScript with a typed API client, with vitest tests
  Files: `dash/web/src/pages/RunDetail.tsx`, `dash/web/src/components/StoredFieldsPanel.tsx`, `dash/web/src/components/StoredFieldsPanel.test.tsx`, `dash/web/src/lib/kyberApi.ts`
  Command: `npm --prefix dash test -- web/src/components/StoredFieldsPanel.test.tsx`
  Acceptance criteria: on `/session/:sessionId`, every field from a fake API built from a catalog snapshot has its `table.column` label, and an absent row shows "not reported", never zero or empty.
  - _Requirements: 9.1, 9.2, 9.3, 9.4_

### 15. Field register

- [ ] 15.1 Create the field register and its test
  Difficulty: medium
  Technology: csharp
  Depends on: 2.2
  Capabilities: C# reflection test over native types against a Markdown table, plus reference writing
  Files: `docs/dash/field-register.md`, `dash/server.tests/KyberDash.Data.Tests/Collector/FieldRegisterTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~FieldRegister`
  Acceptance criteria: the register has one section per harness, each row a field with its `table.column` or "dropped" and a reason; the test fails on a native-type property with no row for its harness.
  - _Requirements: 6.2, 6.6, 12.2_

### 16. Schema version, deletion and re-ingest

- [ ] 16.1 Stamp the relational schema version and refuse newer stores
  Difficulty: easy
  Technology: csharp
  Depends on: 12.1
  Capabilities: C# version checks over a fake file system, with xUnit tests
  Files: `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Version.cs`, `dash/server.tests/KyberDash.Data.Tests/Repositories/SchemaVersionTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~SchemaVersion`
  Acceptance criteria: a newer version is refused with the existing message and nothing is deleted.
  - _Requirements: 7.3_
- [ ] 16.2 Delete an older store and re-ingest
  Difficulty: medium
  Technology: csharp
  Depends on: 16.1, 16.3, 0.1, 0.2, 0.3
  Capabilities: C# file-lifecycle handling over a fake file system, with xUnit tests
  Files: `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Version.cs`, `dash/server.tests/KyberDash.Data.Tests/Repositories/StoreRebuildTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Data.Tests --filter FullyQualifiedName~StoreRebuild`
  Acceptance criteria: through a fake file system and the fake store, an older version deletes the store's database file and its write-ahead-log and shared-memory companions without opening them, keeps no copy, creates the schema and starts a full re-ingest; the log names the path, both versions, the settings reset and the history classes re-ingest cannot recover.
  - Gates 0.1, 0.2 and 0.3 are dependencies, so this task is never ready before the owner has
    resolved BL-1, BL-2 and BL-3.
  - The deletion must not be merged to a release, published or run on any machine until those
    resolutions are recorded (see "Open blockers"). No test touches a real database file.
  - _Requirements: 7.1, 7.2, 7.4, 7.6_
- [ ] 16.3 Run a full re-ingest with per-harness counts
  Difficulty: medium
  Technology: csharp
  Depends on: 5.2, 13.3
  Capabilities: C# ingestion orchestration over fakes, with xUnit tests
  Files: `dash/server/KyberDash.Application/CanonRefreshEngine.cs`, `dash/server.tests/KyberDash.Application.Tests/Ingest/FullReingestTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Application.Tests --filter FullyQualifiedName~FullReingest`
  Acceptance criteria: a full re-ingest over the refresh history window reports sessions and records stored per harness, tested over a fake file system and the fake store.
  - _Requirements: 6.5, 7.5_
- [ ] 16.4 Remove the old store code
  Difficulty: medium
  Technology: csharp
  Depends on: 13.4, 13.5, 14.1, 16.3
  Capabilities: C# dead-code removal guarded by a source-scanning xUnit test
  Files: `dash/server/KyberDash.Domain/CanonSchema.cs`, `dash/server/KyberDash.Data/Repositories/CanonStoreRepository.cs`, `dash/server/KyberDash.Data/Repositories/CanonStoreRepository.Queries.cs`, `dash/server.tests/KyberDash.Domain.Tests/Schema/LegacyColumnAbsenceTests.cs`
  Command: `dotnet test dash/server.tests/KyberDash.Domain.Tests --filter FullyQualifiedName~LegacyColumnAbsence`
  Acceptance criteria: the old DDL and JSON paths are gone, and a test fails if any non-test source file names an old JSON column. Release acceptance is not claimed by this task: it needs BL-2 closed by a proof route, recorded in the support matrix.
  - _Requirements: 2.2, 2.3, 5.1_

### 17. Documentation

- [ ] 17.1 Update the KyberDash architecture document
  Difficulty: easy
  Depends on: 13.2
  Capabilities: technical writing in Markdown and Mermaid
  Files: `docs/dash/architecture.md`
  Command: `kyber-weave docs validate`
  Acceptance criteria: the document describes the relational schema, the collector boundary and the analytics read path, and docs validate and drift pass.
  - _Requirements: 12.1_
- [ ] 17.2 Write the ADR
  Difficulty: easy
  Depends on: 16.4
  Capabilities: architecture decision records in Markdown
  Files: `docs/adr/README.md`
  Command: `kyber-weave docs validate`
  Acceptance criteria: a new ADR records OD-1 to OD-9, the autonomous decisions that held, and how BL-1, BL-2 and BL-3 were resolved, and docs validate and drift pass.
  - _Requirements: 12.3_

### 18. Closeout

- [ ] 18.1 Specification closeout
  Difficulty: easy
  Depends on: 0.1, 0.2, 0.3, 10.11, 10.12, 14.2, 16.2, 17.1, 17.2
  Capabilities: documentation harvest and specification archiving, assigned to `docs-dev`
  Files: `docs/specs/kyberdash-relational-store/README.md`, `docs/specs/README.md`
  Command: `kyber-weave docs validate`
  Acceptance criteria: harvest rows H1 to H7 are harvested into canonical documents, the specification is archived, and merge-ready docs validation passes.
  - _Requirements: all_
