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

Replaces the eleven per-technology worker agents with three developer agents that differ only in
model strength (`dev-easy`, `dev-medium`, `dev-hard`). Each task carries a difficulty and its
technologies. Everything a technology agent knows moves into that technology's folder (standard,
rules, skills, Arbiter rules), which ships as a template. One docs tool builds the whole prompt for
a task. The Arbiter reads its rules from role and technology folders. `test-dev`,
`sql-database-architect` and `azure-reader` go away; a read-only command-line research agent
replaces the last. The old agents stay selectable until a comparison has been run.

It depends on the technology folder layout planned on
[pull request 625](https://github.com/dpalfery/kyber-weave/pull/625).

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 14 requirements in EARS form. Draft. |
| [Design](design.md) | Evidence from the code, where the evidence is against the idea, the roster, difficulty and the step-up, model profiles, technology folders and templates, the task prompt tool, technology-resolved task gates, Arbiter rules by folder, five phases, 18 decisions and 7 open questions. Draft. |

The task list is not written yet. It follows once the requirements and design are approved.
