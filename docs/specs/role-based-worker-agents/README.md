---
id: specs/role-based-worker-agents/index
title: Role-based worker agents specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-11-16
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]
---

# Role-based worker agents specification

**Status:** Draft. Nothing is approved and nothing is built.

Adds three role agents (`server-dev`, `client-dev`, `devops-dev`) beside today's per-technology
worker agents, so the two sets can be compared on the same work. Each task names its technologies;
the worker fetches the matching standards bundle through the docs tool; task gates resolve from the
task's technologies. No current agent is deleted. Retiring the per-technology agents is a later
decision that waits for a measured comparison.

It depends on the stitched standards bundle planned on
[pull request 625](https://github.com/dpalfery/kyber-weave/pull/625).

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 12 requirements in EARS form. Draft. |
| [Design](design.md) | Evidence from the code, where the evidence is against the idea, the roster, task technologies, technology-resolved task gates, the roster switch, 14 decisions and 4 open questions. Draft. |
| [Tasks](tasks.md) | 21 tasks in four shippable phases plus closeout, test-first. Draft; no task file written. |
