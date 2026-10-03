---
id: plans/2026-10-02-issue-216-claude-desktop-content-parts
title: "KyberDash: capture content parts for claude-desktop synth spans (#216)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-03
development-mode: test-first
code-refs:
  - ClaudeContentReader
  - loadClaudeCalls
  - ingestProviders
  - matchingTurns
  - synthesizeCall
  - assembleTurnContent
decided-by:
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0014-unclipped-turn-inspection-and-copy-out-protocol
  - rules/honest-unobservability
---

# KyberDash: capture content parts for claude-desktop synth spans (#216)

**Status: Complete, archived 2026-10-03.** Development mode: `test-first`. Hal approved
approve-and-execute on 2026-10-03 through the conductor ("APPROVE AND EXECUTE: Hal approves").
T1–T5 done: `nativeRecordId` pairing on Claude reader turns; `PARSER_CONTRACT_VERSION` bumped
to `4`; dash typecheck/lint/test **4293 passed / 277 files**. Known pre-existing disclose-list
failures (`migration.test.ts` DROP COLUMN; `cursor.test.ts` DATE-ROT) were on the disclose
list for this issue; on this host's full suite run they passed (**0 failed**). Capture-vs-gap
facts harvested into [`docs/dash/architecture.md`](../../dash/architecture.md),
[`docs/dash/telemetry-inventory.md`](../../dash/telemetry-inventory.md), and
[`docs/dash/runbook.md`](../../dash/runbook.md); no new ADR. Archived here per
`KW-DOC-LIFECYCLE-003` before `docs validate . --merge-ready`.

