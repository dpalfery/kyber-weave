---
id: adr/0077-the-dispatching-session-seats-the-council-from-the-gate-runs-evidence
title: The dispatching session seats the review council, from the evidence the gate run writes
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: ReviewCouncil
decided-by: []
supersedes: []
code-refs:
  - ReviewEvidence
  - TriggerClassifier
---

# ADR 0077: The dispatching session seats the review council, from the evidence the gate run writes

## Status

Accepted, 2026-11-13. Decided by the owner.

## Context

A headless GitHub Copilot CLI run reviewed a change of 22 files and 925 lines and approved it with no
findings. The review was one `code-reviewer` dispatch lasting 11.5 minutes, about 9 of them model time.
It started no seat. One agent walked all seventeen lenses in turn in one context, ran tests and an
end-to-end suite of its own besides the declared gates, and its static-analysis lens restated "0
warnings", which the gates had already proven.

Three things allowed that:

1. **The prose let a dispatched reviewer review in-process.** The council step said that where the
   harness cannot start seats, the reviewer applies each lens in turn, and a second paragraph allowed
   the same when the reviewer is "the top-level review". A dispatched reviewer cannot tell which case
   it is in, and the `STATUS: COUNCIL_SEATS_NEEDED` handback was written as the exception.
2. **A dispatched agent cannot reliably start agents.** GitHub's documentation says subagents cannot
   create other subagents. In about 60 recent Copilot CLI sessions on the owner's machine no subagent
   ever called the CLI's `task` tool, although the rendered `code-reviewer` lists `agent` and its
   roster. Claude Code withholds its `Task` tool from subagents, and Devin withholds nested delegation.
   Only the session a human started dispatches reliably on every harness.
3. **The review started from nothing.** The conductor retyped the change and asked for "targeted"
   tests, so the reviewer rediscovered the change set and re-ran checks the gates own.

## Decision

1. **The dispatching session seats the council.** A `code-reviewer` that another agent dispatched
   never starts or applies a lens itself, whatever tools it holds. It indexes the tree, runs the
   gates, writes one packet per seat, and returns `STATUS: COUNCIL_SEATS_NEEDED` with one `SEAT:` block
   per seat: the agent, the routing header block and the packet path. The conductor (or `bug-crusher`)
   sends each block unchanged to its agent, every seat in one parallel batch, and re-invokes the
   reviewer, which saves the outputs, adjudicates and computes the verdict. The refutation spawn
   comes back the same way. A reviewer a human started seats its own lenses; only one with no tool to
   start agents runs the council in-process, and says so.
2. **The gate run writes the evidence.** The council's `review gates` run writes `review-diff.patch`
   (merge base to the files on disk) and `evidence.md` (base, head, diff path, changed files with
   untracked ones marked, and every gate's result) into the cycle folder, and prints
   `evidence: <path>`. Every packet names that file. The conductor's review dispatch carries only the
   tree, the base ref and the prior cycle folder.
3. **No seat for what a gate already proves.** `static-analysis-triage` is not seated when every
   build, lint, format and type-check gate passed, and `duplicate-implementation` is not seated when
   no duplicate cluster names a changed file. Each is recorded with `--not-seated`, so the report cites
   the gate. `test-adequacy` stays: whether the tests would catch the change breaking is a judgement
   no gate makes.
4. **The Arbiter sees the same spawn.** A `review-lens` or `review-triage` dispatch from the conductor
   or `bug-crusher` that carries `LENS:` or `REFUTE:` classifies as `lens.spawn` or `refute.spawn`, as
   it does from `code-reviewer`, so the lens-skip and refutation rules still apply.

## What is given up

The gates no longer run beside the seats. Every seat now starts after the gate run, so a slow gate
delays the lenses that do not read it. The lenses that do read a gate, `test-adequacy` above all,
already waited for it, so the critical path is the gate run plus the slowest seat either way.

On a host that does not treat warnings as errors, a passing build can still print warnings. With
the analyzer gates passed, no seat triages those warnings or the suppressions the change added; a host
that wants them triaged declares a gate that fails on them, such as this repository's `suppressions`
task gate.

## Consequences

- One review costs at least two reviewer dispatches (gates and packets, then the verdict), and a
  third when a finding needs refuting. The lenses run in parallel in separate contexts.
- The reviewer's agent file shrinks: the lens fan-out procedure lives in the `code-review` skill and
  the conductor's `delivery-execution` skill.
- The review packet no longer depends on the conductor's description of the change.
