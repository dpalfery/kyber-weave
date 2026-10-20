---
id: todo/kyberdash-records-parts-json-compression-detail-path
title: records.parts_json BLOB compression for the detail and digest path
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# records.parts_json BLOB compression for the detail and digest path

Candidate follow-up for the **detail/digest** path only: compress
`records.parts_json` as a BLOB, mirroring the existing `compressRaw` pattern on
`raw`/`parts` (measured precedent: `records` I/O 79 → 6.1 MB per full refresh at
+330 ms refresh cost).

Explicitly **not** for the list path: compression leaves the JSON-parse CPU floor in
place and contradicts the list goal, which the `session_denorm_idx` covering index
already meets.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 6).

## Acceptance criteria

- The detail/digest compression is adopted or declined with measured I/O and refresh
  numbers, and the list path is untouched either way.
