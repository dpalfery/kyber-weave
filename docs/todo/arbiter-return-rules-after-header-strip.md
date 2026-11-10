---
id: todo/arbiter-return-rules-after-header-strip
title: Arbiter return rules stay blind on header-stripped dispatches
doc-type: todo
component: KyberArbiter
status: draft
owner: dpalfery
last-reviewed: 2026-11-09
---

# Arbiter return rules stay blind on header-stripped dispatches

The Arbiter strips the routing headers from a dispatch to an implementation specialist before the worker runs. Some checks that run on the return therefore have nothing to work with. The fix that wired RED evidence into `KW-ARB-MODE-001` left these on purpose, because turning them on adds new blocking behaviour that needs its own test contract.

**Work.**

- `KW-ARB-DIFF-001` is the intended check that a worker changed only its task's files. Its `git.changed-paths.since-dispatch` fact looks up a snapshot keyed on a hash of the unstripped prompt, so a stripped return finds no baseline, and the return has no plan or task facts. The return can now be joined to its dispatch in the ledger, which supplies both.
- `ledger.in-flight.paths` is never set, so the overlap branch of `KW-ARB-READY-001` never fires.
- Factory can deliver a finished worker's report as a pushed completion message, which fires only `UserPromptSubmit`. The Arbiter is not registered for that event and the hook payload is undocumented. Today the conductor must read the worker with `TaskOutput`. Confirm the payload on a live session, then decide whether to capture it.

**Why it matters.** Until `KW-ARB-DIFF-001` works, a dispatch to the right specialist that names another no-test task and describes different work in prose passes the test-first gate. That is the open part of the header-spoofing hazard in the Arbiter runbook.

**Acceptance.** A worker that changes files outside its task is caught on return, on Factory and Claude. Overlapping in-flight tasks escalate. Factory's completion path is captured, or the limit is recorded as permanent.
