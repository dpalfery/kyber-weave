---
id: todo/metric-point-content-digest-identity
title: A metric point's identity is a content digest, so byte-identical points collapse
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# A metric point's identity is a content digest, so byte-identical points collapse

The Factory capture plan's decision **D42** already names this as a follow-up, in its own words:
"That gap is recorded as a follow-up for T20 — a todo". Its risk table names the same finding:
"Two byte-identical decoded metric points collapse into one row". This todo is the reminder the
plan's harvest requires; it does not restate D42, and it adds the one thing D42 leaves to a
reader: what the decision has to settle.

The canon store's `metric_point` table keys a row on `point_key`, a digest of everything the row
carries — its name, kind, unit, aggregation temporality, nanosecond timestamp, value (or count
and sum), the point's attributes, the resource attributes, the scope and the source
(`dash/src/canon/store.ts`, the `point_key` comment and the digest function). The key is `UNIQUE`,
and the write is an upsert that restates the row it already occupies.

Two consequences follow, and neither is pinned by a shipped test:

- **Byte-identical decoded points collapse into one row.** The multiplicity of identical points is
  not preserved, so a batch carrying the same point twice stores one row.
- **A re-delivered point lands on the row it already occupies.** The receiver persists a batch
  before it answers it (`dash/src/otel/receiver.ts`), so a batch KyberDash stored and then failed
  to answer for is delivered again by the exporter's own retry — and the digest makes that
  re-delivery idempotent instead of doubling the rows.

The re-delivery case is the digest's reason to exist, and it is a good one. The cost is that the
table's row count is the count of *distinct* points while the receiver-activity row under
`otlp:metrics` records the points the batch *carried*
(`dash/src/canon/metric-ingest.ts`, `dash/src/otel/service.ts`) — the two numbers differ when a
batch repeats a point, and the difference is the intended one between what arrived and what is
stored. The shipped test writes three distinct points (`dash/src/canon/metric-ingest.test.ts`), so
a later change to the identity would be unobserved.

**What a decision must settle.** Whether the content-digest identity is right as it stands, and
whether a test pins it either way. The options are:

- Keep the digest and pin it. A test that delivers the same point twice and asserts one row, and
  one that delivers two points differing only in their timestamp and asserts two rows, would make
  the identity a stated contract rather than an accident.
- Keep the digest but widen the key so multiplicity is preserved — a row per delivered point,
  keyed on something that distinguishes two deliveries of the same measurement. This trades the
  idempotence the re-delivery case depends on, so it needs the retry story settled with it.
- Change the identity to something else entirely (a sequence, a delivery id). The same retry
  question applies.

**Deliberately not done here.** The identity was not changed, and no test was added. D42 chose the
digest and the governed documents record it as "one row per distinct decoded point"; this todo
carries the one fact that decision does not state — that the collision is unpinned — so the next
reader of that rule knows what it rests on.
