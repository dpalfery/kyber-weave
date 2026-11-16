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
technology. Everything a technology agent knows moves into that technology's folder (standard,
rules, skills, Arbiter rules), which ships as a template. One docs tool builds the whole prompt for
a task. The Arbiter reads its rules from role and technology folders. A read-only
`platform-researcher` replaces `azure-reader`, and a platform-neutral `devops` agent replaces
`github-devops`, with a folder per platform (GitHub, Azure DevOps, GitLab, Jira). The old agents
stay selectable for now.

It depends on the technology folder layout planned on
[pull request 625](https://github.com/dpalfery/kyber-weave/pull/625).

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 13 requirements in EARS form. Draft. |
| [Design](design.md) | Evidence from the code, where the evidence is against the idea, the roster, the developer agent's instructions, difficulty and the step-up, technology, role and platform folders, the task prompt tool and its budget, one technology per task, technology-resolved task gates, Arbiter rules by folder, five phases, 23 decisions and 1 open question. Draft. |
| [Tasks](tasks.md) | 44 tasks in five shippable phases plus closeout, test-first. Draft; no machine task list generated. |
