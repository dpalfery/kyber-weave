---
id: adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver
title: KyberDash as a TypeScript Soft Fork with a Merge Zone and an Embedded OTLP Receiver
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-08-29
---

# ADR 0006: KyberDash as a TypeScript Soft Fork with a Merge Zone and an Embedded OTLP Receiver

## Status

Accepted

## Context

KyberDash merges two working codebases. `getagentseal/codeburn` (MIT, TypeScript) reads the
session files that 41 agent tools write to disk and ships the surfaces — an Ink terminal
dashboard, a React web dashboard, an Electron application, a Swift menu-bar application, and an
MCP server — but has no OpenTelemetry support. `agent-session-analysis-dashboard` (Python)
analyses OpenTelemetry GenAI spans and holds the correctness work — disjoint token classes with
per-harness semantics, cost basis that travels with every figure, tool-schema cost ranking,
context composition, quarantine — but reads no session files, ships no native surfaces, and
routes every live ingest through a separately installed .NET Aspire dashboard.

The correctness work is not optional polish. Each of its requirements is backed by a measured
failure in the Python pipeline — negative fresh input on 293 of 307 spans from the pi/Copilot
convention inversion, a 5.8× cost understatement from rate-table scoping, 25 of 1,009 spans
losing a parent to Aspire's ring buffer, 2.9 GB for 37,623 uncompressed spans — all recorded
in [KyberDash measurable rationale](../reference/kyberdash-rationale.md).

Two foundational questions had to be settled before any implementation, and both had
plausible-looking answers that were wrong.

**Which codebase is the host?** The repository is a .NET/C# solution, and KyberDash had a
reserved C# catalog row (`src/KyberWeave.Dash`). But a C# core would mean hand-porting 41
provider parsers and keeping them in sync against upstream churn with no cross-language
three-way merge. Requirement 14 — "the fork stays mergeable" — would be unsatisfiable.

**Is an external collector acceptable?** The Python pipeline treats a user-run Aspire
dashboard as its OTLP endpoint and buffer, then pulls spans back with a CLI subprocess. That is
a hard prerequisite for every user, and the dashboard is a ring buffer whose eviction is a
measured data-loss class. The Python pipeline's response was to stop grouping by ancestry and
group by attribute instead — a correct workaround for a problem that owning the receiver
removes outright.

A third question was settled once the first two were: the two source codebases do not have
peer data models. codeburn's `ParsedProviderCall` is a per-call cost record; the Python
pipeline's canonical span carries disjoint token classes, a cost basis, content mapped onto
canonical keys, and a voted harness attribution. Every analysis KyberDash must deliver is
expressed over span structure, so a session-file provider becomes a **span synthesizer** and
one analysis layer serves both ingest paths.

## Decision

1. **KyberDash is a TypeScript soft fork of `getagentseal/codeburn`, vendored with `git
   subtree` at top-level `dash/`.** KyberDash code lives only under `dash/kyber/**` — the
   merge zone — and consumes upstream's output rather than modifying its internals. Upstream
   directories are the conflict surface: read-only where a surface is unshipped, extended only
   at the boundary where KyberDash ships it. Deleting an unshipped upstream directory
   (`dash/windows/**`, `dash/gnome/**`) is forbidden because it manufactures a conflict on
   every future merge. The four deliberate edits inside upstream directories — the status
   contract and its wiring in `dash/src/menubar-json.ts`, `dash/src/usage-aggregator.ts`,
   `dash/app/electron/cli.ts`, and `dash/mac/.../CodeburnCLI.swift` — are recorded with their
   reasons so a future conflict arrives with rationale attached.

2. **KyberDash embeds its own OTLP receiver on the standard port 4318.** No Aspire dashboard,
   no container runtime, no collector is required. The Aspire-mediated ingest remains as an
   optional source for existing corpora, and spans whose parent was already evicted are
   grouped by attribute rather than ancestry. Decoding both JSON and protobuf keeps both the
   existing hand-rolled collectors and standard SDK exporters working unchanged.

3. **There is one canonical model, and it is the span model.** Session-file providers are span
   synthesizers; no analysis knows or asks which ingest path its data arrived by. Token
   classes are stored disjointly so the reported-input identity is checkable, cost figures
   carry their basis, and each source declares per-metric measurability so absent is never
   rendered as zero.

4. **Distribution stays on the repository's one install path** (`scripts/install.sh`),
   shipping self-contained Node SEA binaries for the five stable runtime identifiers. The
   upstream npm distribution path is removed.

## Alternatives Considered

- **A C# core matching the reserved catalog row.** Rejected. Tracking upstream *is* a
  three-way merge, and there is no cross-language form of one, so Requirement 14.1 would be
  unsatisfiable and 41 provider parsers would become ours to keep against vendor churn.
- **Keeping Aspire as the collector.** Rejected. It is a hard prerequisite for every user and
  its ring buffer is a measured data-loss class.
- **Two models with a translation layer.** Rejected. Every analysis is expressed over span
  structure, so the translation layer would have to grow into the span model anyway.
- **Running analyses only on OTLP data and leaving file data at totals.** Rejected. It makes
  the file path a second-class citizen and violates the one-data-path requirement.
- **Editing upstream's parser directly.** Rejected. It makes the mergeable-fork requirement
  unachievable within two upstream releases.
- **Publishing to npm.** Rejected by the maintainer in favour of one install path.
- **Migrating the Python pipeline's 2.9 GB derived store.** Rejected. The corpus is
  reconstructible from existing span exports and live OTLP; migration would carry an old
  schema into the new design for no gain.
- **Deleting unshipped upstream surfaces.** Rejected. Deleting an upstream directory
  manufactures a conflict on every future merge and buys a directory listing.

## Consequences

- Upstream provider coverage keeps arriving through `git subtree` merges instead of hand
  ports; the merge boundary is pinned by `tests/KyberWeave.Tests/MergeBoundaryTests.cs`.
- The `dash/kyber/**` merge zone stays small and free of upstream conflicts by construction;
  anything that needs upstream to behave differently is adapted at the boundary.
- Users get the Python pipeline's analysis rigor with zero external infrastructure, and the
  ring-buffer eviction class is eliminated rather than worked around.
- The Python pipeline is retired once the content-free parity gate of Requirement 15
  authorizes it, and its measured rationale has been migrated to
  [KyberDash measurable rationale](../reference/kyberdash-rationale.md) so the retirement loses
  nothing.

## Related

- [KyberDash architecture](../dash/architecture.md)
- [KyberDash measurable rationale](../reference/kyberdash-rationale.md)
- [KyberDash product story](../dash/README.md)
- [Merge-zone rule — `dash/kyber/README.md`](../../dash/kyber/README.md)