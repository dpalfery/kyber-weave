---
id: todo/repo-provenance-second-worktree-unpinned
title: No test proves two worktrees of one repository report the same repo=
doc-type: todo
status: draft
component: DocGraph
owner: dpalfery
last-reviewed: 2026-11-07
---

# No test proves two worktrees of one repository report the same `repo=`

Every docs response now leads with `provenance: root=… branch=… repo=… rev=… dirty=… documents=N`,
and the whole point of `repo=` is the distinction `root=` cannot make: **a second worktree of the
same repository has a different `root=` and the same `repo=`**, while a foreign repository differs
in both. Agents are told to compare `repo=` instead of `root=` before trusting an answer.

No test proves it. `CorpusProvenanceTests` covers a normal checkout — a `.git` directory, the
`HEAD` ref, a detached head, and the unreadable cases — and nothing creates a second worktree, so
the `commondir` read that produces the shared value is exercised by no test at all. A regression
there would surface as an agent treating its own worktree as a foreign repository: the exact
confusion the field was added to end, and one that reads as a correct refusal rather than as a bug.

Closing it needs a fixture that creates a **real linked worktree** — `git worktree add` against a
temporary repository — and asserts both halves: the two roots differ, the two `repo=` values are
equal, and `branch=` differs when the two checkouts are on different branches. The docgraph
`FastTestGitRepositoryFixture` template may be the place to start, or it may not survive creating a
second worktree; that is the first thing to settle.

This todo is complete when a test creates a linked worktree of a temporary repository and pins the
different-`root=`/same-`repo=` pair, with the branch values it expects.
