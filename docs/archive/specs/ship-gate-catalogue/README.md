---
id: specs/ship-gate-catalogue/index
title: Shipped review-gate catalogue specification
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-10-27
documentation-impact: extends-component
impact-components:
  - ReviewCouncil
  - DocGraph
  - KyberSquad
  - CI Pipelines
  - Distribution
---

# Shipped review-gate catalogue specification

**Status:** Ready. Requirements, design and tasks are approved, and the owner approved
execution on 2026-10-27. The work is built on the same branch and pull request (#451), and the
specification is archived there with its build work.

Ship per-technology deterministic checks (format, lint, type-check, build, test) with
Kyber-Weave, so a repository that installs it gets them as editable `review:` configuration
for the technologies it declares, run by the review council, by each developer agent's task
gate, and by a copy-ready CI workflow.

Today an installing repository gets no gates at all, C# included: the product defaults are
empty and `docs init` writes no `review:` section.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | Approved 2026-10-27 (autonomous). 10 requirements in EARS form, non-goals, decisions. |
| [Design](design.md) | Approved 2026-10-27 (autonomous). Embedded per-family catalogue, root detection, `working-directory`, council `include`/`os`, `docs init` review block, `review catalogue`, CI template, vendor-verified argv. |
| [Tasks](tasks.md) | Approved 2026-10-27 (autonomous), test-first; execution approved by the owner 2026-10-27. 16 file-scoped tasks in four parallel waves, a Linux real-toolchain verification job, the final council review, and the docs-dev closeout. |

## Design harvest

The authoritative harvest table (H1–H10: each durable design item, its target component and
doc type) lives in [design.md](design.md#design-harvest), so it has one home.
