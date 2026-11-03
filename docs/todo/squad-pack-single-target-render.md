---
id: todo/squad-pack-single-target-render
title: Let squad pack render a single target to a directory
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-16
---

# Let squad pack render a single target to a directory

`kyber-weave squad pack` renders the whole checked-in corpus in one invocation; it cannot
render a single target to a directory. During the docs two-audience remediation
([plan](../archive/plans/2026-10-15-docs-two-audience-completeness.md)), the Copilot render
total could therefore only be verified by invoking `CopilotRenderer` directly from a throwaway
program. That is a tooling/feature gap in the `squad pack` surface, not a documentation gap: the
workaround measured the renderer, but the CLI offers no supported way to do it.

Add single-target render support to `squad pack` (render one named target to a chosen
directory), so a render total can be verified through the documented command surface instead of
a throwaway harness. Until then, the Copilot render total's canonical verification path remains
the direct renderer invocation recorded in the plan's decision D29 (`Total=123`,
`AgentPrincipals=22 AgentRefs=10 SkillPrincipals=24 SkillResources=67`).

This todo is complete when the documented `squad pack` surface can render a single target to a
directory and the render total is verifiable through it. The evidence recorded here is limited
to what the remediation already gathered; no further investigation has been done.
