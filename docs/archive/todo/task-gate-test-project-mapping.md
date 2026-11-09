---
id: todo/task-gate-test-project-mapping
title: Map changed files to the touched test project for task gates
doc-type: todo
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Map changed files to the touched test project for task gates

> [!NOTE]
> **Closed 2026-11-08.** A task gate can now run the tests that cover its change. `{tests}` is a
> third token beside `{changed}` and `{task}`, and it expands in `TaskGateSelection` through
> `TestClassMapping.Resolve` to the fully qualified classes the changed files touch. The open
> questions this todo listed are answered: the mapping is **declared first** in a new
> `review.test-map` key (source glob to fully qualified class names) with the `<Stem>Tests` naming
> convention as the fallback, rather than the CodeGraph index, which is too unreliable on C# to
> select tests (issue 535); and a changed file with no mapped test **skips the gate** with
> `KW-REVIEW-026` naming it, rather than running a filter over nothing. Only classes a test file
> actually exists for survive, because a name assembled from a path matches nothing and exits
> zero, which afterwards reads as a suite that ran and passed. This repository declares the map
> and a `tests` gate under `csharp-dev`, aliased by `test-dev`, running
> `dotnet test ... --no-build --filter {tests}` against the artifacts directory `dotnet-build`
> wrote. The fast path was **not re-timed** at this closeout: the gate runs one class-filtered
> `dotnet test` over the classes a single change touches, not the 25-minute suite, but no
> measurement of that run is recorded in this tree. Documented in [review council
> architecture](../../code-review/architecture.md#the-tests-a-task-gate-runs), [review council
> configuration](../../configuration.md#review) and the [rule
> reference](../../ci-pipelines/rule-reference.md#gates--review-gates).

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

## Decisions

| ID | Question | Answer / decision | Where it is recorded |
|---|---|---|---|
| TGM1 | Where does the changed-file-to-test mapping come from? | **Declared first, convention second.** A new `review.test-map` key maps a source glob to the fully qualified test classes that cover it; a path no glob covers falls back to `<Stem>Tests`. The CodeGraph index was rejected: `codegraph affected` is too unreliable on C# to select tests (issue 535). | [Review council architecture: the tests a task gate runs](../../code-review/architecture.md#the-tests-a-task-gate-runs) |
| TGM2 | What happens when a changed file maps to no test? | The gate is **skipped** with `KW-REVIEW-026` naming it, never passed. A filter that matches nothing exits zero and would afterwards read as a suite that ran and passed. | [Review council architecture: the tests a task gate runs](../../code-review/architecture.md#the-tests-a-task-gate-runs) |
| TGM3 | How is the token spelled, and where is it resolved? | `{tests}`, a whole argv element beside `{changed}` and `{task}`, resolved in `TaskGateSelection` through `TestClassMapping.Resolve`. The program position refuses a token, so a changed file can never choose what executes. | [Review council architecture: task gates](../../code-review/architecture.md#task-gates) and [`review gates`](../../cli-command-surface.md#review--the-deterministic-review-half) |
| TGM4 | Which class name is emitted? | The namespace the class declares itself, or the one its directory implies for a file that declares none, and only for a class a test file exists for. | [Review council architecture: the tests a task gate runs](../../code-review/architecture.md#the-tests-a-task-gate-runs) |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DH1 | ReviewCouncil | architecture | [Review council architecture: the tests a task gate runs](../../code-review/architecture.md#the-tests-a-task-gate-runs) | A task gate can run the tests a change touches, resolved from a declared map then a naming convention, and a change nothing covers is a skip rather than a pass |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| TGM1, TGM2, TGM3, TGM4, DH1 | [Review council architecture: the tests a task gate runs](../../code-review/architecture.md#the-tests-a-task-gate-runs) | ReviewCouncil | 2026-11-08 | archived |

