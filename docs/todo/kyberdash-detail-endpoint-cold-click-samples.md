---
id: todo/kyberdash-detail-endpoint-cold-click-samples
title: Detail-endpoint cold-click cost, more samples before deciding
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# Detail-endpoint cold-click cost, more samples before deciding

The detail route `GET /api/kyber/session/:id` costs about 3.86 s on a cold click
(single sample, ~97% page-in). The sessions-list work deliberately left the detail
route, its `_clip` behavior, and its ASAD contract intact, so this cost still stands.

Gather more cold-click samples across session sizes before deciding anything: the
payload-split and turns-pagination follow-ups both depend on whether this single
sample reproduces.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 2).

## Acceptance criteria

- Multiple cold-click samples are recorded with corpus shape (row count, payload
  sizes) and eviction method, so a decision can cite them.
