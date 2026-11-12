---
id: plans/index
title: Kyber-Weave Plan Inventory
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-11-11
---

# Kyber-Weave Plan Inventory

This inventory tracks architectural and implementation plans across their lifecycle. Active plans guide current execution; completed plans have their durable architectural decisions harvested into [ADRs](../adr/README.md) and canonical documentation, and are archived to `docs/archive/plans/`.

Every plan document under `docs/plans/` must be reachable from this inventory, directly or through a plan it links. `docs validate` reports one that is not as `KW-DOC-LIFECYCLE-001`.

A plan is open only while it is built. The pull request that finishes it archives it: CI runs `docs validate --merge-ready`, which fails on any plan still in this folder (`KW-DOC-LIFECYCLE-003`).

**One plan never spans several pull requests.** Work that needs more than one is authored as several plans, one per slice: each is carved from the design, approved on the branch that delivers it, and archived in that slice's own pull request. The alternative — one approved design merged in stages — is how a specification once shipped with no closeout, which is the failure `KW-DOC-LIFECYCLE-003` exists to prevent.

**A slice whose base is unmerged is stacked on it, not on `main`.** When a slice edits files the slice before it rewrote, branching from `main` conflicts on the first push, and GitHub runs no `pull_request` workflow at all on a conflicted pull request — so CI never reports and the slice has no verdict. Cut the branch from the unmerged slice's branch, target the pull request at that branch, and retarget it to `main` as the base merges. Do not merge the base into the slice to unblock CI and do not rebase the slice onto `main`: both rewrite a branch whose own review is still open.
**Maintaining this inventory.** Register a new plan by adding a row to **Active Plans** when execution starts. On closeout, `git mv` the plan file to `docs/archive/plans/`, remove its row from **Active Plans** and add one line for it at the top of the table in the [archived plan inventory](../archive/plans/README.md) with its harvest destinations, putting evidence in the plan's own completion summary. Leave **Active Plans** in the empty state when nothing remains open. Reconcile the plan file, frontmatter, body status, and index row in the same change.

A note or excuse for why the process was not followed is not a resolution. The process exists to ensure quality; trying to work around it signals bad quality. Do not put such a note in **Active Plans**. A pull request that does will not be approved.

### Plan Decisions and Closeout Policy

Every plan authored under `docs/plans/` must maintain a permanent `## Decisions` section formatted as a structured table (ID, Question, Answer / decision, Mitigating and supporting information, Status / approval provenance). This section is preserved intact across all lifecycle transitions (Draft -> Ready -> execution -> closeout -> archive); `NO_QUESTIONS` does not bypass it.

Every plan declares its scale at authoring time via `documentation-impact` (`new-component` | `extends-component` | `no-design-change`) with `impact-components`, plus a `Design harvest` table mapping each durable design item to its target component and doc type.

During closeout, durable architectural decisions, contracts, and invariants established by the plan must be harvested into canonical documentation (`docs/`) and/or architectural decision records (`docs/adr/`). The plan's completion summary must provide a closeout mapping linking each durable decision to its destination in canonical documentation.

Closeout explicitly distinguishes between two waiver categories:
- **ADR Waiver**: Permitted when decisions refine existing architecture, adhere to established patterns, or represent implementation details that do not warrant a lasting architectural record. The inventory and plan must state `no ADR — ADR waiver: <reason>`.
- **Documentation Waiver**: Permitted ONLY when an item is purely transient, historical, or test-fixture scoped with zero surviving architectural, operational, or behavioral invariants that future engineers or agents need to consult. "No ADR" is NOT a documentation waiver. "No existing doc describes it" is explicitly rejected as an invalid documentation waiver: canonical documentation must be created or updated.

A plan delivered by a conductor may carry a tracked task file, `docs/plans/<plan>.tasks.json`,
written by `architect` at finalization and committed with the plan at once. Each task carries its
id, role, dependencies, file scope, a `session` group and the full prompt, and each prompt carries
files as path plus symbol, full test class names, exact commands, acceptance criteria and the
standards to fetch. The conductor validates the file, pastes each prompt unchanged and fills only
`{worktree}` and `{prior_results}`. **The plan stays authoritative**: it is what the owner
approves, it holds the `## Decisions` table, and the Arbiter reads task ids, files, dependencies,
skills, task text, test rows and out-of-scope items from it. The file's execution digest covers
the parsed tasks, files, dependencies, skills, test rows, out-of-scope paths and mode, so a status
flip or a Decisions edit does not mark it stale while any task edit does. A file that disagrees
with its plan, a stale digest, a prompt without a command or acceptance criteria, and a prompt
that tells a worker to open a plan each fail validation (`KW-DOC-TASKS-002` to `-005`). A malformed
or wrong-schema file fails `-001`, and a Ready plan with no file warns `-006`.

The task file is transient. Closeout removes it with `git rm` in the same commit that archives the
plan, so it never reaches `main` — this repository squash merges, and nothing durable is lost
because the archived plan keeps the decisions, tasks and test contract while the pull request's
commits stay reachable. A task file left behind fails `docs validate --merge-ready` with
`KW-DOC-LIFECYCLE-005`, which looks under `docs/archive/` too, so a specification folder moved with
its `tasks.json` is caught rather than carried as dead weight.

## Active Plans

No plans are open.

## Archived Plans

Finished plans live in the [archived plan inventory](../archive/plans/README.md), kept out of this file so
that reading the open plans stays cheap. A plan that is still open never belongs there.
