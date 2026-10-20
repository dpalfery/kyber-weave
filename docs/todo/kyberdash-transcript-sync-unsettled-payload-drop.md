---
id: todo/kyberdash-transcript-sync-unsettled-payload-drop
title: Drop the unused payload from the transcript-sync unsettled-row SELECT
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# Drop the unused payload from the transcript-sync unsettled-row SELECT

The transcript-sync job's unsettled-row SELECT
(`dash/src/sync/transcript-sync.ts`, the `SELECT session_id, harness,
transcript_path, transcript_offset, sync_status, settled_at, started, ended, payload
FROM session WHERE sync_status NOT IN ('settled', 'unavailable') OR sync_status IS
NULL` in `syncSessions`) fetches every unsettled row's `payload` and never reads it
afterwards. Dropping the column from the SELECT removes an unnecessary payload fetch
from every sync pass over unsettled rows.

Non-blocking but not harmless: each unsettled row currently demand-pages its payload
blob for no consumer. The change is a column-list edit only; no behavior may change.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 8,
decision D21 item 3); the audit verified `row.payload` is never read in the loop.

## Acceptance criteria

- The unsettled-row SELECT names no `payload` column.
- Sync behavior (delta streaming, settlement, timeout, lock arbitration) is unchanged
  and the sync tests still pass.
