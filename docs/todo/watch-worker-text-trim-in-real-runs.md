---
id: todo/watch-worker-text-trim-in-real-runs
title: Watch real runs for rework the worker-text trim caused
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
---

# Watch real runs for rework the worker-text trim caused

The trim removed about 20 KB of duplicated rule text from the worker agent bodies and moved each rule
to one home: the injected worker baseline, the task-gate skill and its rework reference, or the host
coding standard. Nothing was removed without a home ([the worker rule map](../kyber-squad/worker-rule-map.md)
records the home and the check for every row). What the rule map cannot show is whether a worker still
*acts* on a rule that now arrives from one place instead of six, and the cheapest evidence is the next
few real runs.

## What to compare

Take the next three to five conductor or bug-crusher runs of similar size and compare them with the
runs recorded before the trim:

- **Rework dispatches per run**, and what they were for. The trim's own estimate is that a rule lost
  to a worker shows up here first, as a second dispatch for something the old text stated.
- **InspectCode findings at the end-of-run review**, and specifically redundant qualifiers. The
  mechanical `dotnet format` pass left the task-end text in the same change, so a rise here is
  the signal it was carrying real work rather than the ban on it.
- **Task-gate failures by gate id**, to see whether a failure a worker used to fix by reading the agent
  body now arrives at the review instead.
- **`TESTS: total=0` reports**, which is what a worker returns when it believes no class covers its
  change.

## What would close this

Three observations with no unexplained rework growth close it. If InspectCode rework rises, the answer
is a scoped step back — restore that one rule to the place the worker reads at the moment it acts —
not a rollback of the trim, because the rule map already records which home each rule moved to and no
other row needs to move with it.

The runs themselves are owner-side: this repository cannot start them, and nothing in the tree records
a live run's rework tally today.
