---
id: todo/arbiter-served-containment-console-folding
title: The Arbiter served-containment test fails locally on console line folding
doc-type: todo
component: KyberArbiter
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
---

# The Arbiter served-containment test fails locally on console line folding

## The finding

`ArbiterServedContainmentTests.TheCommentCommandRefusesAFileOutsideTheRoot` fails on an untouched
tree on the reference developer machine. The refusal is produced; the assertion misses it because
the console output is folded at the terminal width, so the expected line is split differently than
the test's `ConsoleOutputAssert` expects.

The test is in `KyberWeave.Integration.Tests` and CI runs it on Ubuntu, where the width differs. The
local failure was seen during the interface-driven unit tests closeout, in the full integration run.

## What is and is not established

The local failure is reproducible on that machine. Whether CI sees it, and whether it is only the
width or also the locale, is not established. No product behaviour is wrong: the command refuses the
file, as the assertion's own output shows.

## Why this was not fixed here

Closeout does not change a passing CI test on the strength of one machine's terminal. The fix is in
the assertion helper, not the product, and it needs the CI signal first.

## Options

1. **Assert on the unwrapped message, not the rendered line (recommended).** `ConsoleOutputAssert`
   should join folded lines before matching, or the test should match the message before the writer
   wraps it.
2. **Set a fixed width in the test's console capture.** Narrower and local to the test, but it hides
   the same bug in every other test that asserts rendered output.
3. **Skip the test locally.** Rejected: it hides a real local signal.

## Related

- [Test coding standard](../standards/test/test.standards.md#isolation-and-assertions) — failure
  messages identify the offender
- `.github/workflows/ci.yml` — the integration job that runs this class on Linux
