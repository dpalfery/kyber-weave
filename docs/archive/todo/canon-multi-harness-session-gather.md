---
id: archive/todo/canon-multi-harness-session-gather
title: Sessions with multi-harness canonical keys gather no records on the store path
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-24
status: superseded
---

# Sessions with multi-harness canonical keys gather no records on the store path

**Status:** Superseded and archived
**Archive Date:** 2026-09-24

Fixed in PR #121. The resolution is recorded below; the narrative is kept as the provenance of
the gather rule.

---

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

Surfaced 2026-09-23 while verifying CodeRabbit's round-1 Major on the
[KyberDash compaction-hazard window plan](../plans/2026-09-23-kyberdash-compaction-hazard-window.md)
(PR #106): the specialists tracing the detector's inputs found that sessions whose canonical
key spans multiple harnesses never get their records gathered on the store path, so finding
derivation and run-outcome derivation silently skip them. The user accepted capturing the
defect as a todo the same day.

## What is known

- The failure is silent: nothing errors. A session with a multi-harness canonical key simply
  never contributes findings or run outcomes.
- Executions and session rows for such a key are stamped with the prefixed id
  `${harness}:${key}` (`dash/src/canon/runs.ts:421`, `dash/src/canon/sessions.ts:310`), but
  the gather step `recordsForSession` filters records by `COALESCE(session_id, trace_id) = ?`
  with no harness dimension (`dash/src/canon/store.ts:1524`). The prefixed id matches no
  record's bare key, so the gather returns nothing.
- `buildFindings` therefore skips those sessions (`dash/src/canon/findings.ts:38-42`), and
  run-outcome derivation is affected the same way (`dash/src/canon/runs.ts:638`).
- Scope: the gap predates PR #106 and lives in the store/upstream gather, not the detector.
  The detector's derivation was corrected and pinned in PR #106 (canonical keys, harness
  prefixing, noncanonical-turn exclusion — commits `4f7efde`, `0c92587`, `0cd3404`) and is
  correct on any record set it is handed.

## What needs deciding

- Gather semantics for harness-qualified keys: match on key plus canonical harness, or a
  canonical gather helper shared with sessions and runs.
- Whether run-outcome derivation shares the fix or is addressed separately.
- Fixture coverage for multi-harness keys end to end — store gather through findings and
  run outcomes.

## Resolution

Decided and delivered in PR #121:

- **Gather semantics: key plus canonical harness, through one identity table.**
  `SessionIdentities` in `dash/src/canon/measurability.ts` assigns the persisted id of every
  canonical-harness share of every session key. `CanonStore.sessionIdentities()` builds it
  from the corpus, and `buildSessions`, `buildRuns` and `buildFindings` all take their ids from
  it. `CanonStore.recordsForShare(key, harness)` gathers one share: the key's records whose
  canonical harness matches, so a share never absorbs its sibling's records.
- **Ids stay as they were, and cannot collide.** A single-harness key keeps the key as its id,
  and a split share is `${harness}:${key}`. The table is built over the whole corpus, so a
  split share whose id a native key already holds is re-prefixed with its harness until it is
  free. The `session` and `execution` tables replace on their id, so two sessions sharing one
  would silently lose a row. Raised by CodeRabbit on PR #121.
- **An id is looked up, never parsed.** The share an execution stands for comes from the
  table: native session ids can contain a colon, and a re-prefixed id no longer has the
  shape a parser would expect.
- **Run outcomes share the fix.** `buildRuns` derives each run's outcome through the same
  gather.
- **The detector names sessions from the same table.** Runs are grouped per harness, so a run
  holds one share of a split key and its records cannot show the split or the id the share was
  given. `buildFindings` passes the table to `detectFindings`, so a compaction hazard names the
  session row that exists.
- **Coverage:** `dash/src/canon/findings.test.ts` covers the store gather through `buildRuns`
  and `buildFindings` to the persisted hazard and each share's run outcome, a split share next
  to a native key that reads like its id, and a single-harness key containing a colon.
  `dash/src/canon/split-identity.test.ts` pins the identity table: native keys, qualification,
  re-prefixing, order independence, raw-harness aliases, and an excluded Gemini record.
