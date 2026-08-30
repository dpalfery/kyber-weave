---
id: archive/specs/kyberdash/index
title: KyberDash specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-08-29
---

# KyberDash specification

Merge a local session-file cost tracker with an OpenTelemetry span-analysis pipeline into one
locally-run product: an embedded OTLP receiver, the deep span analyses, and native desktop and
menu-bar surfaces. The product story this specification implements is
[KyberDash](../../dash/README.md).

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 15 requirements, 72 acceptance criteria in EARS form |
| [Design](design.md) | Architecture, components, data models, error handling, testing strategy, and the nine design decisions with their rejected alternatives |
| [Tasks](tasks.md) | 13 tasks, test-first, each tracing to the criteria it satisfies |

## How to read it

Start with the requirements. Several of them cite a **measurement** rather than a preference —
293 of 307 spans yielding negative fresh input, a 5.8× cost understatement, 25 of 1,009 spans
losing their parent to buffer eviction. Each is a failure that already occurred in the Python
pipeline this specification supersedes, and a reimplementation that drops the requirement
reproduces the failure. They are recorded so a later reader cannot mistake a correctness
constraint for a style choice.

The design's governing idea is that the two source codebases do not have peer data models:
one produces per-call cost records, the other produces spans carrying disjoint token classes
and a cost basis. Every analysis is expressible only over the latter, so the session-file
providers become **span synthesizers** and one analysis layer serves both ingest paths.

## Status

**Archived** — 2026-08-29. Implementation complete: all 13 tasks delivered with the 33
leaf tasks verified green — 481 KyberDash / status-contract / MCP-parity / installer
tests plus 44 merge-boundary and release tests pass, `npm run lint` reports 0 errors, and
`docs validate` and `docs drift` are clean. All four open questions were resolved (listed at
the end of [requirements.md](requirements.md) and [design.md](design.md)); requirements
1–15 were verified against the implemented behaviour before this archive decision.

The durable content survives the archive in the canonical corpus it was migrated into
before archival: the delivered architecture in
[`../../dash/architecture.md`](../../dash/architecture.md), the measured rationale behind
requirements 4, 5 and 6 — and every other quantified constraint — in
[`../../reference/kyberdash-rationale.md`](../../reference/kyberdash-rationale.md), the
foundation decisions in
[ADR 0006](../../adr/0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md), and the
product story in [`../../dash/README.md`](../../dash/README.md).
