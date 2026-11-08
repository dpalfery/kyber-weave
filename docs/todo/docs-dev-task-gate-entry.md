---
id: todo/docs-dev-task-gate-entry
title: A documentation change runs no task gate
doc-type: todo
status: draft
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-07
---

# A documentation change runs no task gate

`review.task-gates` in `.kyber-weave/kyber-weave.yml` declares entries for the development
agents — `csharp-dev`, `github-devops`, `react-dev`, `typescript-dev`, `rust-dev`, `test-dev` —
and for none of the documentation, review or planning roles. `docs-dev` is the one that matters
most, because it is the role every completed change passes through: it edits governed Markdown in
this repository and the two `AGENTS.md` files, and it is the only role that archives a plan or a
specification.

The consequence is not a weak gate but **no gate**. `kyber-weave review gates --agent docs-dev`
reports `KW-REVIEW-027` ("no task gates declared for this agent") and executes zero gates, which is
recorded as `TASK_GATE: NONE_DECLARED` and is indistinguishable, to a reader of a completion digest,
from a change that was checked and passed. Every other developer role's digest carries executed
evidence.

The gate set a docs change can actually be checked by already exists: the docs gates
(`kyber-weave docs validate .` and `docs drift .`) are cheap relative to a build, and the
repository-policy project owns the prose and frontmatter checks. Decide whether a task gate should
run them over `{changed}` documents, and what a docs change that touches nothing but
`docs/archive/` should do — an archive move has no schema findings to produce, so a gate that runs
nothing must still say so rather than reading as a pass.

This todo is complete when a `docs-dev` task gate runs the checks that cover a documentation
change, and its digest reports the executed gate count instead of `NONE_DECLARED`.
