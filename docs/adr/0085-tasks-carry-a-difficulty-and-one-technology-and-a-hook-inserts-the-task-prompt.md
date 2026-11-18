---
id: adr/0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt
title: Tasks carry a difficulty and one technology, and a hook inserts the task prompt
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-17
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - PlanDocumentParser
  - PlanTaskFileGenerator
  - PlanTaskAgentPick
  - TaskPromptService
  - DispatchPromptInsertion
  - TaskGateResolver
  - DocsTools
---

# ADR 0085: Tasks carry a difficulty and one technology, and a hook inserts the task prompt

## Status

Accepted, 2026-11-17. Decided by the owner. It amends
[ADR 0080](0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent.md) (what a
task declares and who loads its standards) and
[ADR 0082](0082-the-decision-model-picks-task-agents-and-headerless-dispatches-are-delegations.md)
(the decision-model call that picks an agent now rates the task too). The tracked task file of
[ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) still holds the
prompt and the plan stays authoritative; its stored form is
[ADR 0086](0086-the-task-file-is-yaml.md).

## Context

A task named its capabilities and the conductor dispatched the agent whose description fitted
them ([ADR 0080](0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent.md)), or
the decision model made that pick when the task file was written
([ADR 0082](0082-the-decision-model-picks-task-agents-and-headerless-dispatches-are-delegations.md)).
Nothing on a task said how hard it was or which technology it was written in. A worker fetched its
own standard at the start, and the conductor either retyped the prompt or told the worker to fetch
it. Three problems followed:

- A wrong rating could not be detected, because there was no rating and no field to carry one.
- A task could touch several technologies, so one prompt could not carry one set of rules, skills
  and gates.
- Whoever assembled the prompt paid for it: either the conductor read and retyped the plan text,
  or every worker read a plan it did not need.

## Decision

1. **A task carries an optional difficulty.** `Difficulty: easy | medium | hard` is read by
   `PlanDocumentParser` only at a line start, so prose that mentions difficulty is not a rating. A
   task with no line is `medium` with source `default`; a line is source `architect`. Any other
   value is `KW-DOC-TASKS-015`, naming the task and the three legal values. No gate checks whether
   the rating is right: a wrong rating costs one attempt when the next attempt steps a tier up.

2. **A task names one technology.** `Technology: <name>` is checked against
   `ontology.technologies`. An undeclared name is `KW-DOC-TASKS-016`; a second technology, written
   with a separator or the word "and", is `KW-DOC-TASKS-017`, which tells the author to split the
   task into one task per technology with a dependency between them. Under the tiered roster a
   developer task with no technology is `KW-DOC-TASKS-018`; a documentation or delivery task needs
   none. A standard may name one parent with `builds-on` in its frontmatter, so a task written in
   React on TypeScript, or Tauri on Rust, also receives the parent's rules and skills.

3. **When a decision model is configured, it rates the task.** `docs tasks --write` and the
   finalize tool ask one question per task that has no plan-assigned role. The same request carries
   the agent pick of [ADR 0082](0082-the-decision-model-picks-task-agents-and-headerless-dispatches-are-delegations.md)
   and the shipped `KW-ARB-DIFFICULTY-001` question. An answer at 0.8 confidence or more, and one
   of the three answers the question offered, replaces the architect's rating and records source
   `decision-model`; the rule spells the middle answer `standard`, which maps to `medium`. Any
   other answer, a lower confidence, no provider or a provider fault leaves the architect's rating
   and source untouched. The rating is recorded once for the attempt.

4. **The task file carries both fields, and the prompt is not stored in it.** Each entry gains
   `difficulty`, `difficulty-source` and `technology`. The prompt is built at dispatch time and is
   never written into the task file or any tracked file, so the execution digest and staleness
   check of [ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) are
   unchanged.

5. **One service builds the prompt, and three callers use it.** `TaskPromptService` is used by the
   dispatch hook, by `docs packet` and by the docs tool. It reads the task file, which is checked
   against its plan first, and then the one standards lookup the standards tool also uses. The
   prompt holds these parts in order, each inside its own tag, with Markdown inside the tag:

   ```text
   <task>              the task's own prompt from the task file
   <host-facts>        the scratchpad, the Config Reg paths, the gates resolved for the technology, and the host's rules
   <shared-rules>      shared-rules.md
   <parent-rules>      the parent technology's rules and skills, when the standard names a parent
   <technology-rules>  the task technology's rules
   <skills>            the task technology's skills
   <standard>          the task technology's standard
   <left-out>          anything the budget cut
   ```

   A dispatch that names a technology and no task gets the parts from `<shared-rules>` on. A
   technology with no installed folder says so in that part and the rest is still built. The
   default budget is 60,000 characters, set by the top-level `task-prompt-budget` key. Past it,
   rules are never cut; the parent skills and the technology skills are cut first, then the
   standard's sections, least relevant to the task text first; every cut is named in `<left-out>`.

