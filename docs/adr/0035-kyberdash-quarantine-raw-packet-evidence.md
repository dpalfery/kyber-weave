---
id: adr/0035-kyberdash-quarantine-raw-packet-evidence
title: Quarantine Raw-Packet Evidence and Harness-Scoped Wipes; Amend ADR 0032's Per-Harness Quarantine Consequence
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-10
---

# ADR 0035: Quarantine Raw-Packet Evidence and Harness-Scoped Wipes; Amend ADR 0032's Per-Harness Quarantine Consequence

## Status

Accepted, 2026-10-10. Records decisions Q3, B7, and B10 of the
[Claude Code turn-content plan](../archive/plans/2026-10-10-kyberdash-claude-code-turn-content.md).

This record amends one documented consequence of
[ADR 0032](0032-kyberdash-user-initiated-clean.md): "a per-harness wipe cannot scope
quarantine rows (no harness column)". Schema v19 gives `quarantine`, `pending_logs`, and
`quarantined_logs` a nullable `harness` column, so a per-harness wipe now scopes those rows.
ADR 0032's decisions are otherwise unchanged and it is not edited — the same shape as
[ADR 0029](0029-kyberdash-per-bucket-otel-first-content-precedence.md) amending one decision of
ADR 0009. [ADR 0018](0018-kyberdash-content-retention-purge.md) is unchanged: raw packets are
not time-purged, and this record confirms that rather than altering it.

## Context

A quarantined span was filed with its namespaces and a reason, and the row's decoded packet was
thrown away with the record. For most quarantines that is enough — the audit row explains a
drop. For a Claude Code interaction span it is not: that span carries no counters, so it is
quarantined as `non-model span`, and it is the turn's only carrier of the user's prompt. With
the packet deleted, the prompt left with the row and the turn inspector rendered empty even
though the sender had exported text. The user's request was direct — "I should at least be able
to see the packet" — and the quarantine row was the only place to keep it.

Two forces bound the fix. First, honest unobservability: the corpus shows the packet the sender
actually sent, or declares why it cannot, rather than a shape-fallback that looks like data.
Second, the retention posture [ADR 0018](0018-kyberdash-content-retention-purge.md) already
states: assembled inspector text is purged after 14 days, while findings, tokens, timestamps,
and raw payloads survive. A time-based scrub of raw packets would rewrite compressed blobs for
a guarantee the corpus never made.

There was also a wipe gap. [ADR 0032](0032-kyberdash-user-initiated-clean.md) wipes by harness
scope, but the quarantine and log tables had no harness column, so a per-harness wipe could not
reach the Claude prompt text sitting in `quarantine`, `pending_logs`, and `quarantined_logs`.
"Wipe the `claude-code` harness" left that text behind. The user's instruction was that a
per-harness wipe should clean all the data for that harness.

## Decision

1. **A quarantined span keeps the sender's decoded packet.** `quarantine.raw_payload` (schema
   v18) holds the span JSON at the moment it is filed, so a deleted record's evidence is not
   lost. The packet is the whole decoded span, stored verbatim.
2. **One route serves it.** `GET /api/kyber/quarantine/:spanId` returns the audit row plus
   `rawPayload` and `evidenceAvailable`. The quarantine **list** route is unchanged and stays
   light: payloads are fetched one row at a time, never in a page.
3. **The turn view links the packets.** `TurnContentResult` gains `parentSpanId` and
   `rawPacket: { spanAttributesPath, interactionPath? }`. `interactionPath` is present only when
   the parent's quarantine row kept a payload, so the pointer is never a link to nothing. The
   server decides which links exist; the web renders only those ([ADR 0033](0033-kyberdash-surfaces-are-display-layers.md)).
4. **A per-harness wipe scopes the audit rows.** A nullable `harness` column (schema v19) is
   written on `quarantine`, `pending_logs`, and `quarantined_logs` when attribution or the B3
   log recognition names one. `wipeHarnessRows` deletes rows whose `harness` is one of the wiped
   raw names. A NULL harness reads as "unknown", never "matches", so unclaimed rows survive a
   per-harness wipe and are cleared only by the complete wipe.
