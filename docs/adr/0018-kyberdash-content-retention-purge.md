---
id: adr/0018-kyberdash-content-retention-purge
title: Stored Content with a 14-Day Automatic Purge
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-13
---

# ADR 0018: Stored Content with a 14-Day Automatic Purge

## Status

Accepted, 2026-09-13. Extends [ADR 0008](0008-kyberdash-single-canonical-store.md) and [ADR 0014](0014-unclipped-turn-inspection-and-copy-out-protocol.md). Records spine decision D18 (store content; purge after 14 days; never hashes-only).

## Context

[ADR 0014](0014-unclipped-turn-inspection-and-copy-out-protocol.md) requires unclipped plaintext in the Context Inspector and a 14-day rolling retention window so that local `canon.db` does not accumulate unbounded prompt text. The spine plan answered Q3 the same way: persist content, then purge it; do not invent a hashes-only store.

The dedicated `kyber purge-content` CLI named in ADR 0014 was not shipped. Retention had to land without racing the schema-11 checkpoint migration (`store.ts` / `source-state.ts`) and without deleting `records.raw`.

## Decision

1. **Content stays on the record for 14 days.** `CONTENT_RETENTION_DAYS` is 14. Newer `content_json` / `parts_json` remain available to the inspector.
2. **Automatic purge after local refresh.** `purgeExpiredContent` runs at the end of `refreshHarnessSources` (after harness jobs drain, before derived-session rebuild). The pass stamps metadata `content_retention_days` and `content_purged_through`.
3. **Empty content in place; never delete `records.raw`.** Rows stay. `setContent` writes `'{}'` for `content_json` and NULL `parts_json`. A SQL NULL in `content_json` would break `toRecord` JSON parse, so the empty object is the purge sentinel. Compressed `records.raw` is untouched (ADR 0008).
4. **No schema bump for retention.** The purge uses existing columns and metadata keys. Checkpoint/provenance remain schema 11.
5. **Hashes-only storage is rejected.** Inspection and copy-out still require plaintext inside the window.

## Alternatives Considered

- **Hashes-only after ingest.** Rejected. Destroys the inspector ADR 0014 exists for.
- **Standalone `kyber purge-content` as the only path.** Deferred. Operators currently get the window by running `dash refresh`; an explicit purge command remains unshipped.
- **NULL `content_json` as the purged form.** Rejected. `toRecord` JSON-parses that column; NULL would fail reads.

## Consequences

- Refresh is the operational retention trigger documented in the [runbook](../dash/runbook.md).
- `kyber build` does not itself purge content.
- Findings, tokens, timestamps, and raw payloads survive past 14 days; assembled inspector text does not.

## Related

- [ADR 0008: Single Canonical Store](0008-kyberdash-single-canonical-store.md)
- [ADR 0014: Unclipped Turn Inspection and Copy-Out](0014-unclipped-turn-inspection-and-copy-out-protocol.md)
- [ADR 0016: Harness-Source Refresh](0016-kyberdash-harness-source-refresh.md)
- [KyberDash architecture](../dash/architecture.md)
- [KyberDash runbook](../dash/runbook.md)
