---
id: adr/0008-kyberdash-single-canonical-store
title: Single Canonical Store; Supersede ADR 0007 D4
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-03
---

# ADR 0008: Single Canonical Store; Supersede ADR 0007 D4

## Status

Accepted, 2026-09-03. Supersedes [ADR 0007](0007-kyberdash-agent-session-analysis-integration.md) decision D4 ("zero-copy dual-database querying" across `canon.db` and the Python pipeline's `sessions.db`). Restores [ADR 0006](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) decision D3 ("there is one canonical model, and it is the span model").

## Context

`canon.db` had no `session` table, so `KyberBridge.getSessionPayload`'s canon branch was unreachable dead code and every read fell through to a hard-coded absolute path pointing at another checkout on one developer's machine.

The TypeScript analysis layer (`analyzeContext`, `rankSchemas`, `buildTimeline`, `compareHarnesses`) had zero non-test consumers; `bridge.ts` reimplemented a cruder comparison in roughly 300 lines of raw SQL instead.

The OTLP content mapping read a single attribute, `gen_ai.prompt`, which none of the harnesses in the corpus emit. All 20,445 stored records carried an empty content map while their compressed raw payloads held the system prompt, message list, workspace rules, skill catalogue and tool definitions.

Because raw payloads were retained, the corpus was repairable in place: a backfill filled 9,318 records with no re-collection.

## Decision

1. **`canon.db` is the only store.** A `session` table holds derived sessions whose payload is produced by the analysis layer over `records`. It is a cache: dropping every row and rebuilding loses nothing.
2. **The Python pipeline's `sessions.db` is no longer read by default.** The loss of its historical corpus was accepted deliberately, on the condition that the new pipeline captures as much or more per session.
3. **Content is modelled as ordered content parts carrying a ground-truth MCP server field and an optional harness-reported token count,** because a flat string per bucket can express neither.
4. **Content is stored once: parts are deflate-compressed and the flat content map is derived from them on read.**
5. **Token counts come from the real `o200k_base` encoder,** and surfaces report which tokenizer actually ran.

## Alternatives Considered

- **Importing `sessions.db` into `canon.db`.** Rejected. It carries a retired schema forward, and the owner accepted the data loss.
- **Keeping the dual-database fallback.** Rejected. Two schemas and the hard-coded path persist indefinitely.
- **Storing content uncompressed alongside the parts.** Rejected. Measured at 166 MB against 40 MB for the same text, which is the shape of the 2.9 GB problem requirement R12.4 exists to prevent.

## Consequences

- One schema governs storage, analysis and serving; the analysis layer is on the critical path at last.
- No hard-coded developer paths remain in the read path.
- The corpus is repairable from retained raw payloads, as demonstrated by the in-place backfill.
- The Python corpus is not migrated and its sessions are lost, as accepted in the decision.
- Storing parts grew the measured store by about 42 percent.
- A schema migration path must now be maintained (version 1 to 2 to 3), because rebuilding a months-old corpus is not always possible.

## Related

- [ADR 0006: KyberDash as a TypeScript Soft Fork with a Merge Zone and an Embedded OTLP Receiver](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md)
- [ADR 0007: KyberDash Agent Session Analysis Integration, Dual-Database Architecture, and Navigation Topology](0007-kyberdash-agent-session-analysis-integration.md)
- [KyberDash architecture](../dash/architecture.md)
- [KyberDash measurable rationale](../reference/kyberdash-rationale.md)
