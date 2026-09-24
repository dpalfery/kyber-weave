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

- **Gather semantics: key plus canonical harness, through one shared rule.** `harnessSessionId`
  in `dash/src/canon/measurability.ts` is now the only place the `${harness}:${key}` id is
  minted — by `buildSessions`, `buildRuns` and the compaction-hazard detector — and
  `harnessSessionKey` beside it inverts it. `CanonStore.recordsForDerivedSession(sessionId,
  harness)` gathers one harness's share of a key: it tries the id verbatim, then the key the
  prefix was minted from, and keeps only records whose canonical harness matches, so a share
  never absorbs its sibling's records.
- **The harness comes from the execution row, never from parsing the id.** Native session ids
  can contain a colon, so splitting on the first one would misread a bare key as a harness and
  lose its records.
- **Run outcomes share the fix.** `buildRuns` derives each run's outcome through the same
  gather.
- **The detector is told which keys were split.** Runs are grouped per harness, so a run never
  holds every share of a key and the detector cannot see from its records that the key was
  split. `buildFindings` passes those keys as `harnessQualifiedKeys`, so the compaction hazard
  names the `${harness}:${key}` session row that exists.
- **Coverage:** `dash/src/canon/findings.test.ts` covers the store gather through
  `buildRuns` and `buildFindings` to the persisted hazard and each share's run outcome, plus a
  single-harness key containing a colon. `dash/src/canon/split-identity.test.ts` pins the id
  rule and the gather across raw-harness aliases and an excluded Gemini record.
