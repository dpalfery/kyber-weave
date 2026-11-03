---
id: todo/native-metric-points-stored-but-not-projected
title: Native metric points are stored and nothing projects them
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# Native metric points are stored and nothing projects them

The Factory capture plan's decisions **D31** and **D38** already name this as a follow-up, in
their own words: "The canon projection does NOT consume metrics in this branch (a todo)", and
"nothing projects them yet (a todo)". Its risk table names the same finding: "The metrics are
persisted but nothing projects them". This todo is the reminder the plan's harvest requires; it
does not restate D31, and it adds the question D31 leaves open.

Factory's native OpenTelemetry export carries metrics, and the receiver accepts them. The decoded
points are persisted in the canon store's own `metric_point` table — one row per **distinct**
decoded point, since a point's identity is a content digest
(`dash/src/canon/store.ts`; see the separate todo on that identity) — through the production
sink's `upsertMetrics` and the `ingestMetricBatch` seam
(`dash/src/canon/metric-ingest.ts`).

No projection consumes them. They reach no session, no turn, no cost and no chart. The canon
projection has no metric concept, and the read-back accessor exists for a caller that wants the
rows rather than for a consumer that turns them into anything
(`dash/src/canon/store.ts`, `getMetricPoints`).

That is a deliberate position and it is stated: the native path is a **complementary** second
path, not a replacement, and the metrics it carries are stored so their values are not lost — the
writer's rule is that a sink that cannot persist metrics is refused rather than accepted-and-
dropped (`dash/src/otel/writer.ts`), and an OTLP exporter that got a 200 does not resend the batch.
Storing the values and projecting them later are separate decisions.

**What a decision must settle.** What should consume the stored points, and whether that belongs
in the canon projection or in a separate view.

- Project them in the canon projection, as sessions and turns are projected. The projection
  derives a session's figures from its records; a metric point is a measurement, not a record, so
  this needs a decision about how a point relates to a session and a turn, and about what the
  session's figures become when a metric contradicts a record.
- Build a separate view over the stored points, leaving the canon projection alone. The points
  carry `service.name` as their source and no harness attribution — the source name is never
  harness evidence — so a view would have to attribute them by something other than the source.
- Keep them stored and unprojected, and make that a stated property of the surface rather than an
  absence. The honest-unobservability rule says a reader must not be shown a metric as measured
  when nothing measures it; the converse holds too — the metrics exist, and a reader who looks
  for them should find that they are stored and where.

Whichever is chosen, the metric point's own identity has to be settled first, because a
projection that counts points counts distinct points, and that count differs from the number the
batch carried.

**Deliberately not done here.** The projection was not changed and no view was built. The native
metrics path is authorized as a complementary path and its destination was decided; what consumes
the rows is later work, and this todo is the seed for it.
