---
id: archive/todo/kyberdash-detail-endpoint-cold-click-samples
title: Detail-endpoint cold-click cost, more samples before deciding
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# Detail-endpoint cold-click cost, more samples before deciding

> [!NOTE]
> **Closed 2026-10-20.** The sampling is done, and its conclusion is that the
> 3.86 s premise **did not reproduce**: the detail path's real cold cost is
> 191 ms for the largest session (p90 8.5 ms, median 1.7 ms). The multi-second
> stalls are the engine's ~24.5 s event-loop block, not the detail route — so
> there is no detail-route decision for the payload-split and turns-pagination
> follow-ups to wait on; that work proceeds (or stands down) on its own
> measurements, while the stall itself belongs to the event-loop investigation,
> not to this route.

The detail route `GET /api/kyber/session/:id` had cost about 3.86 s on a single cold-click
sample (~97% page-in). The sessions-list work deliberately left the detail
route, its `_clip` behavior, and its ASAD contract intact, so that cost still stood,
unmeasured.

More cold-click samples were gathered across session sizes, with corpus shape and eviction
method, before deciding anything — and the single sample did not reproduce. The finding
above is the disposition: no detail-route change follows from this todo.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 2).

## Acceptance criteria

- [x] Multiple cold-click samples are recorded with corpus shape (row count, payload
  sizes) and eviction method, and the decision they support is recorded above: the
  premise did not reproduce; the stalls are event-loop block, not detail-route cost.
