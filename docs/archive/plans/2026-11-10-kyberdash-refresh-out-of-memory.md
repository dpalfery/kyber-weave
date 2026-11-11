---
id: plans/2026-11-10-kyberdash-refresh-out-of-memory
title: KyberDash refresh runs out of memory on re-minted duplicate records
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
created: 2026-11-10
last-reviewed: 2026-11-10
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash]
---

# KyberDash refresh runs out of memory on re-minted duplicate records

**Status: Archived, 2026-11-10.** Approved for execution under decision D13 (autonomous approval of gate `approve-and-execute-2026-11-10-kyberdash-refresh-oom`). The task file was removed at archive; the Test contract and Implementation tasks below hold its content.
**Development mode:** `test-first` (RED by `test-dev`, GREEN by the implementation specialist, never the same worker).
**Autonomy:** `autonomous` (every open item decided by recommendation; provenance in `## Decisions`).
**Scale:** `extends-component`, component `KyberDash`.
**Delivery:** one draft pull request on branch `claude/fix/kyberdash-refresh-out-of-memory`. No merge.

## Problem and goal

**Background.** KyberDash (the analytics engine under `dash/`) reads each coding harness's own history files on a schedule ("refresh"), turns every model call into a canonical record, and stores it in one SQLite file, `~/.kyberdash/canon.db`. A projection then rebuilds sessions and runs from those records. The scheduled refresh and the long-running engine (`kyberdash web`) run the same projection.

**Problem.** Since 2026-11-10T19:52Z every `kyberdash dash refresh --trigger scheduled` dies with exit 134, "JavaScript heap out of memory", at about 4 GB. The engine reached 4.48 GB resident. Two defects combine:

1. **Record identity is not stable.** When a call has no stable native id, its id is a hash that includes the call's position in the list the refresh read. That list is cut to a two-week window, and the window start moves every run, so the same call gets a new id each time and the old row stays. One Kilo session now holds 15,136 rows for its calls, up to four copies of each.
2. **The projection holds a whole session in memory.** `buildRuns` loads every record of a run, and `deriveOutcome` reads each record's content, which inflates and caches its parts. For that session this is 1.33 GB of decompressed text at once.

