---
id: specs/role-based-worker-agents/design
title: Role-based worker agents design
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-11-16
component: KyberSquad
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]
keywords:
  - developer tiers
  - task difficulty
  - technology folder
  - standards templates
  - task prompt tool
  - arbiter rules by folder
  - platform-researcher
---

# Role-based worker agents design

**Phase status:** Draft

## Overview

Replace the eleven per-technology worker agents with three developer agents that differ only in
model strength: `dev-easy`, `dev-medium`, `dev-hard`. Each task carries a difficulty and one
technology. Everything a technology agent knows moves into that technology's folder, which ships
as a template. One docs tool builds the whole prompt for a task. The Arbiter reads its rules from
role and technology folders. `test-dev`, `sql-database-architect`, `azure-reader` and
`github-devops` go away; a read-only `platform-researcher` and a platform-neutral `devops` agent
replace the last two.

The Squad goes from 24 agents to 15. The old agents stay selectable for now; the owner decides
later when they are removed, with a migration path.

This design covers the [requirements](requirements.md). It depends on the technology folder layout
planned on [pull request 625](https://github.com/dpalfery/kyber-weave/pull/625), reverses one
decision of that plan (who fetches the standards), and amends
[ADR 0063](../../adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md),
[ADR 0080](../../adr/0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent.md) and
[ADR 0082](../../adr/0082-the-decision-model-picks-task-agents-and-headerless-dispatches-are-delegations.md).

## Evidence

Read from the tree at `b26ca736` on 2026-11-16. Counts are from text search and are exact unless
marked as estimates.

### The agents today

| Fact | Evidence |
|---|---|
| 24 agents ship | `products/kyber-squad/agents/*.md`. |
| Twelve workers share one tool list and one capability profile | `csharp-dev`, `dal-dev`, `react-dev`, `typescript-dev`, `python-dev`, `go-dev`, `rust-dev`, `maui-dev`, `tauri-dev`, `pulumi-dev`, `sql-database-architect` and `test-dev` name `capability-profile: worker` and one identical `copilot-tools` line. `docs-dev` is `documentation`; `github-devops` is `publishing-worker`. |
| The technology agents differ in model tier and prose only | `fast`: `csharp-dev`, `dal-dev`, `react-dev`, `python-dev`, `maui-dev`, `test-dev`. `general`: `typescript-dev`, `go-dev`, `rust-dev`, `tauri-dev`, `pulumi-dev`. `deep-planning`: `sql-database-architect`. |
| Three agents hold rules stated nowhere else | `tauri-dev` (207 lines) and `sql-database-architect` (182 lines) carry security and platform rules in the body; `csharp-dev` carries a 13-line completion gate. The other technology agents run 67 to 82 lines and mostly point at a standard. |
| Six model profiles exist | `products/kyber-squad/profiles/models.yml`: `architect`, `deep-planning`, `fast`, `general`, `orchestration`, `reviewer`. `orchestration` already defaults to `inherit`, with a named model on five harnesses. |
| `azure-reader` runs no shell | Its profile `read-only` sets `process.execute: deny`. It works through Azure tool servers, not a command line. |
| Fourteen standards templates ship | `products/kyber-squad/standards/`: azure, csharp, data-access-layer, github-actions, go, maui, pulumi, python, react, rust, sql, tauri, test, typescript. `docs init --kyber-standards` seeds them. |

### Developer agent instructions

The text below is the proposed agent file for `dev-medium`. `dev-easy` and `dev-hard` are the same
file with a different `name`, `description` and `model-profile` (`fast` and `dev-hard`). It is
adapted from today's technology agents with every technology removed. The shared worker baseline
that every worker already receives (task scope, task gate commands, build isolation) is unchanged
and is not repeated here.

````markdown
---
schema: kyber-squad.agent/v1
name: dev-medium
description: "Implements application code and its tests in any technology, for a task rated medium: several files or a non-obvious path, no cross-cutting risk. Use only when the host's worker roster is tiered. Do not use for documentation-only work or for pushing, publishing and pipelines."
invocation: subagent
model-profile: general
capability-profile: worker
copilot-tools: [the same list every worker agent carries today]
delegates-to: []
fallback: role-skill
aliases: []
---
# Developer

You implement one task: the code and its tests. You are not tied to a technology. Your task prompt
is a file. It tells you which one technology the task uses and carries that technology's rules,
skills and standard.
Those outrank any habit you bring with you.

## What your task prompt contains

Your dispatch names one file. It holds these parts, each inside its own tag:

1. `<task>`: what to build, the files in scope, the tests the task must add or change.
2. `<host-facts>`: paths, the task gate commands for this technology, the host's own rules.
3. `<shared-rules>`: rules for every technology.
4. `<parent-rules>`: present when this technology builds on another.
5. `<technology-rules>`, `<skills>` and `<standard>` for this task's technology.
6. `<left-out>`: anything the prompt had no room for.

When two parts disagree: technology rules beat parent rules, parent rules beat shared rules, rules
beat skills, and skills beat the standard's general guidance.

## Scope

Your task's files are your scope. Do not edit a file outside them. If the task cannot be done
inside them, stop and report it as an open question. Never edit secrets or deployment credentials.

## Workflow

1. Read the whole prompt file before you touch anything else. Do not open the plan, the
   specification or the task file.
2. If the prompt names a technology and gives no rules or standard for it, stop and report that.
   Do not improvise a standard.
3. If `<left-out>` lists a section you need, fetch it with `docs_get_coding_standard_for_technology`,
   naming the technology and the topic. Fetch nothing else by default.
4. Look up current library documentation for any library you configure or call in a way that is new
   to this repository. Use the standard for which libraries this repository takes.
5. In test-first mode, write the task's tests first, following the standard's Testing section, and
   run the task gate so it fails on them.
6. Implement the change. Match the repository's existing naming and layout unless the standard says
   otherwise.
7. Run the task gate on your changed files. Fix what fails and run it again.
8. Check your change against the standard's Done checklist.
9. Return the completion digest.

## When you cannot finish

Do not loop. After three failed attempts to make the task gate pass, or as soon as you see the task
needs a design decision you cannot make, stop and report what you tried and what failed. A stronger
agent takes the next attempt, and your report is what it starts from. An honest failure is a good
result; a pass you forced is not.

## Discovery order

Use the host's indexes before raw reads.

- Code: `codegraph_explore` first. Do not re-read what it returned.
- Documentation: the docs tools before searching or opening documentation files.
- Read a file directly only for what an index does not cover, or when a tool reports a failure.
  Say so, quoting the error.
- Report discovery in your final report: `Discovery: codegraph N, docs N, direct reads N`.

## Hard rules

- Never report done without a `TASK_GATE:` line from a run on your current changes.
- Never weaken, skip or delete a test to make the gate pass.
- Never hide a command's failure behind another command or a redirect.
- If your change handles a value that arrives from outside the system, fill the input-site table
  before you report done; otherwise write `Input sites: none`.
- A fix for a reported defect covers the whole class of that defect, not the one reported line.

## Completion digest

```text
STATUS: READY_FOR_REVIEW
ARTIFACTS: <files changed or created>
SUMMARY: <2 to 4 sentences: what was built, what was tested, any hand-off>
TASK_GATE: <PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE>, agent=dev-medium gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
OPEN_QUESTIONS: <bullets, or "none">
```
````

Three things differ from a technology agent today: it does not fetch its standard as a first step,
it loads no skill by name, and it has a stated stopping rule that feeds the step-up.

### Difficulty, retries and models today

| Fact | Evidence |
|---|---|
| A difficulty question already exists | `KW-ARB-DIFFICULTY-001` asks the decision model "easy, standard or hard?" with a 0.8 confidence floor and maps the answer through `model-targeting.difficulty-profiles`. |
| A step-up rule already exists | `KW-ARB-LADDER-001` moves a failed attempt one profile up the ladder, from the ledger alone, with no model call. |
| Both are off by default | `arbiter.model-targeting.mode` defaults to `shadow`: the choice is recorded and nothing is delivered. |
| Kyber-Weave sets a model on a dispatch on one harness only | `docs/kyber-arbiter/runbook.md`: the rework dispatch climbs one rung "only where the harness takes a per-dispatch model (Claude); elsewhere the conductor's prose carries the escalation". `architecture.md`: other harnesses record `unsupported-harness`. |
| Nothing rates difficulty with the Arbiter off | No plan or task-file field holds a difficulty. |
| Rework is already capped | `delivery-execution` skill: at most three rework dispatches per failure cluster, then the architect mediates. |
| The agent pick is made when the task file is written | ADR 0082: `PlanTaskAgentPick` asks the decision model once, keeps picks at 0.8 confidence or more, and records them in the task file. |

### Rules, gates and the task prompt today

| Fact | Evidence |
|---|---|
| 36 Arbiter rules ship in one file | `src/KyberWeave.Domain/Arbiter/Rules/default-rules.yml`. Ids are permanent. |
| None is technology-specific | Every trigger is a delegation, planner, review or shortcut event. No rule reads a technology. |
| One rule names a worker | `KW-ARB-MODE-002` answers `not-required` when the target is `test-dev`. |
| A host can already change a rule | `arbiter.rules` in the host configuration sets `shadow` and `effects` per rule id. Seven rules ship in shadow and `KW-ARB-OWNER-002` ships disabled. The two model rules are switched together by `arbiter.model-targeting.mode`, not per rule. |
| The owner rule does not bind current plans | `KW-ARB-OWNER-001` escalates only when a plan carries the older `Skills:` line; a plan that names capabilities answers `no-mapping`, which allows. |
| Five agents list who they may dispatch | `delegates-to` in `conductor` and `bug-crusher` lists every technology agent; `conductor`, `architect` and `code-reviewer` list `azure-reader`. `KW-ARB-ROSTER-001` escalates a dispatch to an unlisted agent. |
| Some dispatches have no task | `bug-crusher` hands a larger fix to a technology agent by name, and `azure-reader` is dispatched by planners and reviewers, both outside any task file. |
| An answer the Arbiter cannot reach allows | [ADR 0072](../../adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md): no clause matched, no provider, an error or a floor not met all allow and are logged. |
| The Arbiter sees a sub-agent's own tool calls on two harnesses | Runbook: "Only Copilot CLI and Claude run the worker guard". |
| Host task gates are keyed by agent name | `review.task-gates` in the host configuration; four call sites look a key up by agent: `ReviewGatesService`, `PacketHostFacts.AppendGates`, `PacketTestCommandRenderer` and `TriggerFactBuilder`. An unknown agent gets a warning, an empty report and exit code 0. |
| Council gates are keyed by gate id | `review.gates`: this repository declares 44, of which 24 are three skill checks repeated for eight skills. They do not name agents. |
| A task packet command exists | `kyber-weave docs packet <task>` prints the task file's prompt plus a host facts section built by `PacketHostFacts` at print time, capped at 6,000 characters of host rules, never written into the task file. |
| Workers fetch standards themselves | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) and decision D3 of the plan on pull request 625: the worker calls `docs_get_coding_standard_for_technology` in its first step. |
| Standards and skills are small | Shipped standards run 4,153 to 10,215 characters (C# is the largest); the nine skills run 997 to 7,487; the four largest agent bodies run 10,352 to 12,858. Host rules in a packet are capped at 6,000 characters. |
| The task file is JSON | `<plan>.tasks.json` and a specification's `tasks.json` (ADR 0063). Host configuration, Arbiter rules, model and capability profiles are YAML. |

### Size of the references

| Area | Files naming a worker agent | Note |
|---|---|---|
| `src/` | 22 | Real coupling to technology agents is `GateCatalogueRenderer` and the family `gates.yml` files. |
| `tests/KyberWeave.Tests` | 165 files, 2,264 occurrences | 144 files name `csharp-dev`, mostly as an example slug. |
| `products/kyber-squad` | 84 | Agents, skills, standards, the bundle, conductor references. |
| Live `docs/` | 39 | `docs/archive` is not edited. |

## Where the evidence is against the idea

Stated so the owner can weigh them. The design follows the owner's direction in each case.

1. **Three agents instead of one is a cost paid for model choice.** The three developer agents are
   one body rendered three times. They exist because choosing an agent is the one way to choose a
   model that works on all thirteen harnesses.

2. **No Arbiter rule is technology-specific today.** Every technology's `<name>.arbiter-rules.yml` starts
   empty. All 36 rules land in three role folders. The technology files earn their place only when
   a rule such as the command allow-list of Requirement 8 is written.
3. **`<name>.rules.md` and `<name>.arbiter-rules.yml` are different things with one name.** The first is prose a
   model reads. The second is evaluated by the Arbiter. A host can read a rule in `<name>.rules.md` and
   assume it is enforced. The template header of each file must say which it is.
4. **One technology per task changes how work is cut.** A change that touches an endpoint and its
   migration becomes two tasks, and the second waits for the first. Plans get more, smaller tasks.

5. **The platform research agent is wider than the agent it replaces.** `azure-reader` cannot run
   a command. `platform-researcher` can, and the allow-list that keeps it read-only is enforced on two
   harnesses and is an instruction on eleven.
6. **Removing `test-dev` removes a second pair of eyes.** Today a different agent can write the
   tests. After this, the developer agent writes code and tests, and the review council is the
   independent check.
7. **Host standards are host-owned.** A rule moved from an agent body into a template reaches new
   hosts only. Requirement 6.4 exists so an existing host can see what it is missing.
8. **Routing was rebuilt three days ago.** ADR 0080 and ADR 0082 moved routing to capabilities and a
   model pick. Under the tiered roster that pick shrinks to "developer, docs or delivery", and
   difficulty picks the tier.

## Architecture

### Roster

| Agent | Status | Capability profile | Model profile |
|---|---|---|---|
| `conductor` | stays | `orchestrator` | none: the session's model |
| `architect` | stays; gains schema and data-model planning | `architect` | `architect` |
| `product-owner` | stays | `product-planning` | `architect` |
| `code-reviewer`, `review-lens`, `review-triage` | stay | unchanged | `reviewer` |
| `bug-crusher`, `bug-crusher-investigator`, `research-agent`, `docs-dev` | stay | unchanged | unchanged |
| `dev-easy` | new | `worker` | `fast` |
| `dev-medium` | new | `worker` | `general` |
| `dev-hard` | new | `worker` | `dev-hard` (new) |
| `platform-researcher` | new; replaces `azure-reader` | `platform-reader` (new) | `fast` |
| `devops` | new; replaces `github-devops` | `publishing-worker` | `general` |
| Ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader`, `github-devops` | stay for now; removed in phase 5 | | |

The three developer agents are one body rendered three times. The body states the workflow and the
rules that hold for every technology, and names no technology. `docs-dev` keeps its own rules
because they follow the documentation ontology and the Config Reg, which a host already customises
through its configuration.

`devops` does what `github-devops` does today (run start, push, pull request, pipeline files) and
names no platform. Its task or the host configuration names one platform, and it receives that
platform folder's rules and skills.

### Difficulty

A task gains an optional line, `Difficulty: easy | medium | hard`. `PlanDocumentParser` reads it and
the task file entry gains `difficulty` and `difficulty-source`.

1. The architect (or `product-owner` for a specification) writes the line. Missing means `medium`,
   source `default`.
2. When the task file is written and a decision model is configured, the writer asks it the question
   and criteria of `KW-ARB-DIFFICULTY-001`, in the same call that picks the agent today. An answer
   at 0.8 confidence or more replaces the architect's rating, source `decision-model`. The rule's
   `standard` answer maps to `medium`.
3. Otherwise the architect's rating stands, source `architect`.

Nothing checks whether a rating is right. A wrong rating costs one attempt, because of the step-up.

### Choosing the agent and stepping up

Under the tiered roster the pick of ADR 0082 chooses the kind of worker (developer, `docs-dev` or
`devops`), and difficulty chooses the tier:

```text
tier = min(hard, rated difficulty + failed attempts at this task)
```

The conductor passes the number of failed attempts at the task to the task prompt tool, which
returns the agent name. The conductor dispatches that agent. The task file records the kind of
worker and the difficulty, never a tier, so a step-up does not contradict it. The same sentence is in the conductor's instructions, so a run with the Arbiter off
behaves the same. The limit of three rework dispatches per failure cluster is unchanged, and at the
limit the architect mediates as today.

`KW-ARB-LADDER-001` moves to the `conductor` role folder unchanged. Its ladder becomes `dev-easy`,
`dev-medium`, `dev-hard`. When the conductor has already dispatched the next tier, the rule's
target equals the rendered profile and it writes nothing.

One rating is used per task. Under the tiered roster the model-targeting step takes the task
file's difficulty as its answer and makes no second model call, so the agent and the model cannot
disagree.

`KW-ARB-OWNER-001` is unaffected: a current plan answers `no-mapping`. The `delegates-to` lists of
`conductor` and `bug-crusher` gain the three developer agents, and those of `conductor`,
`architect` and `code-reviewer` gain `platform-researcher`, so `KW-ARB-ROSTER-001` allows them. The old
names leave those lists in phase 5.

A dispatch with no task, such as a fix `bug-crusher` hands on, starts at `dev-medium` and moves to
`dev-hard` on its second attempt, inside the two attempts that flow already allows.

### Models

The model comes from the agent. Each agent names a model profile in its own definition and the
renderer writes that profile's model for the harness, as today. Nothing in this work assigns a model
at dispatch.

- One profile is added: `dev-hard`. It is separate from `deep-planning` and `architect`, so changing
  a planning model never changes it.
- The conductor has no model profile. `orchestration` is removed and the conductor is rendered with
  no model on every harness; it runs on the model its session was started with.
- No existing profile is renamed, so user presets keep working.

The Arbiter's existing model targeting is untouched. It stays in shadow by default.

### Technology folders and templates

```text
docs/standards/
  shared-rules.md
  <name>/                        one folder per technology, role or platform
    <name>.standards.md          the standard, six headings
    <name>.rules.md              prose rules a model reads
    <name>.skills.md             how to do the work
    <name>.arbiter-rules.yml     rules the Arbiter evaluates; may declare none

  technologies:  csharp/  typescript/  sql-server/  ...
  roles:         conductor/  architect/  reviewer/  devops/
  platforms:     github/  azure-devops/  gitlab/  jira/
```

`<name>` is the folder's own name, so `csharp/` holds `csharp.standards.md`, `csharp.rules.md`,
`csharp.skills.md` and `csharp.arbiter-rules.yml`. Every file says what it is and whose it is when
opened on its own.

The rules file, the skills file and `shared-rules.md` are the plan on pull request 625, which names
them `rules.md`, `skills.md` and `README.md`. This design renames all three with the folder prefix,
and adds the Arbiter rules file, the six headings, the role folders and the platform folders.

The standard's headings, in order:

1. **Naming and layout**: names, versions, project layout, where code goes.
2. **Coding standards**: patterns, error handling.
3. **Testing**: frameworks, what to test, naming, placement, how to run.
4. **Commands**: build, format, lint and test commands.
5. **Security and data handling**.
6. **Done checklist**: what a reviewer checks.

Role folders are declared in a new `ontology.roles` list, so validation tells a role from a
technology. A platform folder is a technology folder; the `devops` agent's task names it the way a
developer task names `csharp`.

Content moves, all additive until phase 5:

- Every technology rule in the eleven technology agents, `test-dev` and `azure-reader` goes to the
  matching template, tracked by the inventory of Requirement 5.5.
- The `test` template's cross-technology rules are copied to `shared-rules.md`; the rest is copied
  to each technology's Testing section. The `test` technology and the `testing` skill stay until
  phase 5, so the old roster keeps working.
- A `sql-server` template is added and takes the rules `sql-database-architect` holds. The `sql`
  template stays until phase 5.
- `azure` gains the skills and rules `azure-reader` holds. An `aws` template is added.
- A `github` platform template takes the `github-actions` standard and the `github-devops` skill.
  `azure-devops`, `gitlab` and `jira` templates are new.

Templates live in `products/kyber-squad/standards/<technology>/`. A contribution is a pull request
that adds one folder; the completeness check of Requirement 5.8 runs on it. Onboarding installs the
folders for the technologies and platforms the user names. `kyber-weave docs standards diff
[technology]` shows how a host folder differs from the current template and writes nothing.

### The task prompt tool

A new tool on the docs server, `docs_build_task_prompt_for_agent_dispatch`, takes the project path
and a task id (and optionally the plan, a prior-results file and the number of failed attempts). It
is built on the service behind `docs packet`, and that command prints the same text.

The tool writes the prompt to one file, `.agent_scratchpad/<task>/prompt.md`, and replies with a
few lines: the file's path, the agent to dispatch, the difficulty and its source, the technology,
and anything left out. The prompt text never enters the conductor's context. The conductor's
dispatch is one short message that names the file; the worker's first step is to read it.

The file holds, in order, each part inside its own tag:

```text
<task>              the task's own prompt from the task file
<host-facts>        as PacketHostFacts builds them today, with gates resolved by technology
<shared-rules>      shared-rules.md
<parent-rules>      the parent technology's rules and skills, when the template names a parent
<technology-rules>  the task technology's rules file
<skills>            the task technology's skills file
<standard>          the task technology's standard
<left-out>          anything the budget cut
```

The tags only mark where a part starts and ends. What is inside stays Markdown, so a heading inside
a standard cannot be mistaken for the start of the next part.

Given a technology instead of a task id, the tool writes the parts from `<shared-rules>` on, for a
dispatch that has no task: a `platform-researcher` request or a fix `bug-crusher` hands on.

The file is in the run's scratch folder, which is not tracked, so it is never committed and the
task file's digest and staleness check are unchanged. `KW-ARB-PLANREAD-001` stops a dispatch that
tells a worker to open a plan, specification or todo file; the prompt file is none of those, and
the rule's criteria gain one line saying so.

**Budget.** The shipped default is 60,000 characters, about 15,000 tokens. Sized from today's files:

| Part | Characters, largest case |
|---|---|
| Task's own prompt | 6,000 (today's cap) |
| Host facts | 6,000 (today's cap) |
| Shared rules, with the cross-technology testing rules | about 5,000 |
| The technology's standard, with its new Testing section | about 15,000 (C# is 10,215 today) |
| The technology's rules, taking what its agent body holds | up to 12,000 (Tauri's agent is 12,858) |
| The technology's skills | up to 3,000 |
| **Total for one technology** | **about 47,000** |
| A parent's rules and skills, where a template names one | up to 10,000 more |

So a task gets everything with nothing cut, and one test proves it for every shipped template with
its parent. Past the budget: rules are never cut; skills are cut before standard sections; the
least relevant to the task text goes first; every cut is listed. A worker may still call
`docs_get_coding_standard_for_technology` for a topic that was left out. The architect and
`product-owner` keep fetching just in time, for a technology a task names.

### One technology per task

A task gains one line, `Technology: <name>`, checked against `ontology.technologies`. Naming two is
a validation error that tells the author to split the task. This keeps every prompt inside the
budget and means a task has one set of gates.

Two cases would otherwise need two technologies:

- **A technology built on another**: React on TypeScript, Tauri on Rust, the data-access layer on
  C#. The template declares one parent with a `builds-on` key in its standard's frontmatter. The
  task names the child and also receives the parent's rules and skills.
- **Unrelated technologies in one change**: an endpoint and its database migration. The architect
  writes two tasks with a dependency. The instruction is added to `architect` and to the
  `product-owner` tasks reference.

### Task gates by technology

One resolver, `TaskGateResolver`, replaces the four lookups by agent name.

1. An agent with its own `review.task-gates` entry uses it (today's path, kept for `docs-dev`,
   `devops` and the old roster).
2. Otherwise `review.technology-gates.<technology>` for the task's technology, when the host
   declares it; else the entry of the technology agent the shipped map names.
3. If nothing resolved and the template names a parent, the parent's gates by the same two steps.
4. A developer agent whose task names a technology and resolves no gate is an error with a non-zero
   exit.

There is no merging of gate lists, because a task has one technology. No gate is added. In this
repository the twelve agent-keyed blocks hold 14 distinct gates and become about six technology
blocks. `review.gates` is untouched.

### Arbiter rules from folders

`default-rules.yml` is split by trigger into three role files. Ids, questions, answers and effects
do not change.

| Role folder | Rules | Count |
|---|---|---|
| `conductor` | `PLAN-001`, `SCOPE-001`, `SCOPE-002`, `READY-001`, `OWNER-001`, `OWNER-002`, `ROSTER-001`, `RUN-001`, `DIFF-001`, `PLANREAD-001`, `GATE-REPORT-001`, `DIFFICULTY-001`, `LADDER-001`, `FAILURE-001`, `CHECKOUT-001`, `MODE-002`, `READONLY-001`, `TRIAGE-001`, `INTAKE-001`, `DOCIMPACT-001`, `TRIM-001` | 21 |
| `architect` | `PLANNER-001`, `PLANNER-DIFF-001` | 2 |
| `reviewer` | `LENS-001`, `LENS-002`, `QUOTE-001`, `PREEX-001`, `CLAIM-001`, `GATE-CORROBORATED-001`, `GATE-001`, `NITPICK-001`, `NITPICK-002`, `DUP-001`, `COMMENT-001`, `SEVERITY-001`, `OVERRIDE-001` | 13 |

Each id above carries the `KW-ARB-` prefix.

Loading order: the shipped role templates; the host's role folders (a host declaration of the same
id wins); for an event that concerns a task, the `<name>.arbiter-rules.yml` of the task's technology
and of its parent; then the host's `arbiter.rules` settings, which keep working as they do today. A file that
fails to parse, or names a trigger or fact the Arbiter lacks, is an `arbiter validate` error and
the shipped rules for that folder are used.

**Is any rule critical?** No rule is locked. Each rule can be turned down to log only through the
switch it has today (`arbiter.rules`, or `arbiter.model-targeting.mode` for the two model rules),
seven already ship that way, and an answer the Arbiter cannot reach allows. Six rules guard the
tree and the run order (`READY-001`, `DIFF-001`, `PLANNER-DIFF-001`, `READONLY-001`,
`CHECKOUT-001`, `ROSTER-001`); the templates keep them as they ship today, and a host may still
turn them down. Their bodies were not all read for this draft; the split task confirms each.

`KW-ARB-MODE-002` loses its `test-dev` clause. It gains a clause on a new machine fact,
`plan.task.files-all-tests`, which is true when every file of the task matches the test paths the
host already declares for its gates.

Technology rule files start empty. The first use is the command allow-list below, which needs one
new trigger, `worker.command`, raised by the existing worker guard.

### The platform research agent

`platform-researcher` takes one technology from its request and receives that folder's
`<name>.skills.md` and `<name>.rules.md` through the task prompt tool, the same way a developer agent
does.

- New capability profile `platform-reader`: `read-only` plus `process.execute: allow`.
- A technology's `<name>.skills.md` covers sign-in, the command-line tools and tool servers to use, query
  patterns and output shaping. Tool servers stay first choice where they exist, as `azure-reader`
  works today.
- A technology's `<name>.arbiter-rules.yml` may declare allowed command forms, for example `az * list`,
  `az * show`, `aws * describe-*`, `aws * list-*`. A command outside them is refused.
- Enforced on Claude and Copilot CLI, where the worker guard sees the call. On the other eleven
  harnesses the allow-list is stated in the prompt and is an instruction only.

`research-agent` stays separate: it reads code and the web and runs no command.

### Roster switch and comparison

A host configuration key `worker-roster` takes `technology` (default until removal) or `tiered`. It
is given again when a task file is written and recorded there; every later reader takes it from the
task file. Under `technology`, a run behaves as today apart from the task prompt.

A runbook defines the comparison: three approved plans, each run once per roster from one base
commit on one harness. It records tasks completed, attempts per task and the tier of each, task
gate failures, review findings by severity, tokens and wall time.

### Removal

Not scheduled. The old agents stay until the owner decides, after the comparison:

- The fourteen agents are deleted from the Squad source; a Squad update removes their rendered
  files only where the lock shows the Squad wrote them unedited.
- Agent-keyed task gates keep resolving through the shipped map for two minor releases;
  `docs validate` warns and names `kyber-weave review migrate-task-gates`, which rewrites them as
  `review.technology-gates` and shows the change first.
- A plan or task file naming a removed agent resolves to the developer agent for the task's
  difficulty and reports the substitution.
- `worker-roster` defaults to `tiered`; `technology` becomes an error that names the change.

### File formats

| File | Today | After |
|---|---|---|
| Host configuration, model and capability profiles, gate catalogue | YAML | unchanged |
| Arbiter rules | YAML, one file | YAML, one file per folder (`<name>.arbiter-rules.yml`) |
| Task file | JSON (`tasks.json`, `<plan>.tasks.json`) | YAML, converted in the phase that already raises its schema version; JSON is read for two minor releases with a warning |
| Standards, rules, skills, agents | Markdown | unchanged |
| The task prompt file | does not exist | Markdown parts inside named tags, written to the untracked scratch folder |
| Arbiter decision log, gate reports | JSON lines, JSON | machine output, not authored; out of scope |

New files use the `.yml` extension, as every YAML file in the repository does.

## Phases

Each phase ships on its own and leaves a working product.

| Phase | Delivers | Depends on |
|---|---|---|
| 1. Technology folders | Six-heading templates with four files each, the rule inventory, `shared-rules.md` testing rules, role folder templates, onboarding by chosen technology, `docs standards diff`. Additive: nothing the old roster reads is deleted | Pull request 625 |
| 2. Task fields and the prompt tool | `Difficulty:` and `Technology:`, the task file change, `TaskGateResolver`, the task prompt tool | Phase 1 |
| 3. Agents | The three developer agents, `platform-researcher`, `devops`, the `dev-hard` profile, the conductor without a profile, the roster switch, the step-up | Phase 2 |
| 4. Arbiter rules from folders | The split of the 36 rules, folder loading, the `MODE-002` change, the command allow-list | Phases 1 and 3 |
| 5. Comparison and removal | The comparison runbook and runs, the owner's decision, removal of the fourteen agents, the `test` technology, the `testing` skill and the `sql` template, and migration | Phases 1 to 4 |

## Data models

| Model | Change |
|---|---|
| Agent frontmatter | `model-profile` becomes optional (the conductor has none); optional `worker-roster: technology \| tiered`. |
| Model profiles | New `dev-hard`; `orchestration` removed. |
| Capability profiles | New `platform-reader`. |
| Host configuration | `worker-roster`; `review.technology-gates`; `ontology.roles`; a task prompt budget. |
| Plan and specification task text | Optional `Difficulty:` and `Technology:` lines. |
| Standard frontmatter | Optional `builds-on: <technology>`. |
| Task file | Schema version plus one; YAML; top-level `worker-roster`; per entry `difficulty`, `difficulty-source`, `technology`. |
| Technology, role and platform folders | The standard is `<name>.standards.md`; new `<name>.arbiter-rules.yml`. |
| Arbiter | Trigger `worker.command`; fact `plan.task.files-all-tests`. |
| Docs tool server | New tool `docs_build_task_prompt_for_agent_dispatch`. |
| Run scratch folder | New untracked file `.agent_scratchpad/<task>/prompt.md`. |

## Error handling

| Case | Behaviour |
|---|---|
| `Difficulty:` holds another value | `docs validate` error naming the task and the three values. |
| No `Difficulty:` line | Treated as `medium`, source `default`. |
| Unknown technology on a task | `docs validate` error naming the task, the value and the declared list. |
| Tiered roster, file-changing task without a technology | `docs validate` error naming the task. |
| `Technology:` names two | `docs validate` error telling the author to split the task. |
| Technology named, folder missing | The prompt says so in that technology's section; the rest is returned. |
| Prompt over budget | Rules kept; skills then standard sections cut; cuts listed. |
| Task file missing or stale | No prompt; the reply names the repair command. |
| Developer agent, no gate resolves | Gate run error, non-zero exit. |
| `<name>.arbiter-rules.yml` unreadable or naming an unknown trigger or fact | `arbiter validate` error; shipped rules used for that folder. |
| Technology folder missing a file, or a standard missing a heading | `docs validate` error naming the folder and what is missing. |
| Command outside a technology's allow-list | Refused where the worker guard runs; an instruction elsewhere. |
| JSON task file | Read, with a warning, for two minor releases. |
| Removed agent named in a plan or task file | Resolved to a developer agent by difficulty; substitution reported. |

## Testing strategy

Test-first, with targeted classes only; no task runs the full suite.

- **New test classes** (estimate: 16 to 20): difficulty parsing, defaulting and validation; the
  decision-model rating and its source; tier computation and step-up; technology parsing and
  validation; `TaskGateResolver`; the prompt tool (order, budget cuts, missing folder, stale task
  file, parity with `docs packet`); folder completeness and the six headings; template install by
  chosen technology; `docs standards diff`; rule loading order and host override; the `MODE-002`
  clause; the command allow-list; the budget test over every template; rendering the conductor
  without a model; developer-agent content (no technology named).
- **Existing tests that change** (estimate: 25 to 40 files before removal): the classes that pin
  the shipped roster, bundle, model profiles, default rules file, task file shape and packet text.
- **Removal** (estimate: 40 to 60 more files): tests that use a removed agent as an example slug
  where the slug must name a shipped agent.

## Sizes

Estimates, for planning only.

| Phase | Source | Tests | Docs and prompts |
|---|---|---|---|
| 1. Technology folders | 6 to 9 files | 4 new classes, 4 to 6 changed | 15 template folders reshaped, 4 role folders, 4 platform folders, 1 inventory, 4 docs |
| 2. Task fields and the prompt tool | 12 to 16 files | 6 new classes, 10 to 15 changed | 4 prompt files, 4 docs |
| 3. Agents | 6 to 9 files | 3 new classes, 8 to 12 changed | 5 new agents, 13 rendered sets, 4 docs |
| 4. Arbiter rules from folders | 8 to 12 files | 4 new classes, 6 to 10 changed | 3 rule files, 3 docs |
| 5. Comparison and removal | 10 to 15 files | 40 to 60 changed | 14 agents deleted, 1 runbook, 39 live docs, 84 product files |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What replaces the technology agents? | Three developer agents by difficulty: `dev-easy`, `dev-medium`, `dev-hard`. | Server and client agents were mechanically identical once technology moved to the task; model strength is the one real difference. | user: explicit decision 2026-11-16 |
| D2 | Who rates difficulty? | The architect; the decision model instead when the Arbiter runs with one. No gate checks the rating. | The decision model is asked when the task file is written, in the call that already picks the agent, so the rating is recorded once. | user: explicit decision 2026-11-16; timing is autonomous, pending owner approval |
| D3 | What happens when a task fails? | The next attempt goes one tier up; the three-dispatch limit stays. | The owner chose the existing one-step rule over a jump to `dev-hard` on the third try. | user: explicit decision 2026-11-16 |
| D4 | Which agents are removed? | The ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader`, `github-devops`. | `docs-dev` stays: it follows the documentation settings a host already customises. | user: explicit decision 2026-11-16 |
| D5 | Are the old agents deleted at once? | No. They stay selectable for now; removal waits for the owner's decision after a comparison. | Keeps every earlier phase additive. | user: explicit decision 2026-11-16 |
| D6 | How is the model assigned? | By the agent: each agent names a model profile, as today. Nothing assigns a model at dispatch. The conductor has no profile. | Run-time assignment was drafted and removed by the owner. Choosing an agent works on all thirteen harnesses. | user: explicit decision 2026-11-16 |
| D7 | Which model profiles change? | `dev-hard` is added, separate from the planning profiles. `orchestration` is removed. Nothing is renamed. | `dev-easy` uses `fast` and `dev-medium` uses `general`, so no preset breaks. | user: `dev-hard` separate and no conductor profile, 2026-11-16; reuse of `fast` and `general` is autonomous, pending owner approval |
| D8 | What does a technology or role folder hold? | `<name>.standards.md`, `<name>.rules.md`, `<name>.skills.md`, `<name>.arbiter-rules.yml`. | `<name>.rules.md` and `<name>.skills.md` are the plan on pull request 625; that plan's `README.md` standard is renamed. | user: explicit decision 2026-11-16 |
| D9 | What are the standard's headings? | Naming and layout; Coding standards; Testing; Commands; Security and data handling; Done checklist. | | user: explicit decision 2026-11-16 |
| D10 | Where do the 36 Arbiter rules go? | Into `conductor`, `architect` and `reviewer` role folders, split by trigger. | The owner named conductor and architect. `reviewer` is added because 13 rules are review rules. | user: explicit decision 2026-11-16; `reviewer` folder is autonomous, pending owner approval |
| D11 | Is any Arbiter rule locked? | No. | The owner's view, and the code agrees: every rule can already be set to log only. | user: explicit decision 2026-11-16 |
| D12 | Who builds the task prompt, and how does it reach the worker? | A docs tool the conductor calls. It writes the prompt to a file in the run's scratch folder and replies with the path; the worker reads the file. | Reverses decision D3 of the plan on pull request 625. Deterministic, the worker cannot skip it, and about 12,000 tokens per dispatch stay out of the conductor's context. | user: explicit decision 2026-11-16 |
| D13 | How are task gates keyed? | By the task's one technology, then its parent, with a fallback to the old agent-keyed entries. | No agent is named after a technology any more. No gate is added and no lists are merged. | autonomous: recommendation, pending owner approval |
| D14 | Which file format? | YAML for every data file added or changed, the task file included, extension `.yml`. | The owner asked for one format and approved converting the task file. `.yml` matches every YAML file in the repository. | user: explicit decision 2026-11-16; extension is autonomous |
| D15 | What replaces `azure-reader`? | `platform-researcher`, which loads the skills and rules of the technology its task names. | Needs a new capability profile that can run commands. | user: explicit decision 2026-11-16 |
| D16 | Where does testing guidance live? | A Testing section in every standard; cross-technology rules in `shared-rules.md`. | `test-dev` and the `test` technology are removed. | user: explicit decision 2026-11-16 |
| D17 | How many specifications? | One, in five phases. | The owner's direction. | user: explicit decision 2026-11-16 |
| D18 | Does this need ADRs? | Yes: one for tiers, difficulty and the task prompt (amending ADR 0080 and ADR 0082), one for rules by folder, one for the task file format (amending ADR 0063). | Each is a contract hosts depend on. | autonomous: recommendation, pending owner approval |
| D19 | What replaces `github-devops`? | A platform-neutral `devops` agent with a `devops` role folder and one platform folder each for GitHub, Azure DevOps, GitLab and Jira. | The agent names no platform; the task or the host configuration does. | user: explicit decision 2026-11-16 |
| D20 | How large is the prompt budget? | 60,000 characters by default; a task must fit with nothing cut. | Sized from today's files at about 47,000 for the largest case, 57,000 with a parent. | autonomous: recommendation, pending owner approval |
| D21 | How many technologies may a task name? | One. A template may name one parent it builds on; unrelated technologies are separate tasks. | Keeps the prompt inside the budget and gives a task one set of gates. | user: explicit decision 2026-11-16 |
| D22 | How are the prompt's parts separated? | Named tags around each part; Markdown inside. | The one-format rule covers stored data files; the prompt is text for a model. | user: explicit decision 2026-11-16 |
| D23 | Is there a test role folder? | No. | Testing lives in the shared rules and each standard's Testing section. | user: explicit decision 2026-11-16 |

## Open questions

1. **Order against pull request 625.** Phase 1 cannot start before that plan lands, with three
   changes: its decision D3 (the worker fetches), its file names, and the tool's output going to a
   file. Recommendation: change that plan first, then land it.

## Scale

`documentation-impact: extends-component`, `impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]`.

## Design harvest

| ID | Design item (section) | Target component | Target doc type |
|---|---|---|---|
| H1 | The roster, the developer tiers and the removed agents (Roster) | KyberSquad | architecture |
| H2 | Difficulty, its sources and the step-up (Difficulty; Choosing the agent and stepping up) | KyberSquad | architecture |
| H3 | The `dev-hard` profile and the conductor without a profile (Models) | KyberSquad | reference |
| H4 | Technology and role folders, the six headings, templates and contribution (Technology folders and templates) | DocGraph | reference |
| H5 | Installing templates and comparing a host folder with its template (Technology folders and templates) | DocGraph | onboarding |
| H6 | The task prompt tool, its order and budget (The task prompt tool) | DocGraph | runbook |
| H7 | Technology-resolved task gates and `review.technology-gates` (Task gates by technology) | ReviewCouncil | architecture |
| H8 | Rules by folder, loading order and overrides (Arbiter rules from folders) | KyberArbiter | architecture |
| H9 | The platform research agent, the `devops` agent and platform folders (The platform research agent; Roster) | KyberSquad | runbook |
| H10 | The roster switch and the comparison procedure (Roster switch and comparison) | KyberSquad | runbook |
| H11 | Migration from agent-keyed gates and removed agent names (Removal) | ReviewCouncil | runbook |
| H12 | Tiers and the task prompt; rules by folder; the task file format (Decisions D1, D10, D12, D14, D18) | KyberSquad | adr |
