---
id: todo/task-gate-test-project-mapping
title: Map changed files to the touched test project for task gates
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-20
---

# Map changed files to the touched test project for task gates

The `csharp-dev` task gates (`review.task-gates` in `.kyber-weave/kyber-weave.yml`) run the
format checks and a Release build, and no tests. This repository has one test project,
`tests/KyberWeave.Tests`, and its full suite with coverage takes about 25 minutes, which is far
too slow to run after every task. So today the per-task exit gate gives no executed evidence
that a change still passes its tests; that evidence arrives only at the end-of-run
`review gates` suite.

Add a way for a task gate to run only the tests relevant to the files a task changed. The
selection step belongs next to `TaskGateSelection`: map each changed source file to the test
project that covers it and, within a project, to a test filter (for example by namespace or
class name), then expose the result to a gate as a new whole-element token alongside
`{changed}`. A gate would then declare `dotnet test <project> --filter {tests}` and be skipped,
with `KW-REVIEW-026`, when nothing maps.

Open questions: whether the mapping is derived from a naming convention, from the CodeGraph
index, or declared in `kyber-weave.yml`; and what a changed file with no mapped test does, since
silently running nothing would read as a pass.

This todo is complete when a task gate can run only the tests that cover a task's changed files
and the fast path stays under a few minutes on this repository.
