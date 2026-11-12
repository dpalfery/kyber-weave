---
id: todo/task-gate-tests-gate-misses-repopolicy-project
title: The test-dev task gate's tests gate never runs the RepoPolicy test project
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-11-11
---

# The test-dev task gate's tests gate never runs the RepoPolicy test project

The `tests` task gate in `review.task-gates` (`.kyber-weave/kyber-weave.yml`, anchor
`&dotnet-tests`) always runs `tests/KyberWeave.Tests/KyberWeave.Tests.csproj` with the classes
`review gates` resolves. A task whose tests live in `tests/KyberWeave.RepoPolicy.Tests` (for example
`KyberWeave.Tests.ReportGateScriptTests`, whose namespace is `KyberWeave.Tests` although its project
is RepoPolicy) gets a filter that matches nothing in that project, and the gate exits 8 (zero tests
matched).

Found in the run of plan `2026-11-11-cleanupcode-exit-hang` (task T1, failure cluster
`task-gate/tests`, one rework dispatch spent). The worker's direct run of the class (16 of 16
passing) was used as evidence instead. The defect predates that plan.

Acceptance for closing this todo: a task gate run over a change to a RepoPolicy test file runs the
matching classes in `tests/KyberWeave.RepoPolicy.Tests` with a non-zero test count, and a change
that maps to no class in either project still skips with `KW-REVIEW-026` rather than exiting 8.
