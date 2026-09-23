---
id: todo/canon-multi-harness-session-gather
title: Sessions with multi-harness canonical keys gather no records on the store path
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-23
status: draft
---

# Sessions with multi-harness canonical keys gather no records on the store path

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

Surfaced 2026-09-23 while verifying CodeRabbit's round-1 Major on the
[KyberDash compaction-hazard window plan](../archive/plans/2026-09-23-kyberdash-compaction-hazard-window.md)
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
