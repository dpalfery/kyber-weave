---
id: archive/todo/kyberdash-transcript-sync-unsettled-payload-drop
title: Drop the unused payload from the transcript-sync unsettled-row SELECT
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# Drop the unused payload from the transcript-sync unsettled-row SELECT

> [!NOTE]
> **Closed 2026-10-20.** The unsettled-row `SELECT` in `syncSessions`
> (`dash/src/sync/transcript-sync.ts`) no longer names `payload`. The delivered
> statement is:
>
> ```sql
> SELECT session_id, harness, transcript_path, transcript_offset, sync_status, settled_at, started, ended
> FROM session
> WHERE sync_status NOT IN ('settled', 'unavailable') OR sync_status IS NULL
> ```
>
> This file's body below previously quoted the pre-change `SELECT` verbatim,
> including `payload` — that quote was corrected to the delivered statement as
> part of this closeout, because the old quote would otherwise misstate the
> code. Nothing else changed: the row set, the predicate, the stamped columns
> and the upserts are exactly as the acceptance criteria required, and sync
> behavior (delta streaming, settlement, timeout, lock arbitration) is
> unchanged. Why the column went: `payload` is the session's whole JSON blob
> (up to 15.3 MB) and the loop never read it, so every pass over the unsettled
> rows paid for a blob it discarded.

The transcript-sync job's unsettled-row SELECT
(`dash/src/sync/transcript-sync.ts`, the `SELECT session_id, harness,
transcript_path, transcript_offset, sync_status, settled_at, started, ended
FROM session WHERE sync_status NOT IN ('settled', 'unavailable') OR sync_status IS
NULL` in `syncSessions`) fetches only the sync job's own columns. `payload`
was dropped from the column list because no consumer in the loop read it.

Non-blocking but not harmless: each unsettled row had been demand-paging its payload
blob for no consumer. The change was a column-list edit only; no behavior changed.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 8,
decision D21 item 3); the audit verified `row.payload` is never read in the loop.

## Acceptance criteria

- [x] The unsettled-row SELECT names no `payload` column.
- [x] Sync behavior (delta streaming, settlement, timeout, lock arbitration) is unchanged
  and the sync tests still pass.
