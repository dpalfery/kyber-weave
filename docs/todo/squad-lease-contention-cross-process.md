---
id: todo/squad-lease-contention-cross-process
title: Squad lease contention test fails under load on a fixed 5-second wait
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-10-11
---

# Squad lease contention test fails under load

## The finding

`SquadDeploymentStateTests.ExecuteProjectAndGlobalSamePhysicalRootContendForOneLease`
waits on a fixed 5-second timeout for the active transaction to reach
`IntentWritten`:

```csharp
Assert.True(
    blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
    "The active transaction did not acquire its lease before contention was tested.");
```

Observed 2026-10-11: three of the four `[Theory]` cases failed at 7-10s each while a
second worktree ran the identical class on the same machine. The failure message
reads as a lease-acquisition regression, when nothing about the product changed.

## What is and is not established

The original hypothesis was cross-process contention: that the `Global` cases lock
something outside the test's temp tree, so two worktrees starve each other. Reading the
test does not support that. It builds `applicationData` inside its own `TempDirectory`
and passes it to `Store(applicationData)`, so the state directory, and the lease keyed on
its physical path, are per-test. Two worktrees should not be able to share one.

What is established is narrower: the wait is a fixed wall-clock deadline, the class is the
slowest in the suite (about 43s of summed time across its cases in a parallel full run), and it failed only while the machine
was running a second copy of the suite. That points to the deadline being too tight for a
loaded machine, not to a shared lock. **The root cause has not been demonstrated**; it was
not reproduced in isolation under controlled load.

## Why this was not fixed here

The local inner loop (`scripts/fast-loop.sh`) excludes this class from the fast tier,
which keeps the noise out of the edit-check loop without touching what CI gates on. That is
a mitigation for the loop, not a fix, and CI can still go red for this reason.

## Options

1. **Make the wait condition, not the clock, the assertion (recommended).** Poll for
   `IntentWritten` with a generous ceiling and fail only on the deadline, so a slow
   machine is slow rather than red. Cheap, and it addresses the one thing the evidence
   shows.
2. **Reproduce first.** Run the class under controlled CPU load, and with a second copy
   in another checkout, to confirm whether the deadline or a shared resource is the cause
   before changing anything else.
3. **Serialize the class across processes.** A cross-process mutex keyed on the resolved
   root. Only worth it if (2) finds real interference; it adds a lock that can deadlock on
   a crashed run.

## Related

- `.github/workflows/ci.yml`, job `squad-filesystem-contract` — runs this class on three
  operating systems.
- `scripts/fast-loop.sh` — excludes the class from the fast tier, with the reason.
