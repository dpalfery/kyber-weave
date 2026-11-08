---
id: todo/review-suppressions-gate-not-selected
title: No task gate runs review suppressions
doc-type: todo
status: archived
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-08
---

# No task gate runs review suppressions

**Status: Archived — closed 2026-11-08.** Both halves landed. `.kyber-weave/kyber-weave.yml` declares a
`suppressions` task gate under `csharp-dev` and aliases it into `test-dev`; the catalogue family file
`products/kyber-squad/standards/csharp/gates.yml` carries the same gate so an installing repository
receives it from `docs init`, and its golden fixture was regenerated through the real code path — the
diff is the five gate entries and nothing else. Verified by running the gate for real: it passes on a
clean change and fails with one error on an added warning-disable pragma that states no reason.

One residual, recorded rather than worked around: `sql-database-architect` also adds suppressions, but
`GateCatalogue`'s developer-slug vocabulary does not list that agent, so naming it in the catalogue
fails the render. It is tracked in
[sql-database-architect is not a catalogue developer slug](sql-database-architect-is-not-a-catalogue-developer-slug.md).

`kyber-weave review suppressions [path]` exists and works: it scans the added lines of a change for a
`#pragma warning disable`, a `SuppressMessage` attribute or a `NoWarn` element and fails one that states
no `reason:` (`KW-REVIEW-036`), and fails closed on a change it cannot read (`KW-REVIEW-037`). What is
missing is the second half of the decision that created it: **no task gate runs it**, and the shipped
gate catalogue offers it to no installing repository. So an agent that adds a suppression without a
reason is not caught by the deterministic gate that was built to catch it; only a reviewer reading the
diff is.

## What "not selected" means here

- `.kyber-weave/kyber-weave.yml` declares no gate whose `run` starts `kyber-weave review suppressions`,
  under any agent's `review.task-gates` and under no `review.gates` entry either.
- `GateCatalogue` has no entry for it, so `kyber-weave review catalogue .` never emits it and a host
  scaffolding its `review:` block from the catalogue never receives it.
- No test pins either half. `ReviewSuppressionsCommandTests` pins the command's behaviour, which is why
  the suite is green while the wiring is absent.

## What closing this needs

One decision first, then the mechanical half. The gate reads a diff, not a file list, so it does not fit
the `{changed}` expansion the other task gates use; it needs either a task-gate form that runs once per
change (a `{base}`-style argument or no argument at all, comparing against `HEAD`) or a place in
`review.gates` where a whole-change check already belongs. Then:

- Declare it in `review.task-gates` following the file's YAML anchor-and-alias pattern, so every .NET
  agent's task gate runs it, or as a `review.gates` entry if the whole-change home is chosen instead.
- Offer it from the csharp catalogue family in `GateCatalogue`, with its `os` restriction and install
  path, so a host that scaffolds a C# review block receives it.
- Pin the declaration with a test, because the command's own tests cannot see it: a policy test over
  `.kyber-weave/kyber-weave.yml`, in the shape `TaskGatesRepositoryConfigTests` already uses for the
  other file kinds, is where the earlier "a change of this kind selects no gate" repairs are held.

The command and the ids are documented and stay as they are: [the suppression
gate](../code-review/architecture.md#the-suppression-gate) and the [rule
reference](../ci-pipelines/rule-reference.md#suppressions--review-suppressions).
