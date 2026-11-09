---
id: todo/docs-dev-task-gate-entry
title: A documentation change runs no task gate
doc-type: todo
status: draft
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-08
---

# A documentation change runs no task gate

> [!NOTE]
> **Narrowed 2026-11-08, still open.** `docs-dev` now declares one task gate, `review-config`
> (`kyber-weave review catalogue .`), and its `include` is `.kyber-weave/kyber-weave.yml` alone. So
> a `docs-dev` task that edits the configuration is checked, and its digest reports an executed
> gate; a task that edits only governed Markdown still selects nothing and still reports
> `NONE_APPLICABLE`. The documentation check this todo asks for is unchanged and unbuilt.

When this todo was filed, `review.task-gates` in `.kyber-weave/kyber-weave.yml` declared entries for
the development agents — `csharp-dev`, `github-devops`, `react-dev`, `typescript-dev`, `rust-dev`,
`test-dev` — and for none of the documentation, review or planning roles. `docs-dev` is the one that
matters most, because it is the role every completed change passes through: it edits governed
Markdown in this repository and the two `AGENTS.md` files, and it is the only role that archives a
plan or a specification. `kyber-weave review gates --agent docs-dev` then reported `KW-REVIEW-027`
("no task gates declared for this agent") and executed zero gates, which is recorded as
`TASK_GATE: NONE_DECLARED` and is indistinguishable, to a reader of a completion digest, from a
change that was checked and passed.

**What closed:** the configuration-only half. A task that edits `.kyber-weave/kyber-weave.yml` is a
`docs-dev` task in this repository, and it now selects `review-config`. That is a configuration load
check, not a documentation check: it proves the file the gates are declared in still loads, and says
nothing about a governed document.

**What remains open:** a task gate that runs the checks covering a documentation change over
`{changed}` documents — the docs gates (`kyber-weave docs validate .` and `docs drift .`) are cheap
relative to a build, and the repository-policy project owns the prose and frontmatter checks. Two
questions come with it, and neither is decided:

- Should the gate run those checks over `{changed}` documents, or over the corpus?
- What should a docs change that touches nothing but `docs/archive/` do? An archive move has no
  schema findings to produce, so a gate that runs nothing must still report that it ran nothing
  rather than reading as a pass.

**This todo is complete when** a `docs-dev` task gate runs the checks that cover a documentation
change, and a documentation-only change's digest reports the executed gate count instead of
`NONE_APPLICABLE`.

Related: the [review council architecture](../code-review/architecture.md#task-gates) records the
gate this role has today.