The same out-of-memory in this function was fixed once before (commit `3781b63a`, pull request #463); the duplicates outgrew that fix.

**Goal.**

- A re-read of the same source call produces the same record id, wherever the window starts.
- The duplicate rows already in the owner's store are removed by a shipped, tested, idempotent repair that runs when the fixed build opens the store.
- One oversized session can no longer exhaust the heap in the run-outcome step, whatever made it large.
- Each of the three is pinned by a regression test written first and seen failing.

## Decisions

Every open item was decided by recommendation under the autonomous-decisions rule. `autonomous: recommendation accepted` means decided and awaiting the owner's approval at the approve-and-execute gate. `autonomous: low-confidence, no authoritative source` marks a call no source settled; the pull request body lists those rows for review.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Why do Kilo calls reach the hash fallback instead of a stable id? | **They come from child (subagent) sessions.** The Kilo parser reads a root session together with its child sessions, and builds each call's key as `kilo-code:<message's own session id>:<message id>`. `nativeRecordIdentity` only accepts a key that starts with `kilo-code:<root session id>:`. A child message's key starts with the child's id, so it falls through to the hash. Root-session messages keep their stable message id. | Store evidence (read-only): the duplicated call at 2026-10-28T02:56:55.956Z is message `msg_180707494001IdkB54aFkJmB4J` in child session `ses_e7f8f8b80ffeG5YZVUxCJuKjra`, whose parent is `ses_e81cf51e6ffebQ4Mq91UpGLGYz`. In that tree, 1,722 assistant messages are in child sessions and 324 in the root. All 324 root rows carry `msg_…` ids and have exactly one copy. Code: `createSqliteSessionParser` in `dash/src/providers/sqlite-session-parser.ts` (`dedupKey`, recursive `session_tree` query) and `nativeRecordIdentity` in `dash/src/synth/synth.ts` (the `prefix` check). | autonomous: recommendation accepted |
| D2 | Do other harnesses share the position-based hash? | **Yes. Cursor and Copilot CLI are re-minting today, and Pi, Command Code and Copilot agent use the same fallback.** The fix is general, not Kilo-only. | Store evidence: cross-import identical duplicates (same harness, session, timestamp, name and tokens; more than one import time) are Kilo 1,905 groups / 4,627 extra rows, Cursor 768 / 11,018 and Copilot CLI 30 / 30. A Cursor call at 2026-10-31T06:57:06.386Z holds 16 copies; each reproduces the hash at a different position (873, 783, 756 … 574), one per import. Rows on the hash fallback: Cursor 12,084, Kilo 8,310, Copilot CLI 1,326, Command Code 1,266, Pi 742, Copilot agent 12. Antigravity's same-timestamp groups have differing tokens and one import: those are distinct calls, not duplicates. | autonomous: recommendation accepted |
| D3 | How is Kilo's identity made stable? | **The Kilo parser stamps the message id on each call as a new identity-only field, `ParsedProviderCall.recordIdentity`.** `nativeRecordIdentity` reads it after the explicit id and `turnId`, and before the key-prefix rule. Root-session ids stay byte-identical, because the prefix rule already yields the message id for them. | `turnId` and the envelope's `nativeRecordId` are not reused: both also feed the twin-collapse key (`collapseKey` in `synth.ts`), and Kilo today collapses on the counter key alone. A new field keeps pairing and twin collapse unchanged. Kilo message ids are unique across the store (`msg_` prefix plus a sortable id). | autonomous: recommendation accepted |
| D4 | How is the hash fallback made window-independent for every harness? | **The hash's ordinal becomes the call's occurrence index among the batch's calls with the same native session id and the same timestamp**, in source order. It replaces the index in the whole batch. | The window is cut by timestamp (`sliceCallsToWindow` in `dash/src/refresh/source-reader.ts` drops `timestamp < dateRange.start`), so every call sharing a timestamp is either all in or all out. The occurrence index therefore cannot move when the window does, or when another session leaves the batch. Two distinct calls at the same instant still get distinct ids (occurrence 0, 1, …). One-time cost: every hash-backed id changes once; D5 removes the old copies. | autonomous: low-confidence, no authoritative source. Expensive to reverse (record identity), but the alternatives (keep position, or hash content) either keep the bug or merge distinct calls with identical counters. |
| D5 | How are superseded copies removed going forward? | **`CanonStore.commitSourceUnit` supersedes hash-backed rows.** In the same transaction it deletes every existing `llm.invoke` row of the same harness and source key whose timestamp equals one in the batch, whose span id is not in the batch, and whose stored payload marks it hash-backed (`raw.provenance.recordDigest` present). It deletes their `tool.invoke` children (by `parent_span_id`) and their `record_provenance` rows with them. Rows with a stable native id are never touched. | The batch holds every call at each of its timestamps (D4 reasoning), so a hash-backed row at that timestamp that the batch did not reproduce is a stale copy. Limiting to hash-backed rows keeps the change away from harnesses with real ids. This extends ADR 0016's repair contract ("same-span upsert over stale rows"), which cannot remove a copy whose span id changed; closeout amends the ADR (D9). | autonomous: low-confidence, no authoritative source. Cost of being wrong: a deleted distinct call. Mitigated by the hash-backed and same-timestamp limits and by the tests in T3. |
| D6 | Is a parser contract bump needed to roll out D3 and D4? | **No.** A unit whose source changed is re-read and its rows written under the new ids, and D5 removes the superseded copies. An unchanged unit is never re-read, so its old ids stay stable and cannot duplicate. | A bump would force every stored unit of six harnesses to be re-read and re-synthesized on first refresh, which costs time on a 5.6 GB store and buys nothing D5 does not already give. | autonomous: recommendation accepted |
| D7 | How are the duplicates already in the owner's store repaired? | **A schema migration, version 23 to 24 (`MIGRATIONS[23]` in `dash/src/canon/store-migrations.ts`), that runs when the fixed build opens the store.** It groups file-sourced `llm.invoke` rows by harness, session, timestamp and name. In a group that spans more than one import time (`record_provenance.imported_at_utc`), it keeps every row from the latest import and deletes the hash-backed rows from older imports, with their `tool.invoke` children and provenance rows. It then increments the records write marker. Re-running it changes nothing. It has a blocking form (store constructor) and a yielding form (`migrateCanonDatabase`), as migration 21 does. | Each import wrote one full copy of every call at a timestamp it covered, so the latest import's rows are the complete current set. A single-import group (distinct calls sharing an instant) is left alone. The schema-version change also forces a full projection rebuild (`projectionIsCurrent` in `dash/src/canon/projection.ts`). Expected on the owner's store: about 15,700 duplicate call rows plus their tool children; the grouping scan took about one to two minutes read-only. No `VACUUM`: freed pages are reused. A build older than this one refuses a v24 store, as with every schema step. | autonomous: low-confidence, no authoritative source. Expensive to reverse (deletes persisted rows); the tests in T3 pin exactly which rows go and stay. |
| D8 | How is the projection's memory bounded? | **Stream the run-outcome step one record at a time.** `deriveOutcome` (`dash/src/canon/outcome.ts`) takes any iterable of records, reads each record's content and parts in one pass, and releases them (`releaseRecordParts`) before the next record. `CanonStore` gains `iterateRecordsForSession(key)` and `iterateRecordsForShare(key, harness)`, generators over a SQLite statement iterator. `buildRuns` (`dash/src/canon/runs.ts`, the persist loop) streams each execution's records through the payload reduction into `deriveOutcome` instead of building an array. The outcome block is unchanged for every input. | At most one record's parts are inflated at a time, so peak memory in this step no longer grows with session size. This is the "whatever its cause" bound: it holds even if duplicates return. The session projection (`buildSessions`) still needs a whole session in memory for the twin collapse and already releases parts after counting; it is out of scope (risk R3). | autonomous: recommendation accepted |
| D9 | Is a new ADR needed? | **No new ADR. Amend ADR 0016 in place** at closeout: hash-backed identity is window-independent (D4), and the repair contract gains supersede-on-commit for hash-backed rows (D5). | ADR 0016 (`docs/adr/0016-kyberdash-harness-source-refresh.md`) owns the refresh identity and repair contract; amending in place is its established practice. | autonomous: recommendation accepted |
| D10 | Does the tray supervisor's 55-minute unrestarted engine belong in this plan? | **No. It is a follow-up todo**, written at closeout under `docs/todo/` and registered in its index. | A separate defect in a separate layer (Rust tray, `ServerSupervisor::attempt` in `dash/tray/src-tauri/src/supervisor.rs`, whose doc comment says the caller owns the restart loop). Folding it in would add a Tauri worker and gates to an already multi-layer fix. | autonomous: recommendation accepted |
| D11 | How is the owner's live store protected? | **No task opens `~/.kyberdash/canon.db`.** Every test uses an in-memory or temporary store. The repair reaches the owner's store only through D7 when the fixed build is installed. Planning read the store only through an `immutable=1&mode=ro` URI. | Hard constraint from the run packet. Scheduled jobs stay paused; the running engine (port 4747) is not touched. | autonomous: recommendation accepted |
| D12 | Development mode and test contract. | **`test-first`**, with the Test contract below. | Default mode; no opt-out was supplied. | autonomous: recommendation accepted |
| D13 | Is the plan approved for execution? | **Approved.** D1 to D12 and the Test contract are accepted as recorded, and the plan is executed as written. | Conductor input `AUTONOMOUS_APPROVAL: approve-and-execute-2026-11-10-kyberdash-refresh-oom` under the Config Reg's resolved autonomy `autonomous`, 2026-11-10; no human answered the gate. The low-confidence rows D4, D5 and D7 stay flagged for the pull request body. | autonomous: recommendation accepted (AUTONOMOUS_APPROVAL approve-and-execute-2026-11-10-kyberdash-refresh-oom, 2026-11-10) |

## Investigation findings

**Discovery:** codegraph 4, docs 4, direct reads 21, read-only store queries 9.

- **Revision read.** Worktree `/Users/dave/git/personal/claude/kyber-weave.worktrees/20261111T025704Z-claude-fix-kyberdash-refresh-out-of-memory`, branch `claude/fix/kyberdash-refresh-out-of-memory`, HEAD `e418fe931bde6d46484bcb299b884511ee5a8abf` (`origin/main`). CodeGraph was queried through the worktree's own index with the `codegraph explore` CLI (4 calls).
- **Direct reads, 21, reason "explore output elided the needed body as a gap, or did not cover the range".** Files: `dash/src/synth/synth.ts` (`synthesizeEnvelopes`, `collapseKey`, `callCounterKey`), `dash/src/synth/provider.ts`, `dash/src/providers/sqlite-session-parser.ts`, `dash/src/providers/kilo-code.ts`, `dash/src/providers/types.ts`, `dash/src/refresh/source-reader.ts` (`toEnvelope`, `sliceCallsToWindow`), `dash/src/refresh/orchestrator.ts`, `dash/src/canon/runs.ts`, `dash/src/canon/outcome.ts`, `dash/src/canon/store-rows.ts`, `dash/src/canon/store.ts`, `dash/src/canon/store-migrations.ts`, `dash/src/canon/projection.ts`, `dash/tray/src-tauri/src/supervisor.rs`, plus locating greps.
- **Docs, 4 calls.** One `docs_answer_question_from_repo_documentation` (low confidence, no answer) and three `docs_find_documents_citing_code_symbol` (`nativeRecordIdentity`, `deriveOutcome`: none; `commitSourceUnit`: `docs/dash/architecture.md`). The docs server is bound to the canonical checkout (branch `claude/fix/architect-plan-date-prefix`, rev `0ae061d2`), so those answers were used only to locate documents.
- **Store queries, 9, read-only** through `file:~/.kyberdash/canon.db?immutable=1&mode=ro` and the Kilo source `file:~/.local/share/kilo/kilo.db?immutable=1&mode=ro`. Results are in D1 and D2.

**Verified facts:**

- **Identity order** (`nativeRecordIdentity`, `synth.ts`): envelope `nativeRecordId` (set from `turnId` by `toEnvelope`), then `turnId`, then the key after `${provider}:${sessionId}:`, then `sha256(session, timestamp, "llm.invoke", ordinal)`. The ordinal is the index in `collapseEnvelopeTurns(envelopes)` (`synthesizeEnvelopes`) or `collapseCallAndTurns(items)` (`synthesize`): the whole windowed batch.
- **Hash marker.** A hash-backed record carries `recordDigest` in `raw.provenance` (`provenanceFor`); `twin-dedupe.ts` already reads it. `record_provenance` has no such column.
- **Repair contract today.** Refresh writes through `commitSourceUnit` (`store.ts`), which upserts records, provenance and checkpoint in one transaction and never deletes. Parser-contract bumps rely on same-span upsert (comments in `dash/src/refresh/registry.ts`). A changed span id is never cleaned up.
- **Import time is first write.** In the Kilo session the 324 stable root rows all carry the first import time; each hash-backed copy carries the import that minted it.
- **Memory path.** `buildRuns` persist loop calls `store.recordsForSession` / `recordsForShare` (both `.all()`), `reduceRecordPayloads`, then `deriveOutcome(outcomeRecords)`. `deriveOutcome` makes four passes and reads `record.content` and `record.parts`, both lazy and cached by `defineLazy` in `store-rows.ts` until released. Nothing releases them in this path.
- **Migration framework.** `SCHEMA_VERSION = 23` (`dash/src/canon/store-schema.ts`); `MIGRATIONS` keyed by from-version; migration 21 shows the blocking-plus-yielding pattern; tests per step (`dash/src/canon/migration-v23.test.ts`).
- **Projection rebuild.** `projectionIsCurrent` is false when the records write marker or the schema version moved, so the repair's marker bump and version step rebuild sessions and runs.

## Design

1. **Identity-only field.** `ParsedProviderCall` (`dash/src/providers/types.ts`) gains `recordIdentity?: string`, documented as identity-only: never a pairing or twin-collapse key.
2. **Kilo stamps it.** `createSqliteSessionParser` (`dash/src/providers/sqlite-session-parser.ts`) yields each assistant call with `recordIdentity: msg.id`, alongside the existing `pairingId`. The `session-level` fallback call is unchanged.
3. **Identity order.** `nativeRecordIdentity` (`synth.ts`): envelope id, then `turnId`, then `call.recordIdentity`, then the key prefix, then the hash. Cursor's existing pairing-id exclusion is unchanged.
4. **Window-independent ordinal.** `synthesizeEnvelopes` and `synthesize` (`Synthesizer`, `synth.ts`) compute each item's ordinal as its occurrence count among earlier collapsed items with the same `nativeSessionIdFor(call, envelope)` and the same `call.timestamp`, and pass that to `synthesizeCall`. Nothing else reads the index.
5. **Supersede on commit.** `CanonStore.commitSourceUnit` (`dash/src/canon/store.ts`), inside its transaction and before writing, finds rows by `record_provenance (harness_id, source_key)` joined to `records` with `op = 'llm.invoke'`, a timestamp in the batch's `llm.invoke` timestamps, and a span id not in the batch. It decompresses each candidate's `raw` and keeps only those whose `provenance.recordDigest` is set. It deletes them, their `tool.invoke` rows whose `parent_span_id` is one of them, and the provenance rows of all deleted spans. The records write marker already moves on every commit (`CanonStore.writeRecords` calls `bumpRecordsWriteMarker`), so the projection sees the deletion.
6. **Repair migration.** `SCHEMA_VERSION` becomes 24. `MIGRATIONS[23]` applies D7. One SQL pass finds candidate groups (`records` joined to `record_provenance`, `source LIKE 'codeburn/%'`, `op = 'llm.invoke'`, grouped by `harness, session_id, timestamp, name`, `HAVING COUNT(DISTINCT imported_at_utc) > 1`). Per group, it deletes rows whose import time is below the group's maximum and whose decompressed `raw.provenance.recordDigest` is set, plus their tool children and provenance. It finishes by incrementing the `RECORDS_WRITE_MARKER_KEY` metadata value. Given `options.yield`, it yields between groups; otherwise it runs in one pass. Both commit the same work.
7. **Streaming outcome.** `deriveOutcome(records: Iterable<CanonicalRecord>)` makes one pass and keeps the existing output order of each list: exit and termination, error signals, correction turns (with `turnIndex` the record's position), test outputs. After reading a record it calls `releaseRecordParts(record)`. The empty-input block is unchanged. `deriveRunIdentity` and `outcomeAvailability` keep working with arrays.
8. **Streaming store reads.** `CanonStore.iterateRecordsForSession(key)` and `iterateRecordsForShare(key, harness)` are generators over the same queries as `recordsForSession` / `recordsForShare`, using `.iterate()`. `buildRuns`' persist loop feeds `deriveOutcome` from a generator that walks each execution's iterator and reduces each record's payload (`record.raw = projectedAttributes(record.raw)`, the `reduceRecordPayloads` step per record) as it goes.

## Design harvest

| ID | Design item | Target component | Target doc type | Target location |
|---|---|---|---|---|
| H1 | Record identity order including `recordIdentity`; window-independent hash ordinal; Kilo child-session cause | KyberDash | architecture | `docs/dash/architecture.md` (canonical model / record identity passage) |
| H2 | Supersede-on-commit of hash-backed rows; the v24 duplicate repair | KyberDash | adr | `docs/adr/0016-kyberdash-harness-source-refresh.md` (dated amendment) |
| H3 | Run-outcome step streams one record at a time; the memory bound it gives | KyberDash | architecture | `docs/dash/architecture.md` (projection memory passage near `buildRuns`) |
| H4 | Schema v24 and what the upgrade does to an existing store (one-time delete, rebuild, duration) | KyberDash | runbook | `docs/dash/runbook.md` (upgrade / store maintenance section) |
| H5 | Tray supervisor leaves a dead engine unrestarted (D10) | KyberDash | todo | new todo under `docs/todo/`, registered in `docs/todo/README.md` |

## Test contract (test-first)

All commands run from the repository root. Paths after `--` are relative to `dash/`. Every run must report a non-zero test count. No test opens `~/.kyberdash/canon.db`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T2 | `dash/src/synth/synth.test.ts` (describe `record identity is window-independent`); `dash/src/providers/kilo-code.test.ts` (describe `kilo child-session record identity`) | `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/synth/synth.test.ts src/providers/kilo-code.test.ts` | Hash-backed calls (no `turnId`, no key-prefix match) get the same span ids from `synthesizeEnvelopes` when the batch is [other-session call, A@t1, B@t2, C@t2] and when it is [B@t2, C@t2]; B and C still differ. Same for `synthesize`. A Kilo store fixture with a root and a child session: the child message's span id is `synth:<harness>:<root>:<msg.id>`, identical whether or not an earlier message is in the window; a root message's span id is unchanged from today (`…:<msg.id>`). `pairingId` and twin collapse are unchanged (an existing collapse test still passes). | The run fails: span ids differ between the two batches; the child message id is a 16-hex hash. Failure output in the task digest. | Same command passes with assertions unchanged; then typecheck, lint and `check:reachable` pass. |
| T4 | `dash/src/canon/store-supersede.test.ts` (describe `commitSourceUnit supersedes hash-backed copies`); `dash/src/canon/migration-v24.test.ts` (describe `v23 -> v24 duplicate record repair`) | `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/store-supersede.test.ts src/canon/migration-v24.test.ts` | Supersede: committing a unit with calls at t1 deletes an older hash-backed row of the same harness and source key at t1 that the batch lacks, with its tool child and provenance; it keeps a stable-id row at t1, a hash-backed row at t0, and a hash-backed row at t1 under another source key. Migration: a genuine v23 store holding (a) a Kilo-shaped group of three hash-backed copies from three imports, each with a tool child, (b) a Cursor-shaped group of two distinct calls per import over two imports, (c) a single-import group of distinct same-instant calls, (d) a stable-id row across imports, opens as v24 with only the latest import's copies of (a) and (b), (c) and (d) untouched, children and provenance of deleted rows gone, the records write marker incremented. Running `MIGRATIONS[23]` again changes nothing. `migrateCanonDatabase` (yielding) leaves the identical rows. | The run fails: the old copies survive the commit; `SCHEMA_VERSION` is 23 and `MIGRATIONS[23]` is undefined. | Same command passes unchanged; then typecheck and lint pass. |
| T6 | `dash/src/canon/outcome.test.ts` (describe `deriveOutcome memory bound`); `dash/src/canon/runs.test.ts` (describe `buildRuns streams outcome records`) | `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/outcome.test.ts src/canon/runs.test.ts` | `deriveOutcome` over 200 records whose `parts` are lazy and instrumented (the test counts inflations live at once and releases): peak live inflated parts is at most 1, none remain after it returns, and the block deep-equals the block for the same records built eagerly (errors, corrections, test deltas, termination). It accepts a generator. `buildRuns` over an in-memory `CanonStore` whose `iterateRecordsForSession` / `iterateRecordsForShare` yield instrumented records: peak live inflated parts during the run phase is at most 1, and the stored run outcome equals the outcome of the same records derived as an array. Every existing `outcome.test.ts` and `runs.test.ts` case still passes. | The run fails: peak live inflated parts equals the record count; `iterateRecordsForSession` does not exist. | Same command passes unchanged; then typecheck, lint and `check:reachable` pass. |
| T7 | No test: documentation task | `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .`, run in the worktree | Harvest H1 to H5 written; plan archived with its closeout mapping; task file removed | n/a (no-test task, replaced by the two docs gates) | Both gates report zero findings |

## Implementation tasks

Every developer task ends with the worker's own `task-gate` run. Standards to fetch: `docs_get_coding_standard_for_technology('typescript')` for `typescript-dev`, `('test')` for `test-dev`. Every test, gate and build command runs through `scripts/proc-guard.sh run --` from the repository root. No task opens, copies or writes `~/.kyberdash/canon.db`, and none starts `kyberdash web` or a refresh against the real home directory.

### T1 (test-dev): failing tests for stable record identity

**Session:** `red-tests`. **Depends on:** none. **Required skills:** `test-dev`.

**Files:**
- `dash/src/synth/synth.test.ts` (new describe "record identity is window-independent")
- `dash/src/providers/kilo-code.test.ts` (new describe "kilo child-session record identity")

**Work.** In `synth.test.ts`, use Synthesizer.synthesizeEnvelopes and Synthesizer.synthesize with hand-built ParsedProviderCall values whose deduplicationKey does not start with `${provider}:${sessionId}:` and that carry no turnId. In `kilo-code.test.ts`, reuse the file's existing kilo.db fixture builder to add a root session, one child session (parent_id = root) and assistant messages in both, parse through createKiloCodeProvider(...).createSessionParser, and derive ids with spanIdFor (exported from synth.ts) under harness `kilo-shared-runtime`.

**Acceptance:** the T2 row of the Test contract, RED column: both describes fail for the stated reason, and the failure output is pasted into the digest.

**Command:** `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/synth/synth.test.ts src/providers/kilo-code.test.ts`

### T2 (typescript-dev): stable record identity

**Session:** `green-identity`. **Depends on:** T1. **Required skills:** `typescript-dev`.

**Files:**
- `dash/src/providers/types.ts` (type ParsedProviderCall: new field recordIdentity)
- `dash/src/providers/sqlite-session-parser.ts` (function createSqliteSessionParser: the yield of each assistant call)
- `dash/src/synth/synth.ts` (function nativeRecordIdentity: identity order; methods Synthesizer.synthesizeEnvelopes and Synthesizer.synthesize: ordinal per Design 4)

**Acceptance:** T1's tests pass unchanged; existing `synth.test.ts`, `kilo-code.test.ts`, `provider.test.ts` and `contract-bump-cursor-pi.test.ts` pass; no parser contract version changes (D6).

**Commands:**
  - `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/synth/synth.test.ts src/providers/kilo-code.test.ts src/synth/provider.test.ts src/refresh/contract-bump-cursor-pi.test.ts`
  - `scripts/proc-guard.sh run -- npm --prefix dash run typecheck`
  - `scripts/proc-guard.sh run -- npm --prefix dash run lint`
  - `scripts/proc-guard.sh run -- npm --prefix dash run check:reachable`

### T3 (test-dev): failing tests for supersede and the v24 repair

**Session:** `red-tests`. **Depends on:** none. **Required skills:** `test-dev`.

**Files:**
- `dash/src/canon/store-supersede.test.ts` (new file, describe "commitSourceUnit supersedes hash-backed copies")
- `dash/src/canon/migration-v24.test.ts` (new file, describe "v23 -> v24 duplicate record repair")

**Work.** The supersede test runs against new CanonStore(':memory:') and CanonStore.commitSourceUnit; hash-backed rows carry raw.provenance.recordDigest. The migration test is modelled on the existing migration-v23 test file: build the store in a mkdtempSync directory, stamp schema_version 23, insert the four fixture groups of the T4 contract row, reopen with CanonStore, and separately run migrateCanonDatabase. Import MIGRATIONS, SCHEMA_VERSION and RECORDS_WRITE_MARKER_KEY from the store module.

**Acceptance:** the T4 row of the Test contract, RED column, with failure output in the digest.

**Command:** `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/store-supersede.test.ts src/canon/migration-v24.test.ts`

### T4 (typescript-dev): supersede on commit and the v24 repair migration

**Session:** `green-store`. **Depends on:** T3. **Required skills:** `typescript-dev`.

**Files:**
- `dash/src/canon/store.ts` (method CanonStore.commitSourceUnit, per Design 5; wire the yielding repair into migrateCanonDatabase and the constructor's migrate the way step 21 is wired)
- `dash/src/canon/store-migrations.ts` (MIGRATIONS entry 23 and a yielding helper beside runYieldingSessionTurnBackfill, per Design 6)
- `dash/src/canon/store-schema.ts` (constant SCHEMA_VERSION becomes 24)

**Acceptance:** T3's tests pass unchanged; the existing migration, migration-v23, store and refresh pipeline tests pass (update only a literal schema-version expectation of 23 to 24, and say so in the digest).

**Commands:**
  - `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/store-supersede.test.ts src/canon/migration-v24.test.ts src/canon/migration.test.ts src/canon/migration-v23.test.ts src/canon/store.test.ts src/refresh/pipeline.test.ts`
  - `scripts/proc-guard.sh run -- npm --prefix dash run typecheck`
  - `scripts/proc-guard.sh run -- npm --prefix dash run lint`

### T5 (test-dev): failing tests for the outcome memory bound

**Session:** `red-tests`. **Depends on:** none. **Required skills:** `test-dev`.

**Files:**
- `dash/src/canon/outcome.test.ts` (new describe "deriveOutcome memory bound")
- `dash/src/canon/runs.test.ts` (new describe "buildRuns streams outcome records")

**Work.** In the outcome test, build records whose parts and content are instrumented getters with a setter that treats undefined as release (the defineLazy contract in the store-rows module), counting live inflations. In the runs test, use an in-memory CanonStore subclass overriding iterateRecordsForSession and iterateRecordsForShare to yield instrumented records, then call buildRuns(store) and read the stored outcome with CanonStore.listRuns, the accessor the file already uses.

**Acceptance:** the T6 row of the Test contract, RED column, with failure output in the digest.

**Command:** `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/outcome.test.ts src/canon/runs.test.ts`

### T6 (typescript-dev): stream the run-outcome step

**Session:** `green-store`. **Depends on:** T5, T4. **Required skills:** `typescript-dev`.

T4 is a dependency because both tasks edit the store module.

**Files:**
- `dash/src/canon/outcome.ts` (function deriveOutcome, per Design 7)
- `dash/src/canon/store.ts` (new methods CanonStore.iterateRecordsForSession and CanonStore.iterateRecordsForShare, per Design 8)
- `dash/src/canon/runs.ts` (function buildRuns: the persist loop that today builds outcomeRecords)

**Acceptance:** T5's tests pass unchanged; the existing outcome, runs, store-projection-idempotence, findings and store-memory tests pass.

**Commands:**
  - `scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/canon/outcome.test.ts src/canon/runs.test.ts src/canon/store-projection-idempotence.test.ts src/canon/findings.test.ts src/canon/store-memory.test.ts`
  - `scripts/proc-guard.sh run -- npm --prefix dash run typecheck`
  - `scripts/proc-guard.sh run -- npm --prefix dash run lint`
  - `scripts/proc-guard.sh run -- npm --prefix dash run check:reachable`

### T7 (docs-dev): closeout

**Session:** `closeout`. **Depends on:** T2, T4, T6. **Required skills:** `docs-dev`, `kyber-docs-authoring`, `architecture-decision-record`.

**Files:**
- `docs/dash/architecture.md` (H1, H3)
- `docs/adr/0016-kyberdash-harness-source-refresh.md` (H2, dated amendment)
- `docs/dash/runbook.md` (H4)
- `docs/todo/kyberdash-tray-supervisor-restart-loop.md` (new todo, H5)
- `docs/todo/README.md` (register the todo)
- `docs/plans/2026-11-10-kyberdash-refresh-out-of-memory.md` (closeout mapping, then moved to the archive)
- `docs/archive/plans/2026-11-10-kyberdash-refresh-out-of-memory.md` (archived plan)
- `docs/archive/plans/README.md` (archive row)
- `docs/plans/README.md` (Active Plans back to its empty state)
- `docs/plans/2026-11-10-kyberdash-refresh-out-of-memory.tasks.json` (removed with git rm)

**Acceptance:** the T7 row of the Test contract.

**Commands:**
  - `kyber-weave docs validate . --merge-ready`
  - `kyber-weave docs drift .`

## Dependency graph

```text
T1 ──> T2 ─────────────┐
T3 ──> T4 ──┐          ├──> T7
T5 ─────────┴──> T6 ───┘
```

T1, T3 and T5 run in one `test-dev` session in that order. T2 starts when T1 lands; T4 when T3 lands. T6 follows T4 in the same `green-store` session because both edit `dash/src/canon/store.ts`.

## Risks

- **R1. Supersede deletes a real call.** Only if a re-read yields fewer calls at a timestamp than the source holds, for example an appended read that starts in the middle of a same-instant group. Bounded to hash-backed rows (D5); pinned by T3.
- **R2. Migration time on a 5.6 GB store.** The grouping scan took one to two minutes read-only on the owner's store. The engine's open path uses the yielding form; the refresh's blocking open waits that long once.
- **R3. Session projection still loads a whole session.** `buildSessions` needs it for the twin collapse and already releases parts per record. With duplicates removed the largest session drops from 15,136 to about 2,400 rows. A full bound there is a separate change.
- **R4. One-time id change.** Every hash-backed id changes once on its next re-read (D4). Supersede removes the old copy in the same commit, so no window shows both.
- **R5. Rollback.** A build older than this one refuses a v24 store, as for every schema step.

## Out of scope

- The tray supervisor restart loop (`dash/tray/src-tauri/src/supervisor.rs`), recorded as a todo (D10).
- A memory bound for `buildSessions` (R3).
- Reclaiming file space (`VACUUM`) after the repair.
- Any parser contract version bump (D6).
- Running the repair, a refresh or the engine against the owner's real store during this run (D11).

## Verification and review

- Each developer task runs its own commands above and its `task-gate`.
- The end-of-run review council runs under the `code-review` skill over the whole branch; CI runs the full KyberDash suite on the draft pull request.
- The pull request body lists the low-confidence rows D4, D5 and D7, and states that installing the build runs the v24 repair on the owner's store once.

## Completion summary

**Delivered.** Three code commits on `claude/fix/kyberdash-refresh-out-of-memory`, each with its own test change:

- `39201705`: window-independent record identity and the Kilo child-session fix (T2).
- `fc70192c`: supersede-on-commit for hash-backed rows and the schema 23 to 24 repair (T4).
- `7aa1c1c6`: one-record-at-a-time run-outcome step (T6).

The red tests for T1, T3 and T5 ran in the implementation sessions; their failure output is in those workers' completion digests and is not repeated here. This closeout did not re-run them.

**Measured 2026-11-10** on an isolated copy of the owner's 5.6 GB store (292,366 records), never the live store:

- The 0.1.8-rc.25 build's `dash refresh` exited 134 with a JavaScript heap out-of-memory, peak 6.3 GB resident.
- The fixed build's first `dash refresh` (repair plus refresh) exited 0 in 148 seconds, peak 4.1 GB resident. It left 270,517 records: 15,627 duplicate call rows and 6,222 tool rows removed. The worst session went from 15,136 rows to 4,724. A second refresh changed no row counts.
- Orphaned tool rows stayed at 7,577 before and after; the repair added none.
- The 0.1.8-rc.25 `kyberdash web` died of the same out-of-memory about 105 to 135 seconds after start. The fixed build's engine held about 200 MB resident for four minutes on the repaired copy.

**Deviations and gaps.**

- The tray type is `Supervisor`, not `ServerSupervisor` as the plan's Design harvest named it. The todo uses the real name.
- The tray restart gap (about 55 minutes on 2026-11-10) is not fixed here; it is the [tray todo](../../todo/kyberdash-tray-supervisor-restart-loop.md) (D10).
- The session projection (`buildSessions`) still holds a whole session for its twin collapse (R3). It is not bounded by this change.
- Reclaiming freed file space is not part of the upgrade.
- Decisions D4, D5 and D7 are low-confidence, and remain flagged for the pull request body.
- Installing this build runs the v24 repair on the owner's live store once. No task in this run touched that store (D11).

## Closeout mapping

Each row gives the destination of one decision or harvest item, its documentation impact, impact components, last-reviewed date and archive status.

**Decisions**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [architecture, Record identity and the window-independent ordinal](../../dash/architecture.md#record-identity-and-the-window-independent-ordinal) (Kilo child-session cause) | extends-component | KyberDash | 2026-11-10 | archived |
| D2 | [architecture, Record identity](../../dash/architecture.md#record-identity-and-the-window-independent-ordinal) (hash fallback is shared across harnesses); no ADR — ADR waiver: identity detail within [ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md) decision 7 | extends-component | KyberDash | 2026-11-10 | archived |
| D3 | [architecture, Record identity](../../dash/architecture.md#record-identity-and-the-window-independent-ordinal) (identity order, `recordIdentity`); no ADR — ADR waiver: a field-level refinement of ADR 0016 decision 7 | extends-component | KyberDash | 2026-11-10 | archived |
| D4 | [architecture, Record identity](../../dash/architecture.md#record-identity-and-the-window-independent-ordinal); [ADR 0016 amendment, window-independent hash identity](../../adr/0016-kyberdash-harness-source-refresh.md) | extends-component | KyberDash | 2026-11-10 | archived |
| D5 | [ADR 0016 amendment, supersede on commit](../../adr/0016-kyberdash-harness-source-refresh.md) | extends-component | KyberDash | 2026-11-10 | archived |
| D6 | [ADR 0016 amendment, window-independent hash identity](../../adr/0016-kyberdash-harness-source-refresh.md) (no parser contract version change); no ADR — ADR waiver: follows the existing re-read rule in ADR 0016 decision 6 | extends-component | KyberDash | 2026-11-10 | archived |
| D7 | [ADR 0016 amendment, schema 24 repair](../../adr/0016-kyberdash-harness-source-refresh.md); [runbook §2e.1](../../dash/runbook.md#2e1-upgrading-the-store-to-schema-24-duplicate-record-repair); [architecture, store schema list](../../dash/architecture.md#store) | extends-component | KyberDash | 2026-11-10 | archived |
| D8 | [architecture, Run-outcome step memory bound](../../dash/architecture.md#run-outcome-step-memory-bound) | extends-component | KyberDash | 2026-11-10 | archived |
| D9 | [ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md), amended in place; no new ADR | extends-component | KyberDash | 2026-11-10 | archived |
| D10 | [todo: tray does not restart a dead engine](../../todo/kyberdash-tray-supervisor-restart-loop.md), registered in the [todo index](../../todo/README.md) | extends-component | KyberDash | 2026-11-10 | archived |
| D11 | Documentation waiver: the constraint that no task opens the owner's live store governed this run only and leaves no invariant for future engineers; no ADR — ADR waiver: process constraint | extends-component | KyberDash | 2026-11-10 | archived |
| D12 | Documentation waiver: the development mode of this run is transient process; no ADR — ADR waiver: process decision | extends-component | KyberDash | 2026-11-10 | archived |
| D13 | Documentation waiver: the approval record is kept in this archived plan's Decisions table; no ADR — ADR waiver: process decision | extends-component | KyberDash | 2026-11-10 | archived |

**Design harvest**

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1 | [architecture, Record identity and the window-independent ordinal](../../dash/architecture.md#record-identity-and-the-window-independent-ordinal) | extends-component | KyberDash | 2026-11-10 | archived |
| H2 | [ADR 0016, dated amendment 2026-11-10](../../adr/0016-kyberdash-harness-source-refresh.md) | extends-component | KyberDash | 2026-11-10 | archived |
| H3 | [architecture, Run-outcome step memory bound](../../dash/architecture.md#run-outcome-step-memory-bound) | extends-component | KyberDash | 2026-11-10 | archived |
| H4 | [runbook §2e.1, upgrading the store to schema 24](../../dash/runbook.md#2e1-upgrading-the-store-to-schema-24-duplicate-record-repair) | extends-component | KyberDash | 2026-11-10 | archived |
| H5 | [todo: tray does not restart a dead engine](../../todo/kyberdash-tray-supervisor-restart-loop.md) | extends-component | KyberDash | 2026-11-10 | archived |
