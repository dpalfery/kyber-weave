---
id: adr/0020-kyberdash-one-time-fork
title: KyberDash as a One-Time Fork, and the Engine Language Re-Decided
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-18
---

# ADR 0020: KyberDash as a One-Time Fork, and the Engine Language Re-Decided

## Status

Accepted, 2026-09-18. It replaces ADR 0006, *KyberDash as a TypeScript Soft Fork with a Merge
Zone and an Embedded OTLP Receiver*, which is now archived at
[`archive/adrs/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md`](../archive/adrs/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md).
ADR 0006 is named here rather than in `supersedes` because an archived record is outside the
corpus and its id no longer resolves.

This record also supersedes decision 8 of
[ADR 0016](0016-kyberdash-harness-source-refresh.md) ("Implementation stays in the merge
zone"). Everything else in ADR 0016 stands.

## Context

ADR 0006 made KyberDash a **soft fork** of `getagentseal/codeburn`. The fork was vendored at
`dash/` with `git subtree`, tracked through a `codeburn` remote, and kept mergeable by a
merge zone. Inside that zone, `dash/kyber/**` was KyberDash's; everything else was upstream's
conflict surface, read-only where unshipped. A test pinned the zone
(`MergeBoundaryTests`), an import rule enforced it (`boundary.ts`), and CodeQL and Semgrep
excluded upstream directories because their alerts were not actionable here.

Three things changed:

- **The product diverged.** KyberDash now centres on context troubleshooting, and cost is a
  [secondary figure](../rules/secondary-cost-display.md). Upstream CodeBurn centres on spend.
  Its changes land mostly in the surfaces KyberDash is deleting: the spend commands, quota
  tracking, the Swift menu bar, the Electron app and the Ink spend dashboard.
- **The merge zone cost more than it protected.** No workflow built or shipped the native
  surfaces it kept mergeable. The menu bar `kyberdash menubar` installed was upstream's own
  signed app. The one piece of upstream worth keeping, the provider session parsers, changes
  independently of everything the zone constrained.
- **The release path no longer depends on it.** The SEA build that once looked blocked by the
  vendored ESM dependencies was solved without touching upstream code, so mergeability is not
  buying a release capability either.

The KyberDash context surfaces specification (`docs/archive/specs/kyberdash-context-surfaces/`)
therefore takes the current code as a one-time fork.

## Decision

1. **`dash/` is a one-time fork, and first-party code from here on.** Its upstream baseline
   is `getagentseal/codeburn` at commit `93b7b9a7da02ee77c80ac54264b87358121c5cf7`,
   imported as squash commit `0677783e` and merged in `3ffad26a` on 2026-09-10, together with
   every KyberDash change since.
   - No further upstream change is merged. The `codeburn` remote, the subtree workflow, the
     merge zone, its import rule and test, and the vendored-path scan exclusions are retired.
   - Any file under `dash/` is edited on its merits, under the repository's gates.
   - AgentSeal's MIT copyright and permission notice is kept in `dash/LICENSE` and
     `dash/THIRD_PARTY_NOTICES.md`, and ships with every distributed binary.
   - The cost-isolation rule that shared a file with the merge-zone rule (Decision D9: no
     cost-shaped types on diagnostic contracts) was never about the fork. It stays, in
     `cost-isolation.ts`.

2. **KyberDash embeds its own OTLP receiver on the standard port 4318** (restated from
   ADR 0006). No Aspire dashboard, container runtime or collector is required. Aspire-mediated
   ingest remains an optional source for existing corpora, and spans whose parent was evicted
   are grouped by attribute rather than ancestry. Both JSON and protobuf are decoded, so
   hand-rolled collectors and standard SDK exporters work unchanged.

3. **There is one canonical model, and it is span-shaped** (restated from ADR 0006, with
   [ADR 0009](0009-multi-signal-ingestion-span-shaped-record.md)'s amendment folded in).
   - There is one record per model call, populated from any OTLP signal. Logs enrich the
     span-shaped record rather than creating a parallel one. Session-file providers are span
     synthesizers.
   - No analysis knows which ingest path its data arrived by.
   - Token classes are stored disjointly so the reported-input identity is checkable, and
     cost figures carry their basis.
   - Each source declares per-metric measurability, so an absent figure is never rendered
     as zero.

4. **Distribution stays on the repository's one install path** (restated from ADR 0006).
   `scripts/install.sh` places self-contained Node SEA binaries for the five stable runtime
   identifiers. The package is renamed `kyberdash` but is not published to npm.

5. **The engine stays TypeScript, for current reasons.** ADR 0006 rejected a C# core
   because a cross-language fork cannot be merged. That reason is gone, so the question is
   decided again here.
   - **The parsers are the asset.** They are 46 modules and roughly 21,700 lines, each
     encoding a harness's on-disk format, worked out by hand and field-tested against real
     installs. Porting them buys no capability and resets that testing.
   - **The analysis layer is TypeScript,** with parity tests that pin it to the retired
     Python pipeline's results. A port would have to re-earn that parity.
   - **Every surface reads the engine through one seam,** the REST API under
     `/api/kyber/*`. The tray, the web dashboard and the context report all consume that
     API and not the implementation, so moving the engine to .NET later changes none of
     them.

   Revisit this decision if a .NET component needs the same analyses in-process, or if the
   Node toolchain keeps blocking the local release loop that the .NET toolchain already
   passes.

## Alternatives Considered

- **Keep tracking upstream.** Rejected. The products have diverged, so upstream's changes
  arrive mostly in code KyberDash is deleting, while the zone forbids edits the product now
  needs, such as deleting upstream directories and changing upstream command wiring.
- **Port the engine to C# now.** Rejected for now, on the grounds in decision 5. It is not
  ruled out, and the REST seam is what keeps it possible.
- **Keep ADR 0006 current and supersede only its first decision.** Rejected. That would leave
  a current record whose title and first decision describe a relationship that no longer
  exists. Restating the three surviving decisions costs less than every reader who would
  otherwise misread it.
- **Rewrite history to drop the subtree import.** Rejected. History is preserved as it is,
  and the fork's baseline is identified by commit above.

## Consequences

- Provider format changes are KyberDash's to track; no stream of upstream fixes arrives. The
  parsers' tests and `kyberdash doctor` are how a broken parser is noticed.
- All of `dash/` is linted and scanned by CodeQL and Semgrep like the rest of the repository.
- Upstream-only features are deleted instead of preserved for mergeability. The KyberDash
  context surfaces specification lists them.
- Existing clones should run `git remote remove codeburn`. Nothing in the repository needs
  that remote any more.

## Related

- [KyberDash architecture](../dash/architecture.md)
- [KyberDash measurable rationale](../reference/kyberdash-rationale.md)
- [ADR 0009](0009-multi-signal-ingestion-span-shaped-record.md): multi-signal ingestion into
  the span-shaped record
- [ADR 0016](0016-kyberdash-harness-source-refresh.md): harness-source refresh, whose
  decision 8 this record supersedes
- [Secondary cost display](../rules/secondary-cost-display.md)