6. **A dispatch hook inserts the prompt into the dispatch.** The conductor dispatches with a
   leading header block — `TASK`, `TECHNOLOGY`, `PLAN_FILE`, `PRIOR_RESULTS`, `FAILED_ATTEMPTS` as
   needed — and any text below it. On a harness whose hook can rewrite the call, the hook parses
   that block, asks the prompt service to build the prompt, and returns the prompt with the text
   below the block kept unchanged, so a retry's failure report still reaches the worker. The
   conductor never holds the prompt text. The hook is installed with the Squad wiring whether or
   not the Arbiter is enabled; with the Arbiter on, the rules judge the short dispatch first and
   the prompt is inserted after them. A prompt that cannot be built stops the dispatch with a
   reason that names `kyber-weave docs packet <task id>`, so no worker starts with only a task id.
   A dispatch that already holds an inserted prompt is left alone, so a second hook cannot wrap it
   in itself.

7. **A harness whose hook cannot rewrite the call gets a file.** The prompt is written to
   `.agent_scratchpad/<task>/prompt.md`, the untracked scratch folder for that task, and the
   dispatch names the file; the worker reads it first. The file is part of the delivery: a file
   that cannot be written is a refusal naming what to check, not a dispatch without a prompt. Which
   path a harness takes is the delivery table in
   [the Arbiter architecture](../kyber-arbiter/architecture.md#dispatch-delivery-path): Claude,
   Copilot in VS Code, Copilot CLI, OpenCode, Pi, Codex, Cursor, Kilo, Factory and Devin rewrite
   the dispatch; Antigravity, ZCode, CommandCode and Warp take the file.

8. **The conductor asks a docs tool which agent to dispatch.** The read-only
   `docs_build_task_prompt_for_agent_dispatch` tool takes a task id (or a technology for a
   dispatch with no task), the plan when more than one is active, the prior-results file, the
   failed-attempt count and the harness. It replies with the agent, the difficulty and its source,
   the technology, what the budget left out and, on a file harness, the prompt file's path. It
   never returns the prompt text.

9. **Task gates resolve by technology.** `TaskGateResolver` reads the agent's own `review.task-gates`
   entry first, then `review.technology-gates.<technology>`, then the entry of the technology agent
   the shipped map names, then the same two steps for the parent technology. A task that names a
   technology and resolves nothing is refused with a non-zero exit and a message naming both
   places to declare its gates. No lists are merged and no gate is added; the council's
   `review.gates` is not read here.

### What this changes in ADR 0080 and ADR 0082

- **ADR 0080 decision 1 gains a field.** A task still names capabilities in plain words; it now
  also names its difficulty and its one technology.
- **ADR 0080 decision 4 changes.** A worker no longer has to load its standards or skills to
  start: its prompt carries the technology's rules, skills and standard. It may still call
  `docs_get_coding_standard_for_technology` for a topic the budget left out, but fetching is no
  longer the first step and the packet is not a pointer at files to open. Consequences judged by
  the conductor from capabilities are unchanged for a task with no recorded agent.
- **ADR 0082 decision 3 gains a question, not a second call.** The decision model still picks each
  unassigned task's agent once, in one request per task, and a pick at 0.8 or more is still
  written as `agent` and `agent-confidence`. The same request also carries the difficulty
  question, whose accepted answer is written as `difficulty` with source `decision-model`.
- **The rest of both records stands:** role and plan text still win, a below-floor answer still
  leaves the conductor to choose, and an unreachable model still writes a task file with no picks.

## Consequences

- A plan gets more, smaller tasks, because one technology per task means an endpoint and its
  migration are two tasks with a dependency. The cost buys one prompt, one set of gates and one
  set of rules per task.
- The conductor never reads about 12,000 tokens of prompt per task, and a worker starts with its
  whole brief. The prompt is not a file on a rewrite harness, so there is nothing to skip and
  nothing to clean up.
- On a file harness the prompt is untracked and ephemeral. It survives only until the task ends,
  and a worker that finds no inserted prompt runs `docs packet` itself.
- The rating is a judgement with no correctness gate. A wrong one costs one attempt, and the
  step-up rule of the tiered roster is what corrects it.
- The prompt budget is the cap on what a task may carry. Rules are never cut, so a task that
  cannot fit its rules is a signal that the task names too much, not that the budget should grow.
- The `FAILED_ATTEMPTS` header is parsed and passed to the prompt service, which does not yet put
  the count into the prompt; the tier step-up that reads it lands with the tiered roster. Until
  then the count changes neither the prompt nor the agent.

## Related

- [ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md) — the tracked task file, its digest and the plan's authority
- [ADR 0064](0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) — project knowledge reached through tools rather than portable files
- [Kyber-Squad architecture](../kyber-squad/architecture.md#the-tracked-task-file-and-the-inserted-task-prompt) — the task file, the prompt service and the delivery paths
- [MCP runbook](../docgraph/mcp-runbook.md#docs_build_task_prompt_for_agent_dispatchtaskid--null-technology--null-plan--null-priorresults--null-failedattempts--0-harness--null-projectpath--null) — the dispatch tool's reply
- [Configuration](../configuration.md#task-prompt-budget) — the prompt budget and technology-keyed task gates
- [The Arbiter's dispatch delivery path](../kyber-arbiter/architecture.md#dispatch-delivery-path) — which harness rewrites and which reads a file
