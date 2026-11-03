---
id: archive/todo/kyberdash-canon-freelist-retention
title: canon.db freelist and retention as a storage concern
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# canon.db freelist and retention as a storage concern

> [!NOTE]
> **Closed 2026-10-20.** Decision: no recurring vacuum cadence, retention
> unchanged — with a new `kyberdash dash vacuum` engine verb as the one-time
> post-migration reclamation mechanism. `--dry-run` reports the reclaimable
> space from SQLite's own `page_count`/`freelist_count`/`page_size` counters,
> opening the store read-only, so the file's size and mtime are unchanged
> afterwards; a real run reports the before size, the after size, the reclaimed
> bytes, and the freelist count it reclaimed. The command holds the same writer
> lock a refresh holds, and it refuses a store it cannot open with the store's
> own "upgrade KyberDash" message. Measured: the live store is 3.33 GB with a
> 286,441-page freelist of 813,331 pages (~35% dead). Retention: the quarantine
> family is evidence retention by design under the existing purge policy —
> recorded numbers, decision "no change". The one-time live vacuum is an
> owner-operated step: stop the engine, run `kyberdash dash vacuum`, start the
> engine — in WAL mode the main file is rewritten only at the final checkpoint,
> so the engine must be stopped, not merely idle.

`canon.db` holds 1.65 GB free in a 3.33 GB file, and the quarantine family is 933 MB —
about 62% of the roughly 1,498 MB live bytes. This is measured orthogonal to list
latency: `VACUUM` halves the file (13.8 s) and leaves the session page set and cold
latency unchanged, because freelist pages are never read.

So this stays a storage concern, not a latency one. Decide what retention or vacuum
cadence, if any, the file wants — session payloads are only about 5% of live bytes,
so the answer is mostly about the quarantine family and content retention, not the
sessions list.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 5,
decision D17).

## Acceptance criteria

- [x] Recorded with the storage numbers above: no recurring cadence, one-time
  post-migration `VACUUM` via the new verb, retention unchanged.
