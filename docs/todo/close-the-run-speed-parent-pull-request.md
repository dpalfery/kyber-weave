---
id: todo/close-the-run-speed-parent-pull-request
title: Close the run-speed parent design pull request once the stack merges
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
---

# Close the run-speed parent design pull request once the stack merges

Pull request #537 holds the conductor and bug-crusher run-speed parent design as a plan-only draft on
`main`. It is designed never to merge: the plan's own record (D12) has each of the eight slices deliver
as its own child plan and pull request, and the last one — slice 8, #547 — copy the parent file out of
that branch with `git show` and archive it inside its own pull request.

That design has one step nobody owns. Once #547 merges, the parent design is archived on `main`, and
#537 is still open, still a draft, and now redundant: it proposes to add a file that is already there in
its final archived form. Its `Docs validate` check fails `KW-DOC-LIFECYCLE-003` by construction, because
a plan that is still open cannot merge, so the pull request can never go green and will sit failing in
the list indefinitely.

## What closing this needs

- Merge the stack in order: #544, #545, #546, #547.
- Confirm the parent design is archived on `main` — `docs/archive/plans/2026-11-07-conductor-bug-crusher-run-speed.md`
  exists there and `docs/plans/` no longer carries it.
- Close #537 with a comment saying it is superseded by #547, naming the archived path, rather than
  deleting the branch: the branch is the design's edit history and the archived plan cites it by name.
- Check no governed document still links to the parent design at its pre-archive path. The archived copy
  is referenced from the plans index and the slice closeouts.

## Why it is a todo and not done now

Closing it before the archive lands on `main` would leave the design reachable only from an unmerged
branch. The order is the point: archive first, then close.
