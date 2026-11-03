---
id: todo/kyberdash-quarantine-harness-index
title: Index quarantine by harness, or leave the scan alone
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-16
---

# Index quarantine by harness, or leave the scan alone

`EXPLAIN QUERY PLAN` against a populated store shows the import-shaped reads already
covered: `records` by `(harness, session_id)` uses `records_by_harness_session_time`, and
`record_provenance` by `(harness_id, source_key)` uses `record_provenance_by_source`. One
read scans:

```
SELECT COUNT(*) FROM quarantine WHERE harness = ?
  -> SCAN quarantine
```

`quarantine` carries only its `span_id` primary key, so a harness-scoped wipe or a count
walks the whole table.

## Why an index was not simply added

`quarantine` is written once per quarantined span, which makes it the busiest insert path on
a machine whose harnesses are not all adapted — one unadapted harness produced roughly 3,100
quarantined rows in half an hour. An index there taxes every write to speed up reads that
only run on a wipe or a panel refresh.

That trade wants a measurement rather than a reflex: instrument the wipe path against a
store with a realistic quarantine and add the index only if the scan shows up.

The neighbouring question belongs with it. Each quarantine row carries a full raw payload, so
the table's cost is dominated by bytes rather than rows. If it is going to be large, a
retention policy — or moving the payload out of the row — is worth more than an index.
