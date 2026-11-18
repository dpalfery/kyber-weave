---
id: todo/squad-crash-test-follow-ups
title: The squad crash-safety contract suite has three untested fault shapes
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
---

# The squad crash-safety contract suite has untested fault shapes

## The finding

The crash-safety contract suite injects a fault at every file operation, runs recovery and compares
the tree. Three shapes it does not reach, and two edges it leaves open:

1. **Lease acquire and release are not fault points.** A crash while the lease is being taken, or
   between the durable intent and the release, is not simulated. The recovery code assumes the lease
   is either held or gone.
2. **An injected kill runs `using` blocks.** The in-memory kill unwinds the stack, so `Dispose` runs
   where a real process kill would not. A resource that a real crash leaves half-written is cleaned
   up in the test, which can hide a recovery bug.
3. **The directory-symlink case is unwritten.** A transaction that meets a directory symlink where
   it expects a real directory is reasoned about in the code but not pinned by a test.
4. **The inline-rollback cleanup marker is not asserted.** A rollback that fails partway writes a
   marker so the next recovery can finish it; no test checks that a second recovery reads it.
5. **Windows leaves no image at the installed path** if the process stops between moving the old
   image aside and moving the new one in. This is a known gap, listed in the self-update contract
   suite; it is not a regression from the seam work.

The suite is the safety net for requirement 10.2, so a shape it cannot reach is a gap in the proof,
not a missing nicety.

## What is and is not established

Every listed behaviour is established by reading the suite and the recovery code. No production
crash has been observed from these shapes, and the real-disk run passes on macOS, Linux and Windows
for the cases it does reach.

## Why this was not fixed here

A kill mode that skips cleanup needs the in-memory file system to model a killed process rather than
an exception, which changes shared test infrastructure. The lease fault points need a seam that can
fail acquire and release deterministically. Both are test-infrastructure changes with their own
cost, and neither blocks the decisions this closeout records.

## Options

1. **Add a "hard kill" mode to the in-memory file system first (recommended).** It skips `Dispose`
   and leaves the tree exactly as written, then the existing fault plan can target lease
   acquire/release and the directory symlink. This is the smallest change that removes all four
   in-memory gaps.
2. **Add the lease fault points alone.** Cheaper, but the `using` difference still hides cleanup bugs.
3. **Write the Windows no-image case as an accepted limitation test.** Pin the current behaviour so a
   later fix changes the test on purpose.

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md#crash-safety-contract-tests) — what the
  suite proves today
- [Squad lease contention](squad-lease-contention-cross-process.md) — the fixed-wait flake in the
  same deployment tests
- [ADR 0089](../adr/0089-every-behaviour-is-testable-through-a-seam.md) — decision 9, the seven
  weaknesses this suite closed
