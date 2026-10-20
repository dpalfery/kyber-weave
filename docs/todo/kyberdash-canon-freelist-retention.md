---
id: todo/kyberdash-canon-freelist-retention
title: canon.db freelist and retention as a storage concern
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# canon.db freelist and retention as a storage concern

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

- A retention or vacuum decision is recorded (cadence, trigger, or an explicit
  decision to leave the file as is) with the storage numbers that justify it.
