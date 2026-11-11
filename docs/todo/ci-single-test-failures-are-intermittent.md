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

## Hypotheses eliminated by reading the code

The first guess was that the executable bit did not survive a copy, because `kw-export-` reads like an
export directory. **It is not one.** `kw-export-` is only the GUID prefix `TempDirectory` puts on every
temp directory it makes (`TempDirectory.cs:6`); nothing is exported or copied, and the stub is written
once, in place.

The mode is set explicitly and correctly: `DoctorHarness.WriteStub` writes the script and then calls
`File.SetUnixFileMode` with `UserRead | UserWrite | UserExecute` on every non-Windows platform
(`DoctorHarness.cs:62-70`). So the stub is executable at the moment it is written, and a missing execute
bit cannot be the cause on Linux.

What the runner's path shows is a deeper nesting than a developer machine produces:
`/tmp/kw-<checkout-key>/<pid>/kw-export-<guid>/kyber-weave-mcp-stub`. `TestTempRoot` redirects `TMPDIR` to
`/tmp/kw-<key>/<pid>` at assembly load so Squad path canonicalisation does not list the system temp
directory, and it creates that root `0700`. Every directory above the stub is therefore traversable by
the owner, which rules out a permission failure on the path as well.

## What is left, and what is missing

"Could not be started … No such file or directory" with an executable file whose directory chain is
traversable means the file was **not there** when the child was started, or its `#!/bin/sh` interpreter
could not be resolved. The first is the more likely of the two and points at lifetime, not permissions:
`TestTempRoot` deletes `/tmp/kw-<key>/<pid>` from a `ProcessExit` handler and again at module load "if a
recycled PID finds the previous run's leftovers", and `TempDirectory.Dispose` deletes each test
directory with five retries. Any overlap between one test process tearing down and another starting under
the same root would remove a live stub.

None of that has been reproduced, and the CI job runs the two test assemblies as sequential steps, which
is the arrangement in which an overlap is least likely rather than most. So this is a narrowed search,
not a finding.

For the roster assertion there is no hypothesis at all yet — it is a plain string search over a document,
so a flake there points at something else, such as a test that reads a file another test rewrote in
parallel.

## What closing this needs

- Reproduce one of them deliberately, or capture the artifact. The job already records the failing
  assertion; a run that keeps the stub file and its directory chain, with `ls -l` on each level plus the
  child's `startTime` relative to the test's own start, would settle the lifetime question in one step.
- If it is the teardown, make the per-process temp root unique per test process rather than per
  PID-derived name, so two processes can never reclaim one root.
- Check whether the failing assertions share a fixture- or filesystem-mediated path, which is where a
  parallel-execution flake would live.
- If a test is genuinely order- or timing-dependent, fix that test. A retry attribute hides the defect
  that the test exists to catch.
