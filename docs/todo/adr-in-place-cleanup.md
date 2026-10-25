---
id: todo/adr-in-place-cleanup
title: Remove superseded history from older ADRs
doc-type: todo
status: draft
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-20
---

# Remove superseded history from older ADRs

The repository policy is that an accepted ADR is edited in place to state only the decision in
force, with no record of the superseded one. [ADR 0005](../adr/0005-task-level-fast-review.md)
was brought into line when the `task-reviewer` was replaced by the `task-gate` skill. Several
older ADRs still carry superseded history that the policy says to remove:

- ADR 0007, decision D4
- ADR 0008
- ADR 0016, decisions 1 and 8
- ADR 0020
- ADR 0023, decision 3
- ADR 0033, the context section

For each, state the decision in force and delete the superseded narrative, then re-run
`docs validate` and `docs drift`, since an edit can move a `code-refs` or link target.

Also decide whether `docs/archive/adrs/` stays. If every ADR is now edited in place, the archive
holds only history the policy forbids keeping in the live set; if some of it is still the only
record of a reversed decision, say which and why it is kept.

This todo is complete when the six ADRs read as the decision in force alone and the archive
question has an answer recorded in the ADR README.
