---
id: todo/ci-single-test-failures-are-intermittent
title: CI fails one unrelated test at a time, and a rerun clears it
doc-type: todo
status: draft
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-11-08
---

# CI fails one unrelated test at a time, and a rerun clears it

The `Build and test` job has failed on a different, unrelated single test in successive runs across
several branches of one programme, and each time the same commit passed when the job was re-run. That is
the signature of an intermittent failure, not of a defect in the code being merged — but it costs a full
re-run each time and it erodes trust in a red build, which is the one signal the whole gate stack rests on.

## What was observed

| Branch | Test that failed | Outcome |
|---|---|---|
| slice 7 | `McpLiveProbeTests.McpLiveProbe_RequestsTheRenamedDocsTool_AndKeepsItsCheckId` — the MCP stub "could not be started" from a `/tmp/kw-export-…/kyber-weave-mcp-stub` path | passed on re-run of the same commit |
| slice 4 | `SquadCanonicalContentTests.ProductReadmeRosterLacksSecondBrain` — `Assert.Contains` sub-string not found | that branch's later commit is green |
| slice 7 and 8 | `RetrievalRegressionTests.APlainlyAskedQuestionReturnsOneAnswerBlob` | **not** this failure mode: a genuine retrieval regression, fixed in `docs/code-review/architecture.md` |

Both real flakes pass on this machine: the MCP probe suite is 23/23 locally at the failing commit. So the
difference is the runner, not the tree.

## The leading hypothesis, and what is missing

The MCP stub is started from a content-addressed export under `/tmp/kw-export-*`, and `Process.Start` on a
shell stub fails when the executable bit did not survive the export or the copy. That would explain "could
not be started" precisely and would not reproduce on a developer machine, where the stub is written
directly. It is a hypothesis, not a finding: nobody has read the export path's permission handling, and
the failure has not been reproduced on demand.

For the roster assertion there is no hypothesis at all yet — it is a plain string search over a document,
so a flake there points at something else, such as a test that reads a file another test rewrote in
parallel.

## What closing this needs

- Reproduce one of them deliberately, or capture the artifact. The job already records the failing
  assertion; a run that keeps the stub file, with `ls -l` on it, would settle the permission question in
  one step.
- If the export is losing the mode, fix the export rather than marking the test as retryable.
- Check whether the failing assertions share a fixture- or filesystem-mediated path, which is where a
  parallel-execution flake would live.
- If a test is genuinely order- or timing-dependent, fix that test. A retry attribute hides the defect
  that the test exists to catch.
