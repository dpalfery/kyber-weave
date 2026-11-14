---
name: review-council
description: "Runs the review council from the session a human started, on a harness where a sub-agent cannot start sub-agents: code-reviewer runs the gates and decides, and this session starts its lens seats. Use only when the conductor or bug-crusher says to load it in place of one code-reviewer dispatch. Do not use where sub-agents can dispatch: there one code-reviewer dispatch runs the whole council. Reviews nothing itself and fixes nothing."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Review council

On this harness a dispatched agent cannot start agents, so `code-reviewer` cannot start its own
lens seats. You start them for it. The reviewer still indexes the tree, runs the gates, writes the
packets, adjudicates and computes the verdict; you only start seats and pass paths.

**Keep your context small.** You never read a packet, a seat file or `evidence.md`, and you never
pass findings: every seat writes its output to its own seat file in the cycle folder, and the
reviewer and the verdict command read them there. What reaches you is statuses and paths.

## Steps

1. **Prepare.** Dispatch `code-reviewer` with `COUNCIL-SEATS: hand-back` as the first body line,
   then the tree under review, the base ref and, after the first cycle, the prior cycle folder.
   Nothing else: no description of the change, no file list, no request for tests. It returns
   `STATUS: COUNCIL_SEATS_NEEDED` with the output of `review seats`: `now:` and `after-gates:` seat
   blocks, each between `--- seat <lens> agent=<agent> ---` and `--- end ---`.
2. **Seat while the gates run.** In one parallel batch, send every `now:` block unchanged as the
   prompt to the agent its marker names, and dispatch `code-reviewer` again with
   `COUNCIL-SEATS: hand-back` and `GATES` as its first two body lines and the same tree, base ref and
   cycle folder. That dispatch runs the gates and returns `after-gates: send <lenses>`. Each seat
   replies `written: <path>`.
3. **After the gates.** In one batch, send the `after-gates:` blocks it named, and once more every
   seat that replied with nothing (only once).
4. **Continue.** Re-invoke `code-reviewer` with `COUNCIL-SEATS: hand-back` as the first body line, the
   same tree, base ref and cycle folder, and one line per seat: its lens and its outcome. The outcome
   is `written`, `no result` for a seat that replied with nothing twice, `not run: <the error>` for
   a seat that could not be started, or the Arbiter note on that seat's dispatch, verbatim
   (`ARBITER_SKIP`, `ARBITER_VERIFIED`, `ARBITER_ANNOTATION`). A seat that could not be started is not
   run, never skipped.
5. **Refute.** The reviewer may return one more `STATUS: COUNCIL_SEATS_NEEDED` holding one
   refutation block per finding, each with a `Seat file:` line. Send them in one batch and repeat
   step 4.
6. **Verdict.** The reviewer returns the verdict (`APPROVE` or `REQUEST_CHANGES`), the cycle folder and
   the path of its `findings.json`. Report that; the review loop in the `delivery-execution` skill
   decides what follows.

## What stays elsewhere

Lens selection, packets, routing headers, the gate-proven lenses that are not seated, refutation and
the verdict rules are in the `code-review` skill and the reviewer's own instructions. The Arbiter
classifies each seat you start as the reviewer's own lens or refutation spawn, so its lens-skip and
refutation rules apply as they would to the reviewer.
