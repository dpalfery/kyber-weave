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

**Status:** Ready. Requirements, design and tasks are approved and the owner gave final approval on 2026-11-16. Phases 1 to 5 are delivered on this branch; closeout has not run.

Replaces the eleven per-technology worker agents with three developer agents that differ only in
model strength (`dev-easy`, `dev-medium`, `dev-hard`). Each task carries a difficulty and its
technology. Everything a technology agent knows moves into that technology's folder (standard,
rules, skills, Arbiter rules), which ships as a template. One docs tool builds the whole prompt for
a task. The Arbiter reads its rules from role and technology folders. A read-only
`platform-researcher` replaces `azure-reader`, and a platform-neutral `devops` agent replaces
`github-devops`, with a folder per platform (GitHub, Azure DevOps, GitLab, Jira). The fourteen old
agents are removed: the comparison this specification planned was not used to decide, and anyone who
wants them installs an older version
([ADR 0088](../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)).

It absorbs the standards plan first drafted on
[pull request 625](https://github.com/dpalfery/kyber-weave/pull/625), which is closed.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 13 requirements in EARS form. Approved. |
| [Design](design.md) | Evidence from the code, where the evidence is against the idea, the roster, the developer agent's instructions, difficulty and the step-up, technology, role and platform folders, the task prompt tool and its budget, one technology per task, technology-resolved task gates, Arbiter rules by folder, five phases, 28 decisions and no open question. Approved. |
| [Tasks](tasks.md) | 48 tasks in five shippable phases plus closeout, test-first. Approved; the machine task list is `tasks.yml`. |
