---
id: archive/specs/mcp-configuration-wiring/index
title: MCP configuration wiring specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-11-01
documentation-impact: extends-component
impact-components:
  - DocGraph
  - Distribution
keywords:
  - MCP wiring
  - specification index
  - harness configuration
---

# MCP configuration wiring specification

**Status:** Archived
**Archive Date:** 2026-11-01

Delivered and closed out 2026-11-01: the implementation ships on the same branch, and every
requirement traces to delivered work verified against the code and its tests (see
[tasks](tasks.md)). The council's re-review of the remediated change returned `APPROVE`, with
the reserved-path `NEEDS_HUMAN` verdict that task 8 predicted and accepted (TK3), because
`.kyber-weave/kyber-weave.yml` is on `review.policy.always-human`. The durable harvest H1–H7
lives in canonical documentation — see [Closeout mapping](#closeout-mapping). Live acceptance
remains the owner's check and is `[NOT LIVE-VERIFIED]` (design DG5, TG2).

`docs init` detects, per harness in play, whether the MCP servers the repository declares
are wired and launchable, warns without failing the command, and offers a confirmed
repair; a separate, bounded, receipted command (`docs mcp-wire` / `docs mcp-unwire`)
performs the repository-scope remedy. Squad is untouched. The ownership boundary the
remedy relies on is recorded in ADR 0052 before the remedy ships (R8.3).

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | Delivered 2026-11-01. Ten requirements in EARS form (R1–R10), non-goals, decisions D1–D19. |
| [Design](design.md) | Delivered 2026-11-01. The `mcp.required-servers` declaration, the harness catalog and detection model, the terminal-gated prompt, the two repair verbs and their write bounds, the receipt, and the ADR 0052 boundary. |
| [Tasks](tasks.md) | Delivered 2026-11-01, test-first; execution approved 2026-10-31. Eleven tasks: nine build tasks across five parallel waves, the conductor-run whole-change review, and the closeout. |

## Decisions

One decision belongs to this index alone; the phase decisions live in the artifacts that own
them (requirements D1–D19, design DD1–DD20, tasks TK1–TK8 with the execution approval AP1),
preserved intact.

| ID | Question | Answer / decision | Status |
|---|---|---|---|
| CO-1 | How is this specification closed out? | Archived 2026-11-01 with the implementation on the same branch: every requirement verified against the shipped code and its tests, durable content harvested to canonical documentation per [Closeout mapping](#closeout-mapping), phase decisions D1–D19, DD1–DD20, TK1–TK8 and AP1 preserved in their own artifacts. | Closed |

## Design harvest

The authoritative harvest table (H1–H7: each durable design item, its target component and
doc type) lives in [design.md](design.md#design-harvest), so it has one home. This index
carries the same ids so its own closeout stands alone:

| ID | Design item | Target component and doc type |
|---|---|---|
| H1 | The per-harness shape catalog — files, keys, in-play markers, writability, substitution semantics; the ZCode row | DocGraph · runbook |
| H2 | The `mcp.required-servers` declaration schema, scaffold default, and the always-human escalation consequence | DocGraph · reference |
| H3 | The `docs init` check step — `KW-MCP-WIRE-001..004`, severities, unchanged exit code, `--no-mcp-check` | DocGraph · CI Pipelines · onboarding and reference |
| H4 | The prompt and non-interactive contract — terminal-gated, decline-on-redirect, no consent flag | DocGraph · reference |
| H5 | `docs mcp-wire` / `docs mcp-unwire` — target selection, insertion without re-serialization, receipt and drift-refusing removal, dry run, exit codes | DocGraph · runbook and reference |
| H6 | ADR 0052 — the repository-scope write exception versus ADR 0026 | DocGraph · adr |
| H7 | The docs-init / Squad split — Squad owns droid-level visibility, the docs branch owns harness-level wiring | KyberSquad · architecture |

## Closeout mapping

Harvest H1–H7 migrated into canonical documentation at `extends-component` scale, components
DocGraph and Distribution, with the implementation as the truth where it diverged from the
design. **ADR disposition: [ADR 0052](../../../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md)**
(design DD15, DD20; task 5) — no ADR waiver. Both merge-ready checks exit 0 after the archive
move.

| ID | Canonical destination |
|---|---|
| CO-1 | Documentation waiver — the closeout record itself, which has no destination outside this document; the durable ids it points to are mapped individually below. |
| H1 | [MCP runbook](../../../docgraph/mcp-runbook.md) — the shape catalog, the ZCode row, and the writable/snippet-only table |
| H2 | [Configuration: MCP servers](../../../configuration.md#mcp-servers), [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring) |
| H3 | [Install](../../../install.md) — what an adopting repository's `docs init` now checks and offers; [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring), [CLI command surface](../../../cli-command-surface.md), [Rule reference](../../../ci-pipelines/rule-reference.md) |
| H4 | [MCP runbook: the prompt and no terminal](../../../docgraph/mcp-runbook.md#the-prompt-and-what-happens-without-a-terminal), [CLI command surface](../../../cli-command-surface.md) |
| H5 | [MCP runbook: `docs mcp-wire`](../../../docgraph/mcp-runbook.md#docs-mcp-wire), [MCP runbook: `docs mcp-unwire`](../../../docgraph/mcp-runbook.md#docs-mcp-unwire), [CLI command surface](../../../cli-command-surface.md) |
| H6 | [ADR 0052](../../../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md), [ADR index](../../../adr/README.md) |
| H7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md) |