**Discovery provenance.** Kyber-Weave MCP `docs_*` tools were unavailable in this harness, so
documentation discovery used the authorized fallback beginning at
[`docs/README.md`](../../README.md), then
[`docs/dash/architecture.md`](../../dash/architecture.md),
[`docs/dash/telemetry-inventory.md`](../../dash/telemetry-inventory.md),
[`docs/rules/honest-unobservability.md`](../../rules/honest-unobservability.md),
[ADR 0009](../../adr/0009-multi-signal-ingestion-span-shaped-record.md),
[ADR 0014](../../adr/0014-unclipped-turn-inspection-and-copy-out-protocol.md), and the archived
[#184 plan](./2026-09-30-issue-184.md). Code discovery used shell
`codegraph explore` against the local `.codegraph/` index before line-accurate Read/Grep
checks. `git rev-parse --show-toplevel` returned
`/Users/hal/git/cursor/kyber-weave-43`. The audit's live `~/.kyberdash/canon.db` is **not**
present on this host (only an empty `~/.kyberdash/cache/`), so this plan relies on the issue's
recorded live evidence and does not make a new live measurement claim. Live repro session
access remains read-only if/when a store is available.

---

## Problem and goal

**Problem.** For `claude-desktop` sessions, every turn inspector shows empty blocks even though
turn resolution is correct. Live repro recorded on 2026-09-30 (read-only) against session
`claude-desktop:eba7886b-a670-4a96-bf67-f37c86442875` (23 turns):

- `GET /api/kyber/session/<id>/turn/{0,1,2,4,5,6,22}/content` → HTTP 200
- Correct sequential `synth:` span ids and model (`claude-sonnet-5-5`)
- `parts: []`, `assembledText: ''`, all five blocks empty
- `system_prompt` / `tool_definitions` carry honest harness `notMeasurable` reasons; the other
  three blocks are plain empty

[#184](https://github.com/dpalfery/kyber-weave/issues/184) already shipped the numbering
convention, strict resolver, measured header counters, and diagnosable empty state. The
resolver lands on the right span — the spans simply carry no structured content parts.

**Goal.** Prefer capturing structured content parts for `claude-desktop` `synth:` spans when
the on-disk transcript supplies them (conversation text and tool results), so the turn
inspector shows real blocks the way Copilot fixtures already do in
`dash/src/server/kyber-content-route.test.ts`. Where the harness genuinely cannot supply a
bucket (`system_prompt`, `tool_definitions`), keep the honest `notMeasurable` treatment and
#184's empty-state UX. Never fabricate content or estimate tokens from text (#184 Q3;
[`rules/honest-unobservability`](../../rules/honest-unobservability.md)).

---

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | Deliver issue #216 through the PLAN path (not a feature-spec). | Conductor assignment, 2026-10-02; Hal approve-and-execute 2026-10-03 |
| A2 | Use `development-mode: test-first`; no opt-out. | Conductor assignment and architect default; Hal approve-and-execute 2026-10-03 |
| A3 | Prefer capturing parts when provider data supports it; otherwise keep #184 honest empty state and document the limitation. Never fabricate content. | Issue expected outcome + #184 Q3 + honest-unobservability rule; Hal approve-and-execute 2026-10-03 |
| A4 | Scope is exactly #216's ask. Live repro session is read-only. Disclose, never fix, the known host failures `migration.test.ts` (DROP COLUMN) and `cursor.test.ts` (DATE-ROT). | Conductor constraints, 2026-10-02; Hal approve-and-execute 2026-10-03 |
| A5 | User-visible product copy says `kyberdash`. | Branding gate; Hal approve-and-execute 2026-10-03 |
| A6 (Q1) | Capture `conversation_history` / `tool_result_content` from transcripts; keep `system_prompt` / `tool_definitions` as honest `notMeasurable`; document both. Reject document-only "no inspector content" for the whole harness. | Auto-resolved from `ClaudeContentReader`, `PROVIDER_READERS['claude-desktop']`, `READER_UNMEASURABLE`, telemetry inventory; Hal approve-and-execute 2026-10-03 |
| A7 (Q2) | Fix ingest/synth reader pairing so `synth:` records persist `parts`. Reject inspector-only workarounds and fabricating/estimating content from counters. | Issue suspected area + #184 resolver-correct verdict + ADR 0014; Hal approve-and-execute 2026-10-03 |
| A8 (Q3) | Set `ReaderTurn.nativeRecordId` from the assistant message id and match `ParsedProviderCall.turnId`; keep positional fallback when ids are absent. | `loadClaudeCalls` / `matchingTurns` contract vs reader omitting ids; Hal approve-and-execute 2026-10-03 |
| A9 (Q4) | Bump Claude `PARSER_CONTRACT_VERSION` when the emitted record shape/parts pairing changes so `dash refresh` re-synthesizes (3→4). | ADR 0016 / #232 D4 / #180 pattern; Hal approve-and-execute 2026-10-03 |
| A10 (Q5) | Twin-dedupe / OTel transplant remains out of scope unless a RED proves file rows have parts and the OTel twin drops them without transplant. | ADR 0009 D4; #232/#231 own twin collapse; Hal approve-and-execute 2026-10-03 |

---

## Investigation findings

1. **Provider that builds `claude-desktop` records.** Refresh classifies Desktop transcripts as
   harness `claude-desktop` (`dash/src/refresh/registry.ts`). Counters come from
   `loadClaudeCalls` (`dash/src/providers/claude.ts`, also re-exported from
   `dash/src/synth/readers/claude.ts`). Content is supposed to come from
   `ClaudeContentReader` via `PROVIDER_READERS.get('claude-desktop')` → `claudeReader`
   (`dash/src/synth/provider.ts`). `ingestUnit` (`dash/src/refresh/orchestrator.ts`) calls
   `ingestProviders` with `{ calls, filePath, harnessId, sourceKey }`, which runs
   `callsAndTurns` → `matchingTurns` → `synthesizeEnvelopes`.
2. **Parts are attachable today when a `ReaderTurn` is present.** `synthesizeCall` sets
   `content` / `parts` only when `readerTurn` is defined
   (`dash/src/synth/synth.ts`). With `readerTurn === undefined`, the record keeps counters and
   measurability but `parts` is omitted — exactly the live API shape (`parts: []` after store
   round-trip).
3. **The Claude reader already extracts the buckets the inspector needs (except two honest
   gaps).** File header and `readClaudeSession` map `text`/`thinking` →
   `conversation_history` and `tool_result` → `tool_result_content`. `system_prompt` and
   `tool_definitions` are genuinely absent from disk (runtime injection) and must stay
   `notMeasurable` — matching the live repro's two reasoned empty blocks and
   `READER_UNMEASURABLE` for the Claude family.
4. **Pairing gap.** `loadClaudeCalls` stamps `turnId` from Anthropic `message.id`, but
   `ClaudeContentReader.read` yields turns **without** `nativeRecordId`. `matchingTurns` then
   falls back to positional index. Refresh window-slices calls in `source-reader.ts` while the
   Claude reader reads the **entire** file and ignores `dateRange`, so positional pairing can
   attach the wrong turn's parts — or, after #232 collapse asymmetries, leave calls unpaired
   (`readerTurn` undefined → empty parts). Copilot VS Code already keys turns by native request
   id; Claude should do the same (Q3).
5. **Regression coverage gap.** `provider.test.ts` proves Claude reader parts for provider
   `'claude'`, and the #232 `claude-desktop` pair test asserts **one invoke + counters only** —
   it does **not** assert `parts` / `conversation_history`. Copilot fixtures in
   `kyber-content-route.test.ts` already prove the content route when parts exist; there is no
   equivalent `claude-desktop` / `synth:` fixture with non-empty parts.
6. **#232 coordination.** File-only request/response collapse and `PARSER_CONTRACT_VERSION = 3`
   landed under [#232](./2026-10-02-file-synth-turn-dedupe.md). That plan's D3
   fused parts across halves specifically to avoid #216-style loss, but #232 D5 kept #216 out
   of scope. This plan owns the remaining "parts never attached / never asserted" gap.
7. **Honest limitation that remains after a successful capture.** Even with perfect pairing,
   `system_prompt` and `tool_definitions` stay `notMeasurable` unless the owner enables raw
   API-body OTLP logs (`OTEL_LOG_RAW_API_BODIES=1` per telemetry inventory) — not assumed here.
   If a specific transcript line carries no text/tool_result blocks, those turns stay honestly
   empty under the #184 UX.

---

## Smallest change

1. **Reader identity:** When splitting Claude turns, set each `ReaderTurn.nativeRecordId` from
   the assistant usage record's `message.id` (same field `loadClaudeCalls` uses for `turnId`).
2. **Pairing robustness:** Keep `matchingTurns` native-id preference; add a focused contract that
   window-sliced / collapsed `claude-desktop` calls still receive the matching turn's parts
   (not the wrong positional neighbor, not `undefined`).
3. **Optional dateRange filter:** If needed for the RED to stay green without brittle
   positional assumptions, teach `ClaudeContentReader.read` to honor `dateRange` consistently
   with `sliceCallsToWindow` — only when required by the pairing contract; do not invent
   content.
4. **Emit + refresh:** Ensure `synthesizeEnvelopes` / collapse paths preserve fused
   `readerTurn.parts` (already intended by #232 `mergeReaderTurns`). Bump Claude
   `PARSER_CONTRACT_VERSION` from `3` → `4` so existing checkpoints re-synthesize.
5. **Content-route fixture:** Add a `claude-desktop` / `synth:` session fixture with real parts
   alongside the existing Copilot cases so
   `GET /api/kyber/session/:id/turn/:index/content` returns non-empty
   `conversation_history` / `tool_result_content` when stored.
6. **Docs:** Record in dash docs that Claude Desktop/Code file ingest supplies conversation and
   tool-result parts when present; system prompt and tool schemas remain `notMeasurable` from
   transcripts; empty inspector after refresh means the transcript lacked those blocks (honest),
   not a resolver bug.

---

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/synth/readers/claude.test.ts` (exists) | `npm --prefix dash run test -- readers/claude` | Each yielded `ReaderTurn` for an assistant usage group carries `nativeRecordId` equal to that group's `message.id`; parts still bucket conversation + tool results; no `system_prompt` / `tool_definitions` parts fabricated | New assertions fail: turns omit `nativeRecordId` today | Same assertions pass; existing bucket tests unchanged |
| T2 | `dash/src/synth/provider.test.ts` (exists) | `npm --prefix dash run test -- provider.test` | `ingestProviders(['claude-desktop'], …)` over a multi-turn transcript with usage + text yields one `llm.invoke` per model turn (post-#232 collapse) **and** each keeper has non-empty `parts` including `conversation_history` (and `tool_result_content` when the fixture has tool results); unpaired-id / window-slice case does not attach a neighbor's parts | Extend the #232 pair case (and add a multi-turn case): today counters pass while `parts` are absent or unasserted — RED must fail on missing/wrong parts | Same cases pass with parts attached and correctly paired |
| T3 | `dash/src/server/kyber-content-route.test.ts` (exists) | `npm --prefix dash run test -- kyber-content-route` | A `claude-desktop` session whose stored `synth:` spans carry parts returns those parts from `GET …/turn/:index/content` with non-empty `assembledText` for measurable buckets; turns without parts keep #184 honest empty (named session/turn/span) — no fabricated text | New claude-desktop fixture cases fail against empty `parts: []` behavior if store rows lack parts, or pin the happy path once T2's emit shape is the fixture source of truth | Same cases pass; existing Copilot fixtures unchanged |
| T4 | `dash/src/refresh/pipeline.test.ts` (exists) | `npm --prefix dash run test -- pipeline.test` | Claude/`claude-desktop` parser contract bump invalidates checkpoints so refresh re-reads and re-synthesizes (same pattern as #232's 2→3 test) | New/updated assertion fails on version `3` | Passes on bumped version (`4`) |
| T5 | no test task (docs only) | `docs validate` / `docs drift` | Limitation and capture expectations are stated in current dash docs; plan reachable from `<plan-index>` | n/a — explicitly no-test; verification is the documentation gates | Both checks clean; `KW-DOC-LIFECYCLE-001` does not fire |

---

## Tasks

### T1 (RED, skill: `test-dev`) — Claude reader native turn ids

- **Objective:** Lock `ReaderTurn.nativeRecordId` to the assistant message id used as
  `ParsedProviderCall.turnId`.
- **Files/symbols:** `dash/src/synth/readers/claude.test.ts`; behavior of
  `ClaudeContentReader.read` / `splitClaudeTurns` / `readClaudeSession`.
- **Acceptance:** RED output shows missing `nativeRecordId` (or mismatch with `message.id`)
  before implementation.
- **Dependencies:** none.
- **Required skills:** `test-dev`.

### T2 (GREEN, skill: TypeScript under `dash/src/synth/`) — Attach native ids + preserve parts through ingest

- **Objective:** Emit `nativeRecordId` on Claude reader turns; keep `matchingTurns` pairing so
  `claude-desktop` synth records persist `parts` through collapse.
- **Files/symbols:** `dash/src/synth/readers/claude.ts` (`ClaudeContentReader.read`,
  `splitClaudeTurns` as needed); `dash/src/synth/provider.ts` (`matchingTurns` /
  `callsAndTurns` only if a proven gap remains after native ids); do not weaken
  `mergeReaderTurns` fusion from #232.
- **Acceptance:** T1 GREEN; T2 contract rows in `provider.test.ts` GREEN; no fabricated
  `system_prompt` / `tool_definitions` parts.
- **Dependencies:** T1.
- **Required skills:** TypeScript/KyberDash implementation under `dash/src/synth/`.

### T3 (RED, skill: `test-dev`) — Content-route claude-desktop parts contract

- **Objective:** Prove the inspector API surfaces captured parts for `synth:` /
  `claude-desktop` spans the way Copilot fixtures already do.
- **Files/symbols:** `dash/src/server/kyber-content-route.test.ts` (extend; reuse `turn()`
  helper or a claude-desktop-shaped variant with `source: 'codeburn/claude-desktop'`,
  `harness: 'claude-desktop'`, `spanId` under `synth:`).
- **Acceptance:** Happy-path turn returns non-empty measurable blocks; empty-parts turn keeps
  honest empty diagnostics (no fabricated assembled text).
- **Dependencies:** none after Ready (may run in parallel with T1); integrate with T2's emit
  shape before claiming end-to-end.
- **Required skills:** `test-dev`.

### T4 (GREEN, skill: TypeScript under `dash/src/server/` + `dash/src/refresh/`) — Route fixture green + parser contract bump

- **Objective:** Make T3 pass against the real assemble path; bump Claude
  `PARSER_CONTRACT_VERSION` so refresh re-synthesizes historical Desktop sessions.
- **Files/symbols:** `dash/src/server/bridge.ts` only if a proven load bug remains after parts
  exist on records (prefer no bridge change — #184 resolver is correct);
  `dash/src/refresh/registry.ts` (`CLAUDE_PARSER_CONTRACT_VERSION` 3→4);
  `dash/src/refresh/pipeline.test.ts` contract for the bump.
- **Acceptance:** T3 GREEN; pipeline contract GREEN; branding gate still says `kyberdash` for
  any new user-visible copy.
- **Dependencies:** T3; T2 (for real emit shape / version bump rationale).
- **Required skills:** TypeScript/KyberDash implementation under `dash/src/server/` and
  `dash/src/refresh/`.

### T5 (docs, skill: `app-docs-standard`) — Document capture vs honest gaps

- **Objective:** State what Claude Desktop/Code file ingest can and cannot put in the turn
  inspector.
- **Files:** `docs/dash/architecture.md` and/or `docs/dash/telemetry-inventory.md` (Claude /
  content-parts rows); touch `docs/dash/runbook.md` only if operator refresh guidance needs a
  one-line note about re-synth after the contract bump.
- **Acceptance:** Docs say conversation/tool-result parts are expected when the transcript
  carries them; system prompt and tool schemas remain `notMeasurable` from transcripts;
  empty inspector after refresh is honest absence, not a numbering bug; product name
  `kyberdash`.
- **Dependencies:** T2, T4.
- **Required skills:** `app-docs-standard`.

### T6 (review + docs-dev closeout)

- **Objective:** Verify gates, run review council, archive the plan in the finishing PR.
- **Files:** branch diff; this plan and `docs/plans/README.md` during closeout.
- **Acceptance:** Focused and deterministic dash gates pass. Full `npm --prefix dash run test`
  may disclose only the two known pre-existing failures below; any other failure blocks.
  `code-review` approve-quality. Closeout archives the plan per `KW-DOC-LIFECYCLE-003` and runs
  `docs validate . --merge-ready`.
- **Dependencies:** T2, T4, T5.
- **Required skills:** `code-review`; docs-dev closeout.

---

## Dependency graph and MAX_CONCURRENCY

```text
T1 (reader RED) → T2 (synth GREEN) ─┐
                                    ├→ T5 (docs) → T6 (review/archive)
T3 (route RED)  → T4 (route/refresh GREEN) ─┘
```

| Task | Depends on | File scope |
|---|---|---|
| T1 | — | `dash/src/synth/readers/claude.test.ts` |
| T2 | T1 | `dash/src/synth/readers/claude.ts`, `provider.ts` as needed, `provider.test.ts` |
| T3 | — | `dash/src/server/kyber-content-route.test.ts` |
| T4 | T3, T2 | content-route green; `registry.ts` + `pipeline.test.ts` |
| T5 | T2, T4 | dash docs |
| T6 | T2, T4, T5 | gates, review, plan inventory |

**MAX_CONCURRENCY: 2.** T1 and T3 are disjoint and may run together. After both RED contracts
exist, T2 and T4 may run together on disjoint trees (`synth/**` vs `server/**` +
`refresh/registry.ts`); serialize only if T4's fixture must be generated from T2's emit helper.

---

## Risks

| Risk | Mitigation |
|---|---|
| Positional pairing silently attaches the wrong turn's prompt | Native `message.id` pairing (Q3); assert multi-turn distinct parts in T2 |
| Fabricating system/tool schema text to "fill" the inspector | Forbidden (A3); tests assert those buckets stay `notMeasurable` / absent parts |
| Stale store rows keep empty `parts_json` after a code fix | PARSER_CONTRACT_VERSION bump (Q4) + owner `dash refresh`; no live-store rewrite in this issue |
| #232 collapse drops one half's parts again | Preserve/assert `mergeReaderTurns` fusion; T2 covers request/response pair with parts on both halves |
| Window-sliced calls vs full-file turns | Native ids; add dateRange filtering only if RED requires it |
| OTel twin keeps counters but file content never transplanted | Out of scope unless RED proves it (Q5); ADR 0009 D4 already defines transplant |
| Live session unavailable on this host | Fixture-based contracts; issue's 2026-09-30 evidence stands; read-only if a store appears |

---

## Out of scope

- Fabricating or estimating inspector content from token counters (#184 Q3).
- Enabling or assuming `OTEL_LOG_RAW_API_BODIES=1` to obtain system prompts / tool schemas.
- Reworking #184 turn numbering, strict resolver, or empty-state UX (already shipped).
- Cursor twin counter joins (#231), file-only duplicate-row collapse beyond what #232 shipped,
  Compare (#190), OTLP projection debounce (#250).
- Writing to the user's live `~/.kyberdash/canon.db` or claiming a new live measurement on this
  host.
- Fixing `dash/src/refresh/migration.test.ts` (DROP COLUMN) or
  `dash/src/providers/cursor.test.ts` (DATE-ROT).

---

## Verification gates

Focused contract gates:

```bash
npm --prefix dash run test -- readers/claude
npm --prefix dash run test -- provider.test
npm --prefix dash run test -- kyber-content-route
npm --prefix dash run test -- pipeline.test
```

KyberDash gates:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
```

Documentation gates:

```bash
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

Known pre-existing full-suite failures are **disclosed, never fixed in this issue**:

- `dash/src/refresh/migration.test.ts` — SQLite `DROP COLUMN` failure.
- `dash/src/providers/cursor.test.ts` — six-month-cap `DATE-ROT` failure.

---

## Review and docs-dev closeout

- Run the `code-review` council over synth/reader, provider pairing, content-route fixtures,
  refresh contract bump, and docs.
- Confirm the review checks: no fabricated parts; `system_prompt` / `tool_definitions` remain
  honest `notMeasurable` from transcripts; `synth:` spans that have transcript text expose it
  in the inspector; branding uses `kyberdash`.
- No new ADR expected — the work conforms to ADR 0009 (file content onto span-shaped records),
  ADR 0014 (unclipped inspection from stored parts), and the honest-unobservability rule.
  Capture-vs-gap facts harvest into `docs/dash/architecture.md` /
  `docs/dash/telemetry-inventory.md`.
- Closeout (2026-10-03): plan archived under `docs/archive/plans/`; Active Plans row
  moved to Archived; harvest into dash architecture / telemetry-inventory / runbook;
  no ADR. `docs validate . --merge-ready` and `docs drift .` run at closeout.
