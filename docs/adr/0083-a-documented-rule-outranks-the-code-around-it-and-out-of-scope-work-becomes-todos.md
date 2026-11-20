---
id: adr/0083-a-documented-rule-outranks-the-code-around-it-and-out-of-scope-work-becomes-todos
title: A documented rule outranks the code around it, and out-of-scope work becomes a pull request note
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-20
component: DocGraph
decided-by: []
supersedes: []
code-refs:
  - RunDelivery
  - PullRequestDescription
  - RunEvidence
---

# ADR 0083: A documented rule outranks the code around it, and out-of-scope work becomes a pull request note

## Status

Accepted, 2026-11-14. Decided by the owner after a live run. It amends the worker scope rule of
[ADR 0080](0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent.md) and adds a
section to the pull request format of [ADR 0081](0081-the-conductor-delivers-a-run-through-one-docs-tool.md).
The review-flow side (severity of placement, the fix round) is
[ADR 0079](0079-the-reviewer-starts-its-own-seats-where-sub-agents-can-dispatch.md), decisions 11 to 13.
Amended 2026-11-20 by the owner: agents never start a todo. Out-of-scope work, pre-existing debt and risks whose fix the request did not ask for become pull request notes; a todo is written only when a human says so. The `change-scope` skill is the one home of the scope rule.

## Context

In a live Copilot CLI run the architect placed a new transfer class in the Domain layer because the
existing code already kept several DTOs there; the repository's documented rule puts shared DTOs in a
contracts project. The architect followed the code, the reviewer followed the rule. Nothing said which
wins, and nothing said what to do with the older misplaced classes. Separately, worker prose told
each developer to fix every finding in the files it touched, which pulls unrelated debt into a task
and hides it from the person reviewing the change.

## Decision

1. **The documented rule wins over the code around it.** New work follows a documented rule or
   standard even where existing code contradicts it, and never copies the divergent pattern. The
   architect records each divergence (the rule and the offending paths) and decides by the size of
   the debt: fix it in this plan when it is small and in the same files, otherwise list it as a
   `PR note:` line under the plan's out-of-scope boundaries. Workers follow the rule and report the conflict as an open item.
2. **Existing code that breaks a documented rule is a finding.** When the change touches or extends
   that area and the code carries no comment marking it as a deliberate exception, a lens reports it
   (`major` for structural placement). One in a file the change did not touch is marked
   `Pre-existing debt:`, and the reviewer drops it as `pre-existing debt`, so it blocks nothing and
   becomes a pull request note. A commented exception is respected.
3. **Out-of-scope work is noted, not done.** A worker never fixes an issue outside its task's files or
   unrelated to its task: it reports one `Open item:` line each. It still fixes every diagnostic its
   own change introduced. The conductor keeps a notes collection of open items, the plan's `PR note:` lines and the
   verdict's `follow-up:` and `open:` lines, deduplicated; an open item naming a stale document goes to
   `docs-dev` and is fixed in the run. No agent files a todo on its own: a todo is written only when a
   human says so.
4. **The pull request lists them and the user decides.** The conductor passes the notes collection
   in the deliver tool's `notes`, and the final report lists it. A todo a human asked for is still
   committed by the deliver tool and listed under "Follow-up work filed". The conductor never starts
   another run for the notes; the owner decides.

## Consequences

- A run no longer grows to fix whatever its workers happened to see; the debt it found is visible in
  the pull request notes and waits for the user's go-ahead.
- The deliver tool no longer refuses uncommitted files under the todo folder; it commits them.
- A todo a human asked for during planning is committed by the deliver tool with the run, not by the
  finalize tool, which commits only the plan, its index row and its task file.
