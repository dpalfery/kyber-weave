---
id: specs/mcp-configuration-wiring/index
title: MCP configuration wiring specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-10-31
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

**Status:** Ready — approved for execution 2026-10-31 (`AUTONOMOUS_APPROVAL: approve-and-execute`, provenance `autonomous: recommendation accepted`; recorded in [tasks.md](tasks.md#decisions) as AP1). Frontmatter `status` stays `draft` because the ontology's closed vocabulary has no `ready` value; the lifecycle Ready is this line and the [specification index](../README.md) row.

`docs init` detects, per harness in play, whether the MCP servers the repository declares
are wired and launchable, warns without failing the command, and offers a confirmed
repair; a separate, bounded, receipted command (`docs mcp-wire` / `docs mcp-unwire`)
performs the repository-scope remedy. Squad is untouched. The ownership boundary the
remedy relies on is recorded in ADR 0051 before the remedy ships (R8.3).

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | Approved 2026-10-31 (autonomous). Ten requirements in EARS form (R1–R10), non-goals, decisions D1–D18. |
| [Design](design.md) | Approved 2026-10-31 (autonomous). The `mcp.required-servers` declaration, the harness catalog and detection model, the terminal-gated prompt, the two repair verbs and their write bounds, the receipt, and the ADR 0051 boundary. |
| [Tasks](tasks.md) | Approved 2026-10-31 (autonomous), test-first; execution approved 2026-10-31. Eleven tasks: nine build tasks across five parallel waves, the conductor-run whole-change review, and the closeout. |

## Decisions and harvest

The permanent [Decisions](design.md#decisions) tables live in the phase artifacts —
requirements.md holds D1–D18, design.md holds DD1–DD20 (the ADR-number correction is
DD20), and tasks.md holds TK1–TK7 with the approve-and-execute record AP1. The
authoritative [Design harvest](design.md#design-harvest) table (H1–H7) likewise lives in
[design.md](design.md); this index adds no decisions of its own. Durable content migrates
to canonical documentation at closeout, before the archive move (task 11).
