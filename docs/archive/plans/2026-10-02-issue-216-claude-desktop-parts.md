---
id: plans/2026-10-02-issue-216-claude-desktop-parts
title: "KyberDash: claude-desktop synth spans carry no content parts (issue #216)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-03
development-mode: test-first
code-refs:
  - Synthesizer
  - synthesizeCall
  - ingestProviders
  - matchingTurns
  - ClaudeContentReader
  - loadClaudeCalls
  - assembleTurnContent
decided-by:
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0014-unclipped-turn-inspection-and-copy-out-protocol
  - adr/0016-kyberdash-harness-source-refresh
---

# KyberDash: claude-desktop synth spans carry no content parts (issue #216)

**Status: Complete, archived 2026-10-03.** Approve-and-execute locked Q1=(a)/Q2=(a) (Hal /
conductor, 2026-10-02). Development mode: `test-first`. Target branch:
`hal.hermes.cursor/issue-216-claude-desktop-parts`. Complete: T1–T5; Claude reader
`nativeRecordId` + date-window alignment, orchestrator fallback keeps `filePath`/reader,
`CLAUDE_PARSER_CONTRACT_VERSION` bump to 4, dash docs harvest for capturable vs
notMeasurable buckets. Council 0 product findings; host coverlet `test` gate timeout
disclosed as environmental (900s cap vs ~25m suite), not a product defect — standalone
green evidence cited on the PR. Archived per KW-DOC-LIFECYCLE-003. Follow-up to
[#184](https://github.com/dpalfery/kyber-weave/issues/184); related but scoped-out of
[#232](https://github.com/dpalfery/kyber-weave/issues/232).

**Discovery provenance.** Kyber-Weave MCP `docs_*` tools were unavailable in this
harness — docs discovery started at [`docs/README.md`](../../README.md),
[`docs/dash/architecture.md`](../../dash/architecture.md),
[`docs/dash/telemetry-inventory.md`](../../dash/telemetry-inventory.md),
[`docs/dash/runbook.md`](../../dash/runbook.md), and archived plans
[`2026-09-30-issue-184`](2026-09-30-issue-184.md) /
[`2026-10-02-file-synth-turn-dedupe`](2026-10-02-file-synth-turn-dedupe.md).
Code discovery used shell `codegraph explore` against a freshly initialized local
`.codegraph/` index (authorized by root `AGENTS.md`; pre-init `git status`/`diff`
were empty; init introduced no tracked-file edits), then Read/Grep for line-accurate
citations. `git rev-parse --show-toplevel` =
`/Users/hal/git/cursor/kyber-weave-2`. Live `~/.kyberdash/canon.db` and the cited
Desktop transcript were **not** present on this authoring host (GAPS).

## Problem and goal

**Problem.** For `claude-desktop` sessions, every turn inspector shows empty blocks
even though turn resolution is correct after #184. Live repro (issue body / #184
audit, 2026-09-30):

- Session `claude-desktop:eba7886b-a670-4a96-bf67-f37c86442875` (23 turns).
- `GET /api/kyber/session/<id>/turn/{n}/content` → HTTP 200, sequential `synth:`
  spans, model `claude-sonnet-5-5`, but `parts: []`, `assembledText: ''`.
- `system_prompt` / `tool_definitions` carry honest harness `notMeasurable`
  reasons; the other three blocks are plain empty.

**Goal.** Get structured content parts onto `claude-desktop` `synth:` records for
buckets the transcript actually carries, so the inspector is non-empty for
conversation/tool-result content; keep honest `notMeasurable` for system_prompt /
tool_definitions; document those inherent omissions in dash docs. Never fabricate
content (Q3 on #184: estimates are not measurements). Repair existing empty rows by
bumping `CLAUDE_PARSER_CONTRACT_VERSION` so `dash refresh` re-synthesizes them.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Deliver via a plan (not a spec) on branch `hal.hermes.cursor/issue-216-claude-desktop-parts`. | Conductor / user assignment |
| A2 | Development mode is `test-first` (default; no user opt-out). | User ("Implement test-first") / conductor |
| A3 | Disclose known failing host tests: `dash/src/refresh/migration.test.ts` (DROP COLUMN), `dash/src/providers/cursor.test.ts` six-month-cap (DATE-ROT). Do not fix them in this change. | Conductor |
| A4 | Branding: new `codeburn/*` wire keys need ALLOWED list + reason; user-visible text says kyberdash. No new `codeburn/*` key required (reuse `codeburn/claude-desktop`). | Conductor |
| A5 | PR body starts `Fixes #216`; archive plan before merge (`KW-DOC-LIFECYCLE-003`). | Conductor |
| **Q1→A6** | **Capture path:** ensure `ClaudeContentReader` parts land on `claude-desktop` `synth:` records for buckets the transcript supplies (conversation, tool_result); keep honest `notMeasurable` for system_prompt / tool_definitions; document those inherent omissions in dash docs. Options (b) docs-only empty inspector and (c) partial capture without documenting system/tool omission are **rejected**. | Hal / conductor, 2026-10-02 — Q1=(a) |
| **Q2→A7** | **Bump `CLAUDE_PARSER_CONTRACT_VERSION`** so `dash refresh` re-synthesizes existing empty claude-desktop rows (matches ADR 0016 / #180 / #232 precedent). Options (b) manual re-ingest only and (c) no repair path are **rejected**. | Hal / conductor, 2026-10-02 — Q2=(a); depends on Q1=(a) |
| A8 | Approve-and-execute: finalize this plan Draft → Ready (frontmatter `status: current`; ontology has no `ready` value); Q1=(a)/Q2=(a) stand; Draft ledger removed; implementation may proceed on T1–T5. | Hal approves via conductor, 2026-10-02 |

All ledger questions are resolved; none remain open. Draft decision ledger removed per plan-authoring finalization.

## Investigation findings

Self-gathered (docs MCP unavailable; CodeGraph via shell after `codegraph init .`).

### 1. Which provider builds `claude-desktop` records?

- **Discovery:** `claude` provider (`dash/src/providers/claude.ts`) — `discoverSessions` walks Claude config `projects/` plus Desktop local-agent trees under `getDesktopSessionsDirs()` (`:545–583`), stamping `sourceKind: 'claude-desktop'` and `sourceLabel: 'Claude Desktop'`.
- **Classification:** `classifySessionSource` / `entrypoint === 'claude-desktop'` → harness id `claude-desktop` (`dash/src/refresh/registry.ts:660–665`). Descriptor: `providerName: 'claude'`, `nativeFormat: 'jsonl'` (`registry.ts:178–184`).
- **Counters:** `loadClaudeCalls` (`providers/claude.ts:299+`) — one `ParsedProviderCall` per assistant usage line, with contiguous request/response pair collapse (#232, `isContiguousPair` at `:423`).
- **Refresh path:** `iterateNativeUnits` expands project dirs to per-`.jsonl` units (`source-reader.ts:144–165`), builds counter envelopes without parts (`toEnvelope` `:308–324`), then `ingestUnit` (`orchestrator.ts:350–370`) calls `ingestProviders` with `{ calls, filePath: unit.source.path, harnessId: 'claude-desktop', ... }`.
- **Wire source stamp:** synthesized records use `codeburn/claude-desktop` (existing namespace; no new key required for the approved path).

### 2. Where do `synth:` spans get created, and why do they lack `parts`?

- **Span id:** `synthesizeCall` → `spanIdFor` / `nativeRecordIdentity` (`dash/src/synth/synth.ts:386–454`, `111–135`) → `synth:<harness>:<session>:<nativeRecordId>`.
- **Parts attachment is reader-gated** (`synth.ts:448–449`):

```typescript
content: readerTurn === undefined ? {} : contentFromParts(readerTurn.parts),
...(readerTurn !== undefined ? { parts: readerTurn.parts } : {}),
```

- Without a paired `ReaderTurn`, the record stores counters + empty content and omits `parts`. The content route then returns `parts: []` / `assembledText: ''` (`bridge.ts:1570–1571`, assembly through `:1770`).
- **Pairing:** `ingestProviders` → `callsAndTurns` + `matchingTurns` (`provider.ts:128–187`, `:300–307`). `PROVIDER_READERS` maps `claude-desktop` → `claudeReader` (`provider.ts:105`).
- **Fragile pairing notes (candidate capture defects for Q1=(a) / A6):**
  1. `ClaudeContentReader.read` does **not** set `nativeRecordId` on yielded turns (`readers/claude.ts:461–470`), so `matchingTurns` never uses message-id maps and falls back to **positional** pairing (`provider.ts:167–179`).
  2. Refresh **window-slices calls** (`sliceCallsToWindow`) but the Claude reader **ignores `dateRange`** (interface accepts it at `types.ts:101`; implementation only takes `filePath` at `claude.ts:439`), so positional pairing can attach the wrong turn's content after a partial window — does not by itself explain *all-empty*, but is unsafe.
  3. `ingestUnit`'s fallback when the first ingest returns zero records re-invokes `ingestProviders` with a **bare call array** (`orchestrator.ts:364–369`), which hits `synthesizer.synthesize(loaded)` **without** a reader (`provider.ts:286–288`) → counters-only spans.
  4. `#232` provider test for `claude-desktop` collapse asserts **token counts only**, not parts (`provider.test.ts:368–378`) — capture regressions would not fail that suite.
- **Backfill cannot repair file-synth rows:** `backfillContent` re-derives via OTLP `canonicalParts` (`tools/backfill.ts:84–89`); file-synth `raw` is the `ParsedProviderCall` (`userMessage: ''`, no message body). Re-synthesis from the transcript is required.

### 3. How do other harnesses (copilot fixtures) attach parts?

- `dash/src/server/kyber-content-route.test.ts` seeds a real `CanonStore` with hand-built records whose `parts` arrays are already populated (`turn()` helper ~`:43–63`, harness `copilot`). That suite pins the **content route / assembly** contract, not ingest capture.
- Live Copilot CLI ingest reads context columns from SQLite (`loadCopilotCliCalls` / D6 path in `provider.ts:142–144`) — a different source shape than Claude JSONL.
- The Claude analogue of "attach parts" is: `ClaudeContentReader` → `ReaderTurn.parts` → `synthesizeCall`.

### 4. Can claude-desktop / the source supply structured parts?

| Bucket | Supply? | Evidence |
|---|---|---|
| `conversation_history` | **Yes** (from transcript message text / thinking) | `readClaudeSession` (`readers/claude.ts:271–328`); `claudeReader` tests (`claude.test.ts:31–54`, paired-turn case `:56+`) |
| `tool_result_content` | **Yes** (from `tool_result` blocks) | `claude.ts:329–378`; D5 integration expects tool_result part (`provider.test.ts:317–321`) |
| Tool call child spans | **Yes** (when tool_use present) | Issue #180 path; `synthesizeToolCalls` |
| `system_prompt` | **No** from session files | `measurability.ts:94–95`, `:386–389` — "Claude Code session files do not store the runtime system_prompt"; telemetry inventory Claude row (prefix bytes `not_measurable` without `OTEL_LOG_RAW_API_BODIES=1`) |
| `tool_definitions` | **No** from session files | Same measurability / inventory row |

**Verdict:** The harness is **not** inherently empty for inspector-useful content. Conversation and tool-result parts are capturable. System prompt and tool definitions remain honestly unmeasurable from Desktop JSONL alone. Live all-empty `parts: []` on `synth:` spans is therefore a **capture/pairing/persistence gap** (or unrepaired store), not a proof that Desktop cannot supply parts.

### 5. Smallest change (locked by A6 / A7)

1. RED: end-to-end `claude-desktop` ingest fixture with real conversation + tool_result text → assert `parts` non-empty on the synthesized `llm.invoke` record (and that #232 collapse still fuses parts).
2. GREEN: harden pairing — set `nativeRecordId` from Claude `message.id` / record uuid on `ReaderTurn`; align Claude reader emission with the refresh date window (or match by id so window slicing cannot desync); ensure the orchestrator fallback never strips the reader when a file path exists; fix any defect the RED exposes.
3. Optional content-route case: `synth:` + `codeburn/claude-desktop` record with parts → `/turn/n/content` returns non-empty `assembledText` (route already works; pins the issue's API shape).
4. Bump `CLAUDE_PARSER_CONTRACT_VERSION` past `'3'` (A7).
5. Docs: document that Desktop/CLI file synth can supply conversation/tool_result parts after refresh, while system_prompt/tool_definitions stay `notMeasurable` without raw API body logging — no fabricated filler.

**Not in scope:** fabricating inspector text; estimating tokens from text; changing #184 numbering/resolver; Cursor twin work (#231); new `codeburn/*` keys (reuse `codeburn/claude-desktop`).

## Test contract (`test-first`)

Locked by **Q1=(a) / A6** and **Q2=(a) / A7**. Changing an approved contract returns the plan to Draft for reapproval.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/synth/provider.test.ts` (extend D5 / #232 desktop cases) and/or `dash/src/synth/readers/claude.test.ts` | `npm --prefix dash exec vitest run src/synth/provider.test.ts src/synth/readers/claude.test.ts` | Ingesting a `claude-desktop` `ProviderLoad` whose JSONL has user text + assistant text (+ optional tool_result) yields ≥1 `llm.invoke` with non-empty `parts` including `conversation_history` (and `tool_result_content` when present); #232 pair collapse still emits one invoke and **retains fused parts** | New assertions fail on current pairing/collapse (or pass only if capture already works — then RED is the regression pin) | Same assertions green; no fabricated parts when transcript has none |
| T2 | `dash/src/synth/provider.test.ts` or `dash/src/refresh/pipeline.test.ts` | focused vitest as above / `src/refresh/pipeline.test.ts` | Window-sliced calls still pair with the **same-turn** reader content (native id or date-aligned turns), not an earlier turn's text; bare-array fallback is not taken when `filePath` exists | Fails if positional+full-file desync or fallback strips reader | Passes after pairing/orchestrator fix |
| T3 | `dash/src/server/kyber-content-route.test.ts` | `npm --prefix dash exec vitest run src/server/kyber-content-route.test.ts` | A stored `codeburn/claude-desktop` / `synth:` record with conversation parts returns HTTP 200 with non-empty `parts` and `assembledText` containing that text; a counters-only desktop record still returns honest empty parts (no fabrication) | New case fails only if route regresses (likely passes once T1 data shape exists) | Green; existing copilot cases unchanged |
| T4 | docs only | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `docs drift .` | Dash docs state capturable vs notMeasurable buckets for Claude Desktop file synth; plan inventory row present | N/A | Zero findings |
| T5 | `dash/src/refresh/pipeline.test.ts` | focused vitest | Bumped Claude parser contract version forces re-read of a stale desktop checkpoint | Fails before bump | Passes after bump (pattern from #232 v2→v3 test) |

## Dispatchable tasks

Skills named for conductor mapping — not owning agents.

### T1 — RED: claude-desktop parts-on-synth contract (skill: `test-dev`)

**Objective.** Pin that `ingestProviders(['claude-desktop'], …)` with a contentful Desktop-shaped JSONL produces non-empty `parts` on the collapsed `llm.invoke` record.

**Files / symbols.** `dash/src/synth/provider.test.ts` (`D5 reader integration`, `#232` desktop case); optionally `dash/src/synth/readers/claude.test.ts` (`ClaudeContentReader.read`, `splitClaudeTurns`).

**Acceptance.** RED output captured; assertions require `conversation_history` (and tool_result when fixture includes it); empty-transcript control stays empty.

**Depends on:** A6. **Skills:** `test-dev`.

### T2 — GREEN: pairing + ingest path so parts persist (skill: none / TypeScript implementor)

**Objective.** Make T1 green without fabricating content.

**Files / symbols.** Likely: `dash/src/synth/readers/claude.ts` (`ClaudeContentReader.read` — emit `nativeRecordId`; honor `dateRange` or otherwise align with sliced calls); `dash/src/synth/provider.ts` (`matchingTurns`); `dash/src/refresh/orchestrator.ts` (`ingestUnit` fallback must not drop `filePath`/reader); confirm `synthesizeCall` / `mergeReaderTurns` / `collapseEnvelopeTurns` preserve parts after #232 collapse (`synth.ts`).

**Acceptance.** T1 green; T2 window/fallback cases green; no new `codeburn/*` keys; user-visible strings say kyberdash.

**Depends on:** T1. **Skills:** (implementation; no named skill required beyond repo TypeScript practice).

### T3 — RED→GREEN: content route pin for desktop synth parts (skill: `test-dev`)

**Objective.** Pin the issue's HTTP shape for a desktop synth span that has parts, and for an honest empty desktop span.

**Files / symbols.** `dash/src/server/kyber-content-route.test.ts`; `assembleTurnContent` (`bridge.ts:1494`) only if a route defect appears (unexpected).

**Acceptance.** Focused file green; copilot fixtures unchanged.

**Depends on:** T1 (fixture shape); may run after T2. **Skills:** `test-dev`.

### T4 — Docs: capturable vs inherent empty (skill: `app-docs-standard` / docs-dev)

**Objective.** Record in dash docs that Claude Desktop/CLI file synth can carry conversation/tool_result parts when ingest pairs the reader, while system_prompt/tool_definitions remain not measurable from session files without raw API body logging. Link #216 closeout.

**Files.** `docs/dash/architecture.md` and/or `docs/dash/telemetry-inventory.md` (and runbook only if operator refresh steps change). Plan inventory already updated at Ready time; archive at PR close.

**Acceptance.** `docs validate` / `docs drift` clean; no ontology widening.

**Depends on:** A6; after T2 so claims match behavior. **Skills:** docs authoring per `app-docs-standard`.

### T5 — Parser contract bump (skill: `test-dev` + implementor)

**Objective.** Invalidate stale Claude family checkpoints so owner `dash refresh` re-synthesizes parts onto historical desktop sessions.

**Files / symbols.** `CLAUDE_PARSER_CONTRACT_VERSION` in `dash/src/refresh/registry.ts` (currently `'3'`); `dash/src/refresh/pipeline.test.ts` (#232 version-bump pattern ~`:305`).

**Acceptance.** Stale checkpoint re-read; focused pipeline test green.

**Depends on:** A7, T2. **Skills:** `test-dev`.

### T6 — Verification, review, docs-dev closeout (no-test / gates)

**Objective.** Run dash + docs gates; disclose known host failures; archive plan before merge-ready PR.

**Depends on:** T2–T5. **Skills:** `code-review` at PR; docs-dev for archive.

## Dependency graph and MAX_CONCURRENCY

```
A6/A7 (locked) ──► T1 → T2 → T5 → T6
                     └→ T3 ──────► T6
                          T4 ─────► T6
```

Disjoint file scopes after T1: synth/provider+reader (T2), content-route test (T3), docs (T4) → **MAX_CONCURRENCY: 3** once T1 is red. T5 shares refresh registry with T2's contract concerns — serialize after T2.

## Risks

- **Live store not re-verified here:** authoring host has no `~/.kyberdash/canon.db` and no `eba7886b-…` transcript (GAPS). Implementation should re-check the cited session after refresh, or use a captured fixture derived from a Desktop JSONL with usage + message content.
- **Inherent empty buckets:** even after capture, system_prompt/tool_definitions stay notMeasurable — inspector will not be "full five blocks"; success is non-empty conversation (and tool results when present).
- **Positional pairing + history window:** can silently attach wrong-turn text; id-based pairing is the durable fix.
- **Known failing host tests (disclose, do not fix):** `dash/src/refresh/migration.test.ts` (DROP COLUMN); `dash/src/providers/cursor.test.ts` six-month-cap (DATE-ROT). Same disclosure pattern as #231 / LiteLLM bundle plans.
- **Fallback ingest path:** if left in place without `filePath`, it recreates counters-only desktop rows.

## Out of scope

- Fabricating or estimating inspector content (#184 Q3).
- Changing turn numbering / strict resolver (#184 shipped).
- Cursor twin counter join (#231) and other harness capture gaps.
- Enabling `OTEL_LOG_RAW_API_BODIES` or claiming system_prompt bytes from Desktop files.
- New wire-format `codeburn/*` keys.

## Verification gates

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
dotnet run --project src/KyberWeave.Cli -- docs validate .
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

Disclose the two known failing tests above in review. Archive this plan to
`docs/archive/plans/` and update `<plan-index>` before
`docs validate . --merge-ready` (`KW-DOC-LIFECYCLE-003`). PR body starts with
`Fixes #216`.

## Review and docs-dev closeout

Code-review council over the dash/TS (+ docs) diff. No new ADR expected — capture
extends ADR 0009 (content from file) and ADR 0014 (unclipped inspection when parts
exist); inherent Claude system/tool omission is already measurability/inventory
territory. Closeout: harvest the capturable-vs-notMeasurable sentence into
architecture or telemetry-inventory, archive this plan, confirm the cited session
(or fixture) shows non-empty conversation parts after refresh.

## GAPS

| Gap | Impact |
|---|---|
| Kyber-Weave MCP `docs_*` unavailable | Docs discovery via `docs/README.md` + `docs/dash/*` fallback (labeled above) |
| No live `~/.kyberdash/canon.db` / cited transcript on authoring host | Cannot re-probe `eba7886b-…` here; rely on #184/#216 issue evidence + code path analysis |
| Exact production failure mode among pairing / fallback / unrepaired store | T1 RED distinguishes; A7 covers store repair |
