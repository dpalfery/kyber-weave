---
id: todo/otel-nested-attribute-values-not-identity-stripped
title: A nested attribute value is not identity-stripped
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# A nested attribute value is not identity-stripped

The OTLP receiver strips account identity at the decode seam, before anything is stored. It does
so for a span's attributes, a span's resource attributes, a log's attributes, a log's resource
attributes, a metric's resource attributes and a metric point's own attributes
(`dash/src/otel/receiver.ts`, the `stripIdentityAttributes` calls at each of those seams;
`dash/src/otel/identity.ts`).

The stripping works on the **keys** of an attribute map. An attribute whose value is a nested
key-value list is decoded into a nested map
(`dash/src/otel/receiver.ts`, `attributesFromJson` on a `kvlistValue`, and `pbKeyValues` on the
protobuf shape), and the nested map's own keys are never examined: the outer map is filtered by
key, and the nested map is a value that is kept whole. An exporter that puts `user.email` or
`organization.id` inside a key-value-list attribute therefore keeps that identity in the stored
row, where the same key at the top level would have been stripped.

This is pre-existing and it applies to every signal the receiver decodes, not to one harness. It
became visible in this delivery because a metric point's own attributes were found to be reaching
the store unstripped and were fixed at the top level (the metric point's attributes are now
stripped, `dash/src/otel/receiver.ts`, the `dataPointFromJson` seam), which left the nested shape
as the remaining case.

**What a decision must settle.** Whether nested values should be stripped too, and what that costs.

- Strip recursively, at every depth. The cost is a walk over every nested value on every decoded
  span, log and metric point — a hot path — and a decision about how deep "nested" goes: a list
  of lists, a map inside a list, an arbitrarily deep structure.
- Strip one level, which covers the key-value-list shape OTLP defines, and leave deeper nesting
  alone. That is bounded and probably covers what exporters actually send, but it is a line drawn
  by convenience rather than by a rule.
- Leave nested values alone and say so in the governed documents. That is only defensible if the
  documents state that identity is stripped from the top-level keys of an attribute map and that a
  nested value is kept verbatim, so a reader does not infer a guarantee that does not exist.

Whichever is chosen, the honest-unobservability rule applies to the documentation either way: the
guarantee the receiver makes is the guarantee it must state.

**Deliberately not done here.** The stripping was not changed. The receiver's decode seam is the
hot path for every signal in the corpus, and the nested walk is a decision with a performance
consequence that belongs with whoever owns the receiver's cost, not with a Factory delivery. This
todo records the remaining case so the top-level guarantee is not read as a total one.
