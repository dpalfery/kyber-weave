---
id: todo/squad-lease-contention-cross-process
title: Squad lease contention tests fail when run concurrently across worktrees
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-10-11
---

# Squad lease contention tests fail when run concurrently

## The finding

`SquadDeploymentStateTests.ExecuteProjectAndGlobalSamePhysicalRootContendForOneLease`
waits on a fixed 5-second timeout for the active transaction to reach
`IntentWritten`:

```csharp
Assert.True(
    blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
    "The active transaction did not acquire its lease before contention was tested.");
```

The four `[Theory]` cases — and in particular the two whose
`SquadDeploymentScope.Global` variants resolve outside the test's own `TempDirectory` —
compete for a lock that is not scoped to that temp tree. Two worktrees, or a local run
alongside a CI job running the same class, starve each other and fail the assertion
after the full 5-second wait. Observed 2026-10-11: three of the four cases failed at
7–10s each while a second worktree ran the identical class.

The failure message is accurate about what it saw and misleading about why: it reads as
a regression in lease acquisition, when nothing about the product changed. That is the
cost — the one class that most needs a human to read carefully is the one whose failures
are most likely to be noise.

## Why this was not fixed here

The local inner loop (`scripts/fast-loop.sh`) excludes this class from the fast tier,
which stops the noise in the edit-check loop without touching what CI gates on. That is a
mitigation for the loop, not a fix for the defect, and leaving it unfixed means CI can
still go red for this reason alone.

## Options

1. **Scope the lock to the test's temp root.** Make the Global scope resolve under a
   per-test root the way the Project scope already does, so two concurrent runs contend
   only with themselves. This is the real fix and it likely means a seam in how
   `SquadDeploymentScope.Global` resolves its root.
2. **Make the wait condition, not the clock, the assertion.** Poll for `IntentWritten`
   with a generous ceiling and fail only on the deadline, so a slow machine is slow
   rather than red. Weaker than (1): it would not stop two runs from actually
   interfering, only from reporting it.
3. **Serialize the class across processes.** A cross-process mutex keyed on the resolved
   root. Treats the symptom and adds a lock that can deadlock on a crashed run.

Option 1 is the one worth doing. Options 2 and 3 are recorded because they are cheaper
and because the choice belongs to whoever picks this up.

## Related

- [`ci-pipelines/workflows-runbook`](../ci-pipelines/workflows-runbook.md) — the CI job
  matrix that runs this class on three operating systems.
- `scripts/fast-loop.sh` — excludes the class from the fast tier, with the reason.