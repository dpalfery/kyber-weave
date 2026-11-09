---
id: todo/review-suppressions-gate-not-selected
title: No task gate runs review suppressions
doc-type: todo
status: archived
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-08
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# No task gate runs review suppressions

**Status: Archived — closed 2026-11-08.** Both halves landed. `.kyber-weave/kyber-weave.yml` declares a
`suppressions` task gate under `csharp-dev` and aliases it across the other .NET developer roles
(`dal-dev`, `maui-dev`, `pulumi-dev`, `test-dev`); the catalogue family file
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

## Decisions

| ID | Question | Answer / decision | Where it is recorded |
|---|---|---|---|
| RSG1 | Which side was missing — the command or the wiring? | The wiring. `review suppressions` and its two rule ids already shipped and were documented; no gate declared it and the catalogue offered it to no host, so nothing ran it. | [Review council architecture: the suppression gate](../../code-review/architecture.md#the-suppression-gate) |
| RSG2 | Which agents get the task gate? | Every .NET authoring role: `csharp-dev`, `dal-dev`, `maui-dev`, `pulumi-dev`, `sql-database-architect` and `test-dev`. A warning-disable pragma is the same defect wherever it is written, and a test file is written by an author like any other. | [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml) `review.task-gates` |
| RSG3 | How is the gate invoked — with `{changed}` or bare? | Bare: `review suppressions "."`. The command reads the change against its base itself, so naming the changed files would scan them twice and still miss a suppression added to a file the gate's `include` did not list. | [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml) `review.task-gates`, id `suppressions` |
| RSG4 | Does the catalogue carry it too, or only this repository? | Both. This repository's config gains the gate so the change is caught here, and the csharp catalogue family carries the same entry so `docs init` scaffolds it into any host installing a .NET stack. The golden fixture was regenerated through the real code path; the diff is the five gate entries and nothing else. | [The shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) and `products/kyber-squad/standards/csharp/gates.yml` |
| RSG5 | What about `sql-database-architect` in the catalogue? | Left off it, deliberately. `GateCatalogue`'s developer-slug vocabulary does not list that agent, so naming it in a catalogue gate fails every render. The gap is recorded as its own todo rather than hidden by widening the vocabulary to make the error go away. | [sql-database-architect is not a catalogue developer slug](../todo/sql-database-architect-is-not-a-catalogue-developer-slug.md) |
| RSG6 | Was the gate verified by running it, or by reading it? | Run. `review gates --agent <name>` passes clean for all six agents, and fails with `KW-REVIEW-022` for `dal-dev` on a `.cs` file given an injected warning-disable pragma with no reason; the injection was removed afterwards. | This note's closing paragraph, and the gate's own output |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DH1 | ReviewCouncil | architecture | [The suppression gate](../../code-review/architecture.md#the-suppression-gate) | The suppression gate is selected by the task gates of every .NET authoring role and runs bare rather than over `{changed}`, because the command reads the change against its base itself and a changed-file list would both scan twice and miss a file the gate did not list |
| DH2 | ReviewCouncil | architecture | [The shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) | A gate that ships in the catalogue reaches installing hosts through the golden fixture, which is regenerated through the real `docs init` path so a hand edit to the catalogue cannot silently change what a host receives |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| RSG1, RSG2, RSG3, RSG6, DH1 | [The suppression gate](../../code-review/architecture.md#the-suppression-gate) | ReviewCouncil | 2026-11-08 | archived |
| RSG4, DH2 | [The shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) | ReviewCouncil | 2026-11-08 | archived |
| RSG5 | Documentation waiver: the agent that the catalogue cannot name is a boundary fact, not a lasting design statement, and the durable record is the todo that tracks it; the run-it-for-real check needs no prose beyond the paragraph above, because the gate's behaviour is already stated in the suppression-gate section. | ReviewCouncil | 2026-11-08 | archived |
