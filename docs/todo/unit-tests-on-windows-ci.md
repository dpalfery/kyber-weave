---
id: todo/unit-tests-on-windows-ci
title: Run the unit test projects on Windows, and make their path defaults platform-qualified
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
---

# Run the unit test projects on Windows

## The finding

CI runs the unit and content projects on Ubuntu only. The integration job has a Windows leg with a
narrow filter (`SquadDeploymentState`, `PhysicalFileSystemContractTests`, `SelfUpdate`), but no unit
project runs on Windows at all. So a test that hard-codes a POSIX root passes every CI run and would
fail on a Windows developer's machine.

The known literals:

- `FakeProcessEnvironment` defaults its working, home and temporary directories to `/kw/work`.
- `SquadTestCommands` and `FakeReviewWorkTreeService` default to the same shape.
- `InMemorySquadState` and `SquadLinkSyncCrashWorkspace` call `Path.GetFullPath("/kw/work")`.
- A few newer tests build `Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kw-..."))`; those are
  platform-qualified but read the real system temp root, so they are still not fake-rooted.

`InMemoryFileSystem.Root` is already the platform root a unit test should derive from, so most of
the fix is deleting a literal default and passing `files.Root` (or a child of it).

## What is and is not established

The literals are established by grep. Whether every one of them fails on Windows is not: a POSIX
path can be a legal relative name there, and the in-memory file system normalises it. The honest
statement is that the suite has no Windows evidence, not that it is broken there.

## Why this was not fixed here

Closeout records the gap; adding a Windows unit leg changes CI cost and belongs with a CI change,
and changing the defaults touches many tests at once. It needs the Windows leg first, so the change
is proved rather than guessed.

## Options

1. **Add the unit projects to the existing Windows integration leg, or a small Windows unit job, and
   fix the literals it reports (recommended).** Start with one representative project so the job
   stays inside the CI budget, then widen it.
2. **Fix the literals first, without a Windows leg.** Cheaper, but the change is unproved and the
   next literal lands unnoticed.
3. **Leave the literals and document the suite as POSIX-only.** Rejected as the end state: the
   product ships on Windows and the unit suite is the main proof.

## Related

- [Test coding standard](../standards/test/test.standards.md#the-seams-and-their-fakes) — test roots
  are derived from the fake's in-memory root
- `.github/workflows/ci.yml` — the unit job and the Windows squad-contract job
- [Interface-driven test follow-ups](interface-driven-test-follow-ups.md) — the other test-side work
