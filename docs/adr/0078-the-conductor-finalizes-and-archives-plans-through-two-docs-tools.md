---
id: adr/0078-the-conductor-finalizes-and-archives-plans-through-two-docs-tools
title: The conductor finalizes and archives plans through two docs server tools
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: DocGraph
decided-by: []
supersedes: []
code-refs:
  - PlanFinalize
  - PlanCloseout
  - DocsTools
---

# ADR 0078: The conductor finalizes and archives plans through two docs server tools

## Status

Accepted, 2026-11-13. Decided by the owner.

## Context

Two plan bookkeeping steps cost a model dispatch each:

1. **Finalize.** Once a Draft plan is approved, the conductor re-dispatched `architect` with
   `FINALIZE` (about a minute on an expensive model). The architect then recorded the approval in the
   plan's `## Decisions` table, set the frontmatter to `current`, the body status line and the plan
   index row to Ready, ran `kyber-weave docs tasks <plan> --write`, and re-ran the documentation
   checks. Every one of those steps is mechanical. The only judgement is fixing the plan when the task
   file cannot be written from it or the plan fails validation, and that already happens before
   `PLAN_READY`.
2. **Archive.** After review approves, `docs-dev` ran `kyber-weave docs closeout <plan> --archive`.
   The command does all the work. But one dispatch held two different jobs: harvesting the plan into
   canonical documentation (judgement) and archiving it (mechanical). In a real run the archive
   refused for missing closeout evidence, merge-ready validation failed on the still-open plan
   (`KW-DOC-LIFECYCLE-003`, `-005`), and the conductor improvised with `test-dev`, `github-devops` and
   `architect` dispatches that moved plan files by hand: 12 dispatches over 12 minutes.

On GitHub Copilot CLI the conductor has no shell, but every harness's conductor holds the `kw-docs`
server's tools.

## Decision

1. **Two state-changing tools on the docs server.** `docs_finalize_approved_plan_and_write_task_file`
   finalizes an approved Draft plan; `docs_archive_plan_after_closeout_is_complete` archives a plan
   through the same `PlanCloseout` step as `docs closeout --archive`. `kyber-weave docs finalize` is the
   CLI twin of the first. They are the conductor's first state-changing tools.
2. **Mechanical only.** Each tool edits only the plan, its task file, the plan index and the archive,
   and refuses any path that is not a plan file directly under the plans folder. Each refuses,
   changing nothing, when a precondition fails: finalize on a missing or multi-line approval, an
   autonomous approval of a plan whose frontmatter says `autonomy: supervised`, a plan that is not
   Draft, an open decision, a plan the task file cannot be written from, or a result that fails
   validation (it writes, validates, and restores the original bytes on failure), or other files
   being staged. A plan already Ready with a current task file reports done. Amended: in autonomous
   mode finalize accepts any decision still `RECOMMENDED` and answers the approve-and-execute row
   whatever its id, so the conductor never dispatches `architect` just to record the approval; on
   success finalize commits exactly the plan, its task file and the plan index on the current branch
   (never a push), and replies with the ready queue, so no `github-devops` commit dispatch and no
   `architect` briefing dispatch follow it. Archive keeps every check `docs closeout` makes.
3. **Judgement goes back to its owner.** A finalize refusal for plan content goes to `architect` as a
   Draft revision. An archive refusal carries closeout's findings and one line sending them to
   `docs-dev`, and to no other role.
4. **Closeout is two steps.** `docs-dev` harvests and fills the Closeout mapping in one dispatch, and
   checks it with `docs closeout <plan>` (no `--archive`), which names what is missing. Then the
   conductor calls the archive tool, and only after that runs merge-ready validation. Nothing runs
   merge-ready before the archive, so `KW-DOC-LIFECYCLE-003` and `-005` cannot stall the closeout.
5. **The gates are unchanged.** The human approve-and-execute gate is unchanged in supervised mode:
   the conductor calls finalize only after the owner approves. In autonomous mode it calls finalize
   with `AUTONOMOUS_APPROVAL` right after `PLAN_READY`.
6. **The dispatch stays as the fallback.** When the conductor does not hold the tools (the server is
   not wired or does not answer), it re-invokes `architect` with `FINALIZE`, and sends the harvest
   dispatch with `ARCHIVE: true` so `docs-dev` archives itself. The Arbiter keeps its `FINALIZE`
   classification for that path.

## Consequences

- No Arbiter rule changes. The tools are not dispatches, so no dispatch rule fires on them. The
  git-diff rules attribute changed files to dispatches in flight, so the conductor calls both tools
  only while no worker is dispatched, and passes the run's worktree as `projectPath`, so the
  canonical-checkout write guard (`KW-ARB-CHECKOUT-001`) never sees their writes.
- The tools join the `kw-docs` roster in `toolchain.yml`, which every role is entitled to (ADR 0050),
  so a worker on a harness that lists tools by name can see them too. Their refusals keep that
  harmless: neither can edit code or any document except a plan's lifecycle files.
- `docs closeout` now creates the archive folder when it does not exist; before, the first archive in
  a repository with no archived plans failed on the missing folder.
- The architect runs `docs tasks --write` and `--check` before `PLAN_READY`, because the packet
  self-containment warnings come from a model the tools never call.
