---
id: todo/arbiter-user-level-judging-agy-opencode
title: The user-level Arbiter hook judges Antigravity, OpenCode, Factory, Cursor and Pi dispatches
doc-type: todo
component: KyberArbiter
status: draft
owner: dpalfery
last-reviewed: 2026-11-19
---

# The user-level Arbiter hook judges Antigravity, OpenCode, Factory, Cursor and Pi dispatches

## Where it stands

Since 2026-11-19 the user-level hook (`--scope global`) **inserts the task prompt** into an Antigravity,
OpenCode, Factory (droid), Cursor or Pi dispatch when the project enables Arbiter and has no project hook of
its own for that harness (`ProjectDispatchHookProbe`). A live dispatch verified it on each: agy 1.5.12,
OpenCode 2.3.0, droid 1.6.0, Cursor 2026.11.19 and Pi 1.5.2. It does
**not judge** the dispatch: no Arbiter rule runs, nothing is recorded, and such a project stays ungated on
those harnesses, as `arbiter doctor` reports. The owner chose insert-only on 2026-11-19.

## What judging means

The user-level hook evaluates the dispatch under the project's configuration, as it already does for Claude
([ADR 0070](../adr/0070-the-user-level-claude-hook-evaluates-unless-a-project-hook-covers-it.md)), and records
the decisions in the project's `artifacts/arbiter/` ledger:

- **Before the dispatch:**
  - the roster and owner rules: `KW-ARB-ROSTER-001`, is the target in the caller's `delegates-to`;
    `KW-ARB-OWNER-001`, does the task name the target as its specialist;
  - model-tier targeting: `KW-ARB-DIFFICULTY-001` and `KW-ARB-LADDER-001`.
- **After the dispatch returns:**
  - `KW-ARB-DIFF-001`, files changed outside the task's work;
  - `KW-ARB-CHECKOUT-001`, stray writes to the canonical checkout while the run works in its own worktree.

Antigravity's `PostToolUse` carries no sub-agent result (`ObservesReturns` is false), so the after-dispatch
rules there can only read git facts, never the worker's report. The design has to say which of them still
apply.

## Why it was deferred

[ADR 0070](../adr/0070-the-user-level-claude-hook-evaluates-unless-a-project-hook-covers-it.md) §5 keeps the
deferral for every harness except Claude. Only Claude has a measured per-dispatch probe for "does a project
hook cover this dispatch".

- **Double judging.** If a project hook also fires, the dispatch is judged twice. The decision log counts
  repeats, and a repeat count of two stops the task.
- **Blocking live runs.** A user-level judge would start blocking dispatches that pass today. A live probe on
  2026-11-19 escalated a dispatch through `KW-ARB-ROSTER-001` as soon as the hook evaluated it. That risks
  stopping the owner's live agy runs.

Prompt insertion has neither risk. It is idempotent: a dispatch that already carries the prompt is left alone.

## Done looks like

1. **Measure coverage on each harness.** For each of the five, establish when a project hook covers a
   dispatch, recorded in the [Arbiter architecture](../kyber-arbiter/architecture.md) with the live evidence:
   - whether both the user-level and the project hook fire on one dispatch, and in which order (OpenCode
     loads the global plugin before the project plugins);
   - whether agy skips a project `.agents/hooks.json` it cannot parse;
   - whether each harness gives a call id stable enough to claim on (`toolCall.id` on agy, `id` on OpenCode).
2. **Extend the per-dispatch probe.** Give each adapter a `CoverageQuery` and `ClaimKey`, so the user-level
   hook takes the same claim path as Claude:
   - no covering project hook: claim and evaluate;
   - a covering hook: wait for its claim or defer.
3. **Prove it live.** Run one marked dispatch per harness:
   - a project without a hook: judged once, recorded;
   - a project with a hook: judged once, by the project hook.
4. **Record the decision.** Amend ADR 0070 §5, or write a successor ADR.
