---
id: plans/2026-10-04-pr-276-review-findings
title: "KyberDash: PR #276 review findings (pairing invert, Claude sessionId fuse, docs)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-04
development-mode: test-first
code-refs:
  - matchingTurns
  - ContentReader
  - cursorReader
  - ClaudeContentReader
  - splitClaudeTurns
  - loadClaudeCalls
  - ingestProviders
decided-by:
  - adr/0009-multi-signal-ingestion-span-shaped-record
  - adr/0014-unclipped-turn-inspection-and-copy-out-protocol
---

# KyberDash: PR #276 review findings (pairing invert, Claude sessionId fuse, docs)

**Status: complete, delivered 2026-10-04.** Development mode: `test-first`. Target
branch: `hal.hermes.cursor/issue-216-claude-desktop-parts`. Escalated from
bug-crusher on three [PR #276](https://github.com/dpalfery/kyber-weave/pull/276)
review findings. Complete: T1–T6; every review thread resolved. Fixing commits —
F1 `5d7fbe5` (invert to `positionalPairingUnsafe`), F2 `13d2b22` (Claude reader
sessionId stem fuse), F3 `e303e65` (architecture + telemetry-inventory). Archived
per KW-DOC-LIFECYCLE-003. No ADR — behavior restore + doc alignment under ADR 0009
/ ADR 0014.

**Approve-and-execute.** Conductor returned explicit human approval 2026-10-04
("Fix these 3" + locked A1–A5). Frontmatter was `status: current` during execution
(ontology has no `ready` value); now `complete`. Draft ledger was removed before
T1–T6.

**Discovery provenance.** Kyber-Weave MCP `docs_*` tools were **unavailable** in
this harness (no MCP docs namespace). Docs discovery fell back to
[`docs/README.md`](../../README.md), [`docs/dash/architecture.md`](../../dash/architecture.md),
[`docs/dash/telemetry-inventory.md`](../../dash/telemetry-inventory.md), and archived
plans [`2026-10-02-issue-216-claude-desktop-parts`](2026-10-02-issue-216-claude-desktop-parts.md)
/ [`2026-10-01-pr-233-review-follow-up`](2026-10-01-pr-233-review-follow-up.md).
Code discovery used shell `codegraph explore` against the existing
`.codegraph/` index, then Read/Grep for line-accurate citations.
`git rev-parse --show-toplevel` = `/Users/hal/git/cursor/kyber-weave-43`.

## Problem and goal

**Problem.** Three verified defects remain on the #216 / #276 branch:

1. **CRITICAL — pairing.** `positionalPairingSafe` defaults off; only Claude opts
   in. Commit `b2c64a9` also dropped the id-miss → positional fallback that
   pre-gate `matchingTurns` used when `!hasNativeIds`. Codex stamps `turnId`
   (`currentTurnId`) while `codexReader` / `piReader` never emit
   `nativeRecordId`, so id lookup fails and there is no positional rescue →
   empty parts (`parts` / inspector empty). Cursor correctly must not pair
   positionally (filtered turn list).
2. **sessionId fuse.** `parseLineUsageInfo` leaves `sessionId` undefined when
   the JSONL line omits it; `isMatchingTurnUsage` only compares when both sides
   are defined. `loadClaudeCalls` substitutes the file stem
   (`providers/claude.ts:324`) and `isContiguousPair` compares strictly →
   mixed-sessionId transcripts: `loadClaudeCalls.length === 2` but
   `splitClaudeTurns.length === 1`.
3. **Docs drift (depends on F1).** Architecture and telemetry-inventory still
   describe Claude-only `positionalPairingSafe` / Cursor-never-positional in the
   pre-invert framing.

**Goal.** Land F1→F2→F3 (or F1∥F2 then F3) test-first, one commit each, restoring
Codex/Pi (and default) positional pairing while Cursor opts out; align Claude
reader fuse with the parser; update both dash docs to the invert contract.

## Approved decisions

| Id | Decision | Provenance |
|---|---|---|
| A1 | **F1 approach = invert.** Replace `positionalPairingSafe` with `readonly positionalPairingUnsafe?: boolean` on `ContentReader`; only `cursorReader` sets `true`. Default pairing is positional (id-less **and** id-map miss fall back to `turns[index]` unless Unsafe). Remove Claude's Safe opt-in. Preserve Cursor non-positional pin. | Human-locked preferred; "Fix these 3" |
| A2 | **F2 = stem substitution in `parseLineUsageInfo`.** Thread the transcript file stem (same as `loadClaudeCalls`) so fuse `sessionId` matches `isContiguousPair`. | Human-locked |
| A3 | **F3 = update `architecture.md` AND `telemetry-inventory.md`** to the invert contract; include the sentence **"Cursor is never paired positionally."** After F1 lands. | Human-locked |
| A4 | **One commit per finding**; F3 after F1; no rebase/force-push; push to existing branch; no GitHub PR comments. | Human |
| A5 | **`development-mode: test-first`.** Disclose (never fix) host failures: `migration.test.ts` DROP COLUMN; `cursor.test.ts` six-month-cap DATE-ROT. User-visible text: kyberdash. | Human / conductor |
| A6 | **Approve and execute.** Finalize Draft → Ready (frontmatter `status: current`); A1–A5 stand; Draft ledger removed; implementation may proceed on T1–T6. | Conductor / human, 2026-10-04 — "Fix these 3" + locked preferred approaches |

**NO_QUESTIONS.** All material decisions were human-locked and approved; Draft
decision ledger removed per plan-authoring finalization.

## Investigation findings

Self-gathered (docs MCP unavailable; CodeGraph via shell).

### F1 — pairing invert

- **Current contract** (`dash/src/synth/readers/types.ts:107`,
  `provider.ts:164–186`, `claude.ts:511`): `positionalPairingSafe?: boolean`;
  Claude sets `true`; `matchingTurns(..., reader?.positionalPairingSafe === true)`.
  Id-less calls pair by index only when Safe; **id-bearing calls use
  `turnsById.get` only** — no miss fallback (regression vs pre-`b2c64a9`).
- **Pre-`b2c64a9` behavior** (`312559f` era):
  `turnsById.get(call.turnId) ?? (hasNativeIds ? undefined : positional)`.
  Codex/Pi (no reader native ids) recovered via positional on miss.
- **Codex:** parser always stamps `turnId: currentTurnId`
  (`providers/codex.ts:1169`, `:1316`); `codexReader` yields turns **without**
  `nativeRecordId` → map miss → empty parts after the gate.
- **Pi:** provider emits no `turnId`; reader has no `nativeRecordId` → id-less
  path needs default positional.
- **Cursor pin:** `provider.test.ts:1002` — unidentified Cursor call must not
  receive another request's parts; `cursorReader` must set Unsafe.
- **Target `matchingTurns` shape (A1):**

  ```ts
  const byId = call.turnId !== undefined ? turnsById.get(call.turnId) : undefined
  const turn = byId ?? (positionalPairingUnsafe ? undefined : turns[index])
  ```

  Call site: `matchingTurns(calls, turns, reader?.positionalPairingUnsafe === true)`.

### F2 — sessionId fuse

- **Parser** (`loadClaudeCalls`): `sessionId = claudeText(record['sessionId']) ?? fileStem`
  (`providers/claude.ts:309–324`); `isContiguousPair` requires
  `prev.sessionId === next.sessionId` (`:436`).
- **Reader** (`parseLineUsageInfo`): `sessionId: claudeText(record['sessionId'])`
  only (`readers/claude.ts:68`); `isMatchingTurnUsage` skips compare when either
  side is undefined (`:88–90`) → false fuse.
- **Fix:** `parseLineUsageInfo(rawLine, fileStem)` (or equivalent) applies the
  same `?? fileStem` substitution; `splitClaudeTurns(lines, fileStem)` threads
  stem from `ClaudeContentReader.read` (basename of `filePath`). Existing
  length-parity tests (`claude.test.ts` `#276` model-gap / three-line) stay;
  add mixed-sessionId length assertion.

### F3 — docs

- Sites: `docs/dash/architecture.md` (~239–241) and
  `docs/dash/telemetry-inventory.md` (~80–85).
- After F1: default is positional unless `positionalPairingUnsafe`; Claude is
  not a special Safe opt-in; **Cursor is never paired positionally.**

## Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (F1 RED) | `dash/src/synth/provider.test.ts` (new Codex case; keep Cursor pin) | `npm --prefix dash exec vitest run src/synth/provider.test.ts` | Codex rollout → `ingestProviders` yields call+turn pair with **non-empty** `parts` (or conversation content). Cursor unidentified call still has no stolen `instruction_context`. | New Codex test **fails** (empty/missing parts) under current Safe-default-off + no miss fallback. Cursor pin still passes. | — |
| T2 (F1 GREEN) | same + `types.ts` / `provider.ts` / `cursor.ts` / `claude.ts` | same + `npm --prefix dash run typecheck` | Invert flag; Codex parts green; Cursor pin green; Claude mixed-id `#276` tests still green. | — | Codex parts non-empty; Cursor pin; typecheck clean; no `positionalPairingSafe` symbol left. |
| T3 (F2 RED) | `dash/src/synth/readers/claude.test.ts` | `npm --prefix dash exec vitest run src/synth/readers/claude.test.ts` | Mixed sessionId transcript: `loadClaudeCalls(path).length === splitClaudeTurns(lines, stem).length` (expect 2). | New assertion **fails** (turns length 1, calls length 2). | — |
| T4 (F2 GREEN) | same + `parseLineUsageInfo` / `splitClaudeTurns` | same | Fuse matches parser stem rule. | — | Length parity passes; prior `#276` fuse tests still pass. |
| T5 (F3) | docs only (no-test) | `/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `docs drift .` (build first if needed) | Prose matches invert contract; **"Cursor is never paired positionally."** in both files. | Manual read shows mismatch before edit. | Both docs updated; validate/drift zero findings. |

## Tasks

### T1 — RED: Codex positional pairing + Cursor pin (F1) — skill: `test-dev` — COMPLETE

**Objective.** Prove Codex rollout ingest attaches reader parts; keep Cursor
non-positional regression.

**Files / symbols.** `dash/src/synth/provider.test.ts` (new it); existing
`does not positionally pair an unidentified Cursor call…` (~`:1002`).

**Acceptance.** Codex RED fails for empty parts; Cursor pin still green.
Commit policy: do **not** commit RED alone — RED+GREEN land in the **F1
commit** after T2 (test-first discipline inside the finding commit).

**Depends on:** A1. **Skills:** `test-dev`.

### T2 — GREEN: invert to `positionalPairingUnsafe` (F1) — implementor — COMPLETE

**Objective.** Restore default index pairing; Cursor opts out.

**Files / symbols.**

- `dash/src/synth/readers/types.ts` — rename/replace Safe →
  `positionalPairingUnsafe?: boolean`; update remarks.
- `dash/src/synth/provider.ts` — `matchingTurns` + call site; comments.
- `dash/src/synth/readers/cursor.ts` — `cursorReader.positionalPairingUnsafe = true`.
- `dash/src/synth/readers/claude.ts` — remove `positionalPairingSafe = true`.
- Grep-clean any Safe references in dash tests/comments.

**Acceptance.** T1 Codex GREEN; Cursor pin GREEN; Claude `#276` mixed-id /
positional tests GREEN; `npm --prefix dash run typecheck` / `lint` clean for
touched scope.

**Commit 1 (F1):** single commit covering T1+T2. Message focuses on why
(restore non-Cursor positional pairing after Safe gate regression).

**Depends on:** T1. **Skills:** (dash TypeScript practice; no named skill).

### T3 — RED: mixed sessionId length parity (F2) — skill: `test-dev` — COMPLETE

**Objective.** Pin `loadClaudeCalls` vs `splitClaudeTurns` length on a transcript
where some usage lines omit `sessionId` and others set a different id than the
file stem.

**Files / symbols.** `dash/src/synth/readers/claude.test.ts` (new it).

**Acceptance.** RED fails with turns.length === 1, calls.length === 2.

**Depends on:** A2; may start in parallel with T1 if file lock on
`claude.test.ts` only. **Skills:** `test-dev`.

### T4 — GREEN: stem in `parseLineUsageInfo` (F2) — implementor — COMPLETE

**Objective.** Mirror `providers/claude.ts:324` in the reader fuse path.

**Files / symbols.** `dash/src/synth/readers/claude.ts` —
`parseLineUsageInfo`, `splitClaudeTurns`, `ClaudeContentReader.read` (pass
`basename(filePath, extname(filePath))`); update direct `splitClaudeTurns`
callers/tests to pass stem when exercising fuse.

**Acceptance.** T3 GREEN; existing `#276` fuse tests still pass.

**Commit 2 (F2):** T3+T4 only.

**Depends on:** T3. Prefer after T2 if both touch `claude.ts` (T2 only removes
the Safe property — low conflict; parallel OK with care). **Skills:**
(dash TypeScript).

### T5 — Docs align to invert contract (F3) — skill: `app-docs-standard` — COMPLETE

**Objective.** Update both dash docs; no application code.

**Files.** `docs/dash/architecture.md` (Claude Desktop/CLI file-synth pairing
paragraph); `docs/dash/telemetry-inventory.md` (pairing bullets). Required
phrase: **Cursor is never paired positionally.** Replace Safe/opt-in Claude
framing with: positional by default unless `positionalPairingUnsafe`.

**Acceptance.** Prose matches A1; `docs validate` + `docs drift` zero findings.

**Commit 3 (F3):** docs only, after Commit 1.

**Depends on:** T2 (F1 landed). **Skills:** `app-docs-standard`.

### T6 — Push, gates, review closeout — no-test — COMPLETE

**Objective.** Push three commits (no force); run dash gates; disclose known
failures; do **not** post GitHub review comments. Plan archive is this closeout
(KW-DOC-LIFECYCLE-003): complete and move to `docs/archive/plans/`.

**Gates.**

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test   # disclose migration DROP COLUMN + cursor DATE-ROT
/Users/hal/.dotnet/dotnet restore KyberWeave.sln   # if needed
/Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release --no-restore
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
/Users/hal/.dotnet/dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

**Depends on:** T2, T4, T5. **Skills:** `code-review` at end of run; docs-dev
for eventual plan archive.

## Dependency graph and MAX_CONCURRENCY

```
A1–A5 (locked)
  ├─► T1 → T2 ──commit F1──► T5 ──commit F3──► T6
  └─► T3 → T4 ──commit F2──────────────► T6
```

Recommended schedule: **F1 then F2 then F3** (safest on shared `claude.ts`),
or **F1 ∥ F2 then F3** when T2's only `claude.ts` edit is deleting
`positionalPairingSafe`.

**MAX_CONCURRENCY: 2** (T1/T2 vs T3/T4). T5 serial after T2. Commits strictly
ordered F1 → F2 → F3 on the branch (F2 may land before F3 even if started in
parallel; F3 must not precede F1).

## Risks

- **Id-miss positional restore:** Claude `turnId` miss again falls back to
  index (pre-`b2c64a9`). Acceptable under A1; Cursor exempt via Unsafe.
  Re-run Claude mixed-id and windowed `#216` / `#276` provider tests.
- **Codex turn boundary alignment:** positional pairs by index; parser turn
  count must match reader flush count for the fixture — use a minimal one- or
  two-turn rollout aligned with `codexReader` `token_count` boundaries.
- **Known host failures (disclose, never fix):**
  `dash/src/canon/migration.test.ts` (or refresh migration) DROP COLUMN;
  `dash/src/providers/cursor.test.ts` six-month-cap DATE-ROT.
- **Docs MCP gap:** validate/drift via CLI only in this environment.

## Out of scope

- Fixing the two known failing host tests above.
- Setting Safe on Codex+Pi (rejected alternative).
- GitHub PR review comment replies.
- Rebase / force-push.
- New ADR (behavior restore + doc alignment under existing ADR 0009/0014).
- Parser contract version bump (not required for these three findings).

## Verification gates

See T6. Focused RED/GREEN commands in the Test contract table. Full
`npm --prefix dash run test` before push; disclose pre-existing failures only.

## Review and docs-dev closeout

End-of-run `code-review` council over the three commits. No ADR expected.
**Closeout complete 2026-10-04:** plan archived to `docs/archive/plans/` and the
Active Plans row cleared (`KW-DOC-LIFECYCLE-003`).

## GAPS

| Gap | Impact |
|---|---|
| Kyber-Weave MCP `docs_*` unavailable | Docs via `docs/README.md` + `docs/dash/*` fallback |
| Live Codex rollout / Desktop transcript not re-probed on this host | F1 RED uses in-repo fixture pattern from `codex.test.ts` / provider tests |
