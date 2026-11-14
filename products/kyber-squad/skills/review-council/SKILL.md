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
   Nothing else: no description of the change, no file list, no request for tests. It runs the
   gates and returns `STATUS: COUNCIL_SEATS_NEEDED` with the `evidence:` line and one block per seat.
   Each block opens with `SEAT: <review-lens|review-triage>`, then the routing header block, a blank
   line, `Packet: <path>` and `Seat file: <path>`.
2. **Seat.** Send everything after each `SEAT:` line, unchanged, as the prompt to the agent that line
   names. Send every seat in one parallel batch. Each seat replies `written: <path>`.
3. **Continue.** Re-invoke `code-reviewer` with `COUNCIL-SEATS: hand-back` as the first body line, the
   same tree, base ref and cycle folder, and one line per seat: its lens and its outcome. The outcome
   is `written`, `not run: <the error>` for a seat that could not be started, or the Arbiter note on
   that seat's dispatch, verbatim (`ARBITER_SKIP`, `ARBITER_VERIFIED`, `ARBITER_ANNOTATION`). A seat
   that could not be started is not run, never skipped.
4. **Refute.** The reviewer may return one more `STATUS: COUNCIL_SEATS_NEEDED` holding a single
   refutation seat, whose body carries the findings themselves and a `Seat file:` line. Repeat steps 2
   and 3 for it.
5. **Verdict.** The reviewer returns the verdict (`APPROVE` or `REQUEST_CHANGES`), the cycle folder and
   the path of its `findings.json`. Report that; the review loop in the `delivery-execution` skill
   decides what follows.

## What stays elsewhere

Lens selection, packets, routing headers, the gate-proven lenses that are not seated, refutation and
the verdict rules are in the `code-review` skill and the reviewer's own instructions. The Arbiter
classifies each seat you start as the reviewer's own lens or refutation spawn, so its lens-skip and
refutation rules apply as they would to the reviewer.
