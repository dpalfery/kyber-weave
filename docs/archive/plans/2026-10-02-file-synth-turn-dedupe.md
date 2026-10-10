---
id: plans/2026-10-02-file-synth-turn-dedupe
title: "KyberDash: synthesize one canonical record per turn for file-synthesized turns (#232)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
---

# KyberDash: synthesize one canonical record per turn for file-synthesized turns (#232)

**Status: Complete, archived 2026-10-02.** Approved by orchestrator on 2026-10-01 (Approve-and-execute gate). Assigned to conductor / architect on branch `agy/issue-232-synth-dup-turns`. T1–T5 complete. Archived per KW-DOC-LIFECYCLE-003.
This plan addresses GitHub issue [#232](https://github.com/dpalfery/kyber-weave/issues/232): file-synthesized turns arrive twice per turn (request/response pair).

**Development mode:** `test-first`. Every implementation task defines failing automated tests before touching production logic.

---

## 1. Problem and Goal

### 1.1 Context and Evidence

Issue [#232](https://github.com/dpalfery/kyber-weave/issues/232) is a direct follow-up to [#182](https://github.com/dpalfery/kyber-weave/issues/182) (PR #233: harness fold + ADR 0009 dedupe).

During live store auditing on 2026-09-30 (session key `4dd19692-d68e-4f15-b48c-df913b1d3eec`):
- Every model invocation in a twin session produced **1 OTLP row** (source `claude-code-desktop`, hex span ID) plus **2 file rows** (source `codeburn/claude-desktop`, `synth:` span IDs) carrying **byte-identical token counters** (e.g. `reportedInput 64075`, `output 563`).
- In PR #233, `dedupeTwinTurns` (`dash/src/canon/twin-dedupe.ts`) was implemented to enforce ADR 0009 D4 source precedence ("counters come from the OTel row; content comes from the file row"). When an OTLP mate exists (`otels.length > 0`), `collapseCluster` drops the file rows and transplants their content onto the OTel row.
- **The Defect:** When a session is **file-only** (i.e. no OTLP collector was configured or active during the session, which is standard for Claude Desktop or file-ingested harnesses), `otels.length === 0`. The guard `if (otels.length === 0 || files.length === 0) return;` exits immediately.
- Consequently, in file-only sessions, both file rows survive into all derived surfaces (`buildSessions`, `buildRuns`, `buildFindings`). Every turn, token counter, and cost calculation is counted **twice today**.

### 1.2 The Ask

From issue #232:
> "Ask: synthesize one row per turn at the reader/synth layer (adjacent to #180 tool-call extraction work — coordination, not this PR). Audit only; not fixed."

### 1.3 Goal

1. **Reader/Synth Layer Singularity:** When synthesizing turns from transcript files (specifically `codeburn/claude-desktop` and file-sourced harnesses), emit exactly **one** canonical `llm.invoke` record per model turn, fusing request/response halves into a single record carrying unified content parts and single-counted token counters.
2. **Clean Canonical Store:** Raw records in `records` table (`canon.db`) store 1 row per turn rather than manufacturing duplicate spans with disjoint `synth:` IDs.
3. **File-Only Session Accuracy:** File-only sessions compute accurate turn counts, token totals, and costs matching physical reality, whether OTLP telemetry exists or not.
4. **Defense-in-Depth:** Update `dedupeTwinTurns` in `dash/src/canon/twin-dedupe.ts` so that if any legacy or stray duplicate file rows with identical counters arrive together in a file-only share, they also collapse cleanly to one keeper.
5. **Cache Invalidation:** Bump `PARSER_CONTRACT_VERSION` in `dash/src/refresh/registry.ts` so `dash refresh` automatically re-synthesizes and repairs existing ingested sessions.

---

## 2. Shared Root Cause Analysis

### 2.1 Why Two Rows Are Generated (`dash/src/providers/claude.ts` & `dash/src/synth/readers/claude.ts`)

1. **Transcript Record Structure:** In Claude Desktop / local-agent transcripts, each model turn produces two assistant/message records that both carry the Anthropic `message.usage` block (request setup / tool execution vs. response completion).
2. **Parser Extraction (`loadClaudeCalls`):**
   ```ts
   // dash/src/providers/claude.ts:311-327
   for (const rawLine of lines) {
     const usage = claudeUsageOf(rawLine);
     if (usage === undefined) continue;
     // ...
     const messageId = claudeText(record['uuid']) ?? claudeText(message['id']) ?? `turn-${index}`;
     index += 1;
     // pushes a ParsedProviderCall for EACH line with usage
   }
   ```
   Both lines have `claudeUsageOf(rawLine) !== undefined` with byte-identical counters. Because `record['uuid']` differs on each line, each produces a separate `ParsedProviderCall` with a distinct `deduplicationKey` (`claude:${sessionId}:${messageId}`).
3. **Synthesizer 1:1 Mapping (`dash/src/synth/synth.ts:782-813`):**
   `Synthesizer.synthesize` and `synthesizeEnvelopes` map every `ParsedProviderCall` / envelope to a `CanonicalRecord` (`op: 'llm.invoke'`) under `spanId: synth:<harness>:<session>:<messageId>`. Because the span IDs differ, SQLite's `INSERT OR REPLACE` treats them as two distinct spans.
4. **Derived Layer OTLP Bias (`dash/src/canon/twin-dedupe.ts:203`):**
   ```ts
   // dash/src/canon/twin-dedupe.ts:201-203
   const otels = cluster.filter((record) => !isFileSource(record.source));
   const files = cluster.filter((record) => isFileSource(record.source));
   if (otels.length === 0 || files.length === 0) return;
   ```
   `dedupeTwinTurns` was designed strictly as cross-source (OTel vs File) deduplication. When `otels.length === 0`, it does nothing, letting both file rows pass to the session, run, and scorecard aggregations.

---

## 3. Decision Ledger

| Id | Decision | Options Considered | Recommendation | Rationale | Status |
|---|---|---|---|---|---|
| **D1** | Layer for single-turn synthesis | (a) Ingest/Reader/Synth layer (`dash/src/synth/` & `dash/src/providers/claude.ts`); (b) Derived-layer only in `twin-dedupe.ts`; (c) Both (primary at synth + defense-in-depth in twin-dedupe) | **(c) Both** | Direct ask of #232 is to synthesize 1 row per turn at reader/synth layer so the database never stores duplicate spans. Adding derived-layer support ensures existing data and edge-case feeds remain safe. | **APPROVED** |
| **D2** | Turn matching and pairing criteria | (a) Exact-counter match (`freshInput`, `cacheRead`, `cacheCreation`, `output`, `reportedInput`, `reportedOutput`) within timestamp skew (`TWIN_TURN_MAX_SKEW_MS = 60_000`); (b) Consecutive assistant record pairing in JSONL parser | **(a) Exact-counter match within skew** | Consistent with ADR 0009 D4 and existing #182 twin-dedupe contracts. Protects against arbitrary transcript line interleaving while reliably uniting request/response halves. | **APPROVED** |
| **D3** | Content and parts fusion semantics | (a) Fuse parts using `contentFromParts` (keep all distinct parts from both halves, keeper text first); (b) Keep only response half content | **(a) Fuse parts** | Retains both system prompt / prompt context from request half AND tool execution / reply text from response half (prevents data loss as reported in #216). | **APPROVED** |
| **D4** | Checkpoint and cache invalidation | (a) Bump `PARSER_CONTRACT_VERSION` in `dash/src/refresh/registry.ts`; (b) Require manual cache deletion | **(a) Bump `PARSER_CONTRACT_VERSION`** | Standard KyberDash protocol (ADR 0016, issue #180). Causes `dash refresh` to recognize stale checkpoints and cleanly re-synthesize historical sessions. | **APPROVED** |
| **D5** | Scope Boundary | (a) Strictly issue #232 (exact-counter request/response pairs); (b) Also fix Cursor differing counters (#231) and missing parts (#216) | **(a) Strictly issue #232** | #231 (Cursor differing output counters) and #216 (Claude Desktop content part capture) have distinct failure mechanics and dedicated open issues. #232 coordinates with #180 tool calls and stays bounded. | **APPROVED** |

---

## 4. Test-First Implementation Tasks

### Task T1: File-Only Proximity Cluster Collapse in Derived Dedupe (Canon)

**Objective:** Ensure `dedupeTwinTurns` in `dash/src/canon/twin-dedupe.ts` collapses same-turn duplicate file rows even when no OTLP mate exists (`otels.length === 0`).

- **Failing Test First:**
  - In `dash/src/canon/twin-dedupe.test.ts`:
    Add test case `"collapses duplicate file rows in a file-only session (no OTLP rows)"`.
    Provide two `llm.invoke` records both with `source: 'codeburn/claude-desktop'`, identical token counters (`reportedInput: 64075`, `output: 563`), within 5 seconds of each other, one carrying `{ system_prompt: 'req' }` and one carrying `{ conversation_history: 'res' }`.
    Assert:
    - `out.length === 1`
    - Survivor has single-counted tokens (`reportedInput === 64075`)
    - Survivor's `content` contains both `system_prompt` and `conversation_history`
- **Implementation:**
  - In `dash/src/canon/twin-dedupe.ts`:
    In `collapseCluster(cluster, dropped, transplant)`:
    If `otels.length === 0` and `files.length > 1`:
    Treat the earliest file row as the keeper; all subsequent file rows in the cluster are marked in `dropped`; their parts/content are merged into the keeper via `mergeDonors`.

---

### Task T2: Single-Turn Synthesis in Reader / Synth Layer (Synth)

**Objective:** Synthesize exactly one canonical `llm.invoke` record per turn during file ingestion so that the store receives clean, non-duplicated spans.

- **Failing Test First:**
  - In `dash/src/synth/synth.test.ts`:
    Add test case `"synthesizeEnvelopes collapses request/response pair calls with identical counters into one canonical record"`.
    Pass two envelopes from `codeburn/claude-desktop` with identical token counts and proximate timestamps.
    Assert:
    - Synthesizer emits 1 parent `llm.invoke` record, not 2.
    - Span ID is deterministic.
    - Child `tool.invoke` spans remain properly parented to the single turn span.
  - In `dash/src/synth/provider.test.ts`:
    Verify `callsAndTurns` and `matchingTurns` pair request/response calls with the corresponding `ReaderTurn` without generating duplicate records.
- **Implementation:**
  - In `dash/src/synth/synth.ts` (or `dash/src/synth/provider.ts`):
    Add same-turn deduplication for file-synthesized calls sharing identical token counts and proximity within the session before emitting canonical records.
    Fuse `parts` and `content` across both halves.

---

### Task T3: Claude Transcript Call Extraction Harmonization (Provider)

**Objective:** Align `loadClaudeCalls` (`dash/src/providers/claude.ts`) and `ClaudeContentReader` (`dash/src/synth/readers/claude.ts`) so that assistant usage blocks belonging to the same turn are recognized and grouped.

- **Failing Test First:**
  - In `dash/src/providers/claude.test.ts`:
    Test parsing a Claude Desktop JSONL transcript fixture containing paired request/response lines with identical usage. Assert that `loadClaudeCalls` emits 1 `ParsedProviderCall` per model turn with unified tool sequences and commands.
  - In `dash/src/synth/readers/claude.test.ts`:
    Assert that `claudeReader.read()` yields 1 `ReaderTurn` per turn with both prompt/thinking and tool results intact.
- **Implementation:**
  - In `dash/src/providers/claude.ts`:
    In `loadClaudeCalls`, group contiguous assistant usage entries that share identical usage counters within the turn window.
  - In `dash/src/synth/readers/claude.ts`:
    Ensure `splitClaudeTurns` preserves the boundary of the unified turn.

---

### Task T4: Parser Contract Version Bump and Cache Invalidation (Refresh)

**Objective:** Invalidate stale cached checkpoints so that historical ingested sessions are re-synthesized upon `dash refresh`.

- **Failing Test First:**
  - In `dash/src/refresh/pipeline.test.ts`:
    Test that updating `PARSER_CONTRACT_VERSION` causes `orchestrator` to treat existing `source_checkpoint` rows as stale, re-reading the transcript files and emitting single-turn records.
- **Implementation:**
  - In `dash/src/refresh/registry.ts`:
    Bump `PARSER_CONTRACT_VERSION` for `claude-desktop` (and `claude`).

---

### Task T5: Governance, Plan Inventory, and Verification (Docs & Validation)

**Objective:** Keep documentation and governance in sync.

- Add `2026-10-02-file-synth-turn-dedupe.md` to `docs/plans/README.md` under `## Active Plans`.
- Run `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli -- docs validate .` and confirm zero findings.
- Note that upon PR merge, this plan will be moved to `docs/archive/plans/` to satisfy `KW-DOC-LIFECYCLE-003`.

---

## 5. Non-Negotiables & Constraints

1. **Permanent Rule IDs:** Rule IDs (`compaction-hazard`, `duplicate-tool-call`, etc.) remain permanent and unchanged.
2. **Honest Unobservability:** No fabricated counters or synthetic tokens; token counts must match provider telemetry.
3. **No Ontology Widening:** Use existing document types (`doc-type: plan`) and metric ontology.
4. **Zero Compiler & Lint Warnings:** `TreatWarningsAsErrors` is active across `.NET` and `npm run lint` / `npm run typecheck` across `dash`.
5. **Coordination with Concurrent Issues:**
   - Coordinate with #180 (tool-call extraction): tool calls in `parts` and child spans must remain intact during turn collapse.
   - Do NOT attempt to fix #231 (Cursor differing output counters) or #216 (empty inspector content parts) in this slice.

---

## 6. Verification and Gates

Before claiming work complete, the implementation must pass all local CI gates:

```bash
# TypeScript / Dash Gates
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable

# .NET & Documentation Gates
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli -- docs validate .
```
