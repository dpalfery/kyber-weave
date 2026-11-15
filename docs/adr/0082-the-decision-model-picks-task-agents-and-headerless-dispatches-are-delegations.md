---
id: adr/0082-the-decision-model-picks-task-agents-and-headerless-dispatches-are-delegations
title: The decision model picks task agents, and headerless dispatches are delegations
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-14
component: KyberArbiter
decided-by: []
supersedes: []
code-refs:
  - TriggerClassifier
  - PlanFacts
  - PlanTaskAgentPick
  - ConfiguredProvider
  - PlanTaskFileGenerator
  - ArbiterAuditCommand
---

# ADR 0082: The decision model picks task agents, and headerless dispatches are delegations

## Status

Accepted, 2026-11-14. Decided by the owner. It amends [ADR 0080](0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent.md)
(who chooses a task's agent) and keeps [ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)
(what may block). The number may move if another branch takes 0082 first.

## Context

The owner's direction is that the Arbiter use the decision model for judgement instead of deciding
everything deterministically. In three full autonomous Copilot CLI runs the model was asked 0, 1 and
0 times. A Copilot CLI root conductor names no caller in the hook payload, so a dispatch counted as a
delegation only when its text carried routing headers (`KYBER-ARBITER`, `PLAN_FILE`, `TASK`). The
conductor, a small model, wrote them in 0 to 17 dispatches per run, and once wrote that routing "is
handled by the harness". Every headerless dispatch to a developer fell through to `investigate`, which
runs only four machine-fact rules, so the twelve model-backed delegation rules never ran.

Separately, the conductor chose each task's agent by reading capability text against agent
descriptions (ADR 0080). That is a judgement the decision model makes well: in a live check it picked
the right agent for six tasks at 0.99 to 1.00 confidence.

## Decision

1. **Identity comes from machine facts, not from headers.** On Copilot CLI, where a sub-agent's payload
   always names its caller, a dispatch with no caller is the root session's. Its dispatch to a Squad
   implementation or documentation agent is a `delegate` (its return a `delegate.returned`) with or
   without headers. Headers, when present, still win: any delegation that wrote a routing header is judged on
   its headers as before. A delegation that wrote none, on any harness, has its identity resolved: when it names no plan, the plan is the one Ready
   or In-progress plan in the run's worktree (the worktree the dispatch names); the task is a task id
   the plan holds, taken from a label at a line start (`TASK_ID: T3`, `TASK: T3`, `Task T3`) or from
   the one id the text names after `task` or `packet`. Anything else leaves the plan or task unknown.
   The fact `delegation.identity` records `headers` or `machine`. Every other harness, and read-only
   and planner targets, keep their triggers.
2. **Unknown identity never blocks.** With identity from machine facts, `KW-ARB-PLAN-001` answers
   `unresolved` when no plan resolves, and `KW-ARB-SCOPE-001`, `KW-ARB-PLANREAD-001`,
   `KW-ARB-GATE-REPORT-001` and `KW-ARB-DIFF-001` answer at step 0 and allow when no plan task is named:
   a closeout, a delivery or a check is rightly not a plan task. `KW-ARB-DIFF-001` also no longer holds a
   documentation agent's non-task return to the plan's out-of-scope list, which is read from prose.
3. **The decision model picks each task's agent, once, when the task file is written.** Finalize
   (`docs_finalize_approved_plan_and_write_task_file`, `docs finalize`) and `docs tasks --write` ask one
   choice question per task without a plan-assigned `role`, one request per task, sent in parallel: the task's title,
   capabilities and files against the name and description of every Squad agent whose profile takes
   implementation or documentation work. A pick at 0.8 confidence or more is written to the task entry
   as `agent` and `agent-confidence`, and the ready queue shows `agent=<name>`. The conductor dispatches
   that agent; with no `agent=` it chooses from the capabilities as before. A plan's `Role:` line still
   wins. A check, and a rewrite of an unchanged task, keep the pick on disk without asking again.
4. **The model is optional.** With provider `none`, a missing key or an unreadable credential store no
   provider is built and no call is made. A fault or timeout costs at most one timeout per task file, because the requests run in parallel, and leaves
   every task without a pick; the file is still written. No Arbiter hook is needed for the pick.
5. **A regression guard.** `arbiter audit` reports how many decision-model evaluations sit behind the
   events in scope and how many dispatches went to implementation agents. When a model is configured,
   such dispatches happened and the model was asked nothing, it reports `KW-ARB-AUDIT-006`. With
   provider `none` it stays silent.

`KW-ARB-OWNER-002` stays shipped disabled: the up-front pick replaces its purpose, and rule ids are never
reused. `KW-ARB-OWNER-001` is unchanged; it judges only an older plan's `Skills` line.

## Consequences

- The delegation rules run on every Copilot CLI writer dispatch: about 0.2 to 0.4 seconds per dispatch
  decision when a task question is asked, one request per decision.
- On Copilot CLI post-dispatch outcomes stay advisory, so only the pre-dispatch delegation rules can stop a
  dispatch there.
- The ready queue's `agent=` makes routing a lookup for the conductor; a below-floor pick or no model
  leaves ADR 0080's behaviour unchanged.
- The candidate agents are the Squad agent files this build embeds, so a new agent file is a new
  candidate with no other change.
- READY-001 cannot see completion markers for a task identified by machine facts, because the ledger
  records headers; it answers `returns-unread`, which allows.