5. **No time-based scrub of raw packets.** Wiping is the deletion mechanism for raw packets, as
   ADR 0018 already implies. The docs state it plainly: assembled turn text is purged after 14
   days; the raw packet keeps exactly what the sender sent until you wipe the `claude-code`
   harness or the whole database.
6. **Both migrations are presence-guarded.** Each `ADD COLUMN` is a no-op when the column is
   already present, so a store stamped v18 by an off-main build that already added
   `raw_payload` reaches the identical v19 shape instead of failing on a duplicate column.

## Alternatives Considered

- **Time-based scrub of raw packets, matching the 14-day window.** Rejected. ADR 0018's
  Consequences say raw payloads survive past 14 days; a scrub would rewrite compressed `raw`
  blobs to deliver a guarantee the corpus never claimed, and would change an accepted ADR for
  no stated requirement.
- **Serve payloads from the quarantine list.** Rejected. A page of rows would carry N packets,
  which is the weight the list contract exists to avoid; one detail route keeps the list light.
- **Keep the audit row but never the packet.** Rejected. It is exactly the behaviour that left
  the user unable to see the packet that carried the redacted prompt, and it makes the
  interaction span's prompt unrecoverable after the record is deleted.
- **Leave the per-harness wipe unable to scope quarantine.** Rejected. The user's instruction
  is that a per-harness wipe cleans all the data for that harness; leaving prompt text behind
  contradicts it. The harness column is the narrow way to honour it without a full-table scan.
- **Store the full OTLP envelope in `records.raw` instead.** Rejected and out of scope. The
  quarantined span's own packet is the evidence that matters; broadening `raw` would change the
  record model for no defect this record addresses.

## Consequences

- The store holds the sender's decoded packet — including prompt text — verbatim until a wipe.
  This is the retention cost ADR 0018 already accepted, now made visible from the turn view.
- A per-harness wipe scopes only attributed rows. Rows with a NULL harness (attribution named
  none) still need the complete wipe; both surfaces and the docs say so.
- Quarantine rows written before the payload column answer `rawPayload: null,
  evidenceAvailable: false`. That is explicit and never an invented packet.
- `kyber backfill` and `kyber renormalize` do not replay cross-span prompt attachment and do not
  pass `rawPayload`/`harness` for rows they quarantine; those rows read `evidenceAvailable:
  false`. A known limit, out of scope here.
- The quarantine list route and its response shape are unchanged, so existing list consumers
  are unaffected.
- `docs/dash/architecture.md`, `docs/dash/runbook.md`, and
  `docs/dash/telemetry-inventory.md` document the route, the wipe scoping, and the retention
  sentence.

## Related

- [ADR 0032: User-Initiated Clean of the KyberDash Database](0032-kyberdash-user-initiated-clean.md)
  — its per-harness quarantine consequence amended here
- [ADR 0018: Stored Content with a 14-Day Automatic Purge](0018-kyberdash-content-retention-purge.md)
  — the raw-payload retention posture this record confirms and does not change
- [ADR 0009: Multi-Signal Ingestion into a Span-Shaped Canonical Record](0009-multi-signal-ingestion-span-shaped-record.md)
  — why a non-model span is quarantined rather than stored
- [ADR 0014: Unclipped Turn Inspection and Copy-Out](0014-unclipped-turn-inspection-and-copy-out-protocol.md)
  — the inspector this evidence serves
- [ADR 0033: The KyberDash Tray and Web Dashboard Are Display Layers](0033-kyberdash-surfaces-are-display-layers.md)
  — the server decides which links exist; the web renders them
- [KyberDash architecture](../dash/architecture.md) — schema and route contract
- [KyberDash runbook](../dash/runbook.md) — seeing a turn's raw packet
- [Claude Code turn-content plan](../archive/plans/2026-10-10-kyberdash-claude-code-turn-content.md)
  — the defect and decisions this record harvests
