---
id: adr/0016-kyberdash-harness-source-refresh
title: Harness-Source Jobs, Client-Surface Identity, and Checkpointed Local Refresh
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-13
---

# ADR 0016: Harness-Source Jobs, Client-Surface Identity, and Checkpointed Local Refresh

## Status

Accepted, 2026-09-12; harvested as shipped 2026-09-13. Extends [ADR 0008](0008-kyberdash-single-canonical-store.md) and [ADR 0009](0009-multi-signal-ingestion-span-shaped-record.md). The command, schema-11 checkpoints, and split identities now match this decision. Verified behaviour lives in [KyberDash architecture](../dash/architecture.md#local-harness-source-refresh).

## Context

KyberDash fills `canon.db` from local coding-harness history as well as OTLP. Vendor folders, model names, and provider objects are not the same as the surfaces a developer filters on. A Gemini chat store is not Antigravity; Copilot CLI is not Copilot for VS Code; Codex Desktop and Codex CLI share a root and differ by native originator. Collapsing those identities makes Context Doctor filters lie, and a full-corpus reparse on every run does not scale.

The [refresh pipeline plan](../archive/plans/2026-09-06-kyberdash-refresh-pipeline.md) fixed the product contract before the checkpointed, record-windowed scheduler landed. This record keeps those constraints from being renegotiated.

## Decision

1. **Public command.** Local-history ingest is `kyber-weave dash refresh` (`dash refresh` on the KyberDash CLI). It is not a top-level `refresh` command. `--history-weeks` is a positive integer; omission means two weeks (`DEFAULT_HISTORY_WEEKS = 2`). There is no public `--provider` / vendor selector. Usage errors (invalid `--history-weeks`) exit `2` before the store opens; any failed harness job or derivation failure exits `1`; complete success, including absent sources, exits `0`. A dashboard refresh button is not part of this lifecycle.
2. **One logical job per harness source type**, not per vendor, parent dot-folder, or file. Native files or database records are the units of work inside a job.
3. **Client surfaces stay distinct** whenever the source supplies deterministic evidence (`antigravity` / `antigravity-cli` / `antigravity-ide`; Copilot CLI vs editor vs agent; Codex and Claude classified from native originator/entrypoint, with an explicit unclassified bucket when evidence is missing). VS Code distributions of one client remain one harness. A shared store with no client discriminator (today: Kilo's shared runtime DB) stays one honest identity rather than a guessed split.
4. **Gemini is a model/provider identity, not a coding harness.** Canonical records must not use harness `gemini` merely because an Antigravity session used a Gemini model. Network sources such as Vercel Gateway stay off this local-history job set.
5. **Windowing is record-timestamped and UTC-anchored.** Inclusion is `[commandStartedAt - N×7 days, commandStartedAt]`. A chat that crosses the cutoff contributes only in-window records. File mtime is a discovery hint, never a semantic timestamp. Future-dated records are quarantined, not imported.
6. **Reruns are checkpointed, idempotent, and additive.** Schema version **11** adds `source_checkpoint` and `record_provenance`. `CanonStore.commitSourceUnit` writes accepted records, provenance, and the unit checkpoint in one transaction. They do not rewrite or delete `records.raw`. A failed harness job does not cancel other jobs. Successful jobs persist and derive. Derivation failure does not roll back committed raw rows. A source disappearing from discovery never auto-deletes history. After jobs drain, `purgeExpiredContent` applies the 14-day content window ([ADR 0018](0018-kyberdash-content-retention-purge.md)).
7. **Stable identity is split-surface plus native ids**, not array position. OTLP counters remain authoritative; file content fills gaps. Generic OTLP without a deterministic client is unclassified, not merged into a guessed harness.
8. **Implementation stays in the merge zone.** Reuse upstream parser, `DateRange`, cache, and provider discovery through Kyber-owned adapters under `dash/kyber/refresh/**`. That directory is an ADR 0006 contact surface: `isAllowedUpstreamImporter` in `dash/kyber/tools/boundary.ts` already allowlists `kyber/refresh/` alongside `synth/**` and `canon/adapters/**`. Edits to `dash/src/**` are not authorized by this decision.

## Implementation notes (harvest 2026-09-13)

- Gemini is excluded from harness jobs and from `canonicalHarnessId`. `SURVEYED_HARNESSES` still lists `gemini` as an E4 **survey family** for cache-counter vocabulary; that is not a stored harness id. The web harness selector may still show a **Gemini** display label (G4a); it must not query `runs?harness=gemini` as a coding-harness filter.
- `dash/kyber/cli/refresh.ts` re-exports `refreshHarnessSources` (alias `refreshLocalProviders`) and does not walk providers itself.
- Orchestrator `RefreshReport.exitCode` is `0 | 1`. Exit `2` is Commander `invalidArgument` on `--history-weeks`, not a report field.

## Alternatives Considered

- **One job per upstream provider object.** Rejected. Antigravity's three roots and Copilot's source types would collapse in the scheduler even if the UI later tried to split them.
- **Treat Gemini as a harness because Antigravity lives under `~/.gemini`.** Rejected. Path coincidence is not client identity; it already produced false rollups.
- **Infer missing client splits from vendor or model.** Rejected. Unclassified or shared identities are honest; guessed filters are not.
- **UI-triggered refresh before the CLI contract exists.** Rejected. A button without windowing, checkpoints, and per-harness isolation would hide the same full-corpus failure modes.

## Consequences

- Split surfaces persist as distinct harness ids (`copilot-cli`, `cursor-agent`, `claude-unclassified`, and the rest of the registry inventory).
- `normalizeHarnessName` must not collapse those ids. Survey-family maps may still group split surfaces onto a shared E4 cache survey (telemetry vocabulary, not stored identity).
- Schema 11 migrates in place; production stores still need the existing backup path before opening a new binary.
- CLI output must stay free of chat content and raw local paths.

## Related

- [KyberDash architecture](../dash/architecture.md)
- [ADR 0006](../archive/adrs/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) — `dash/kyber/refresh/**` adapter seam
- [ADR 0008](0008-kyberdash-single-canonical-store.md)
- [ADR 0009](0009-multi-signal-ingestion-span-shaped-record.md)
- [ADR 0018](0018-kyberdash-content-retention-purge.md) — 14-day content purge after refresh
- [Plan: KyberDash harness-source refresh pipeline](../archive/plans/2026-09-06-kyberdash-refresh-pipeline.md)
