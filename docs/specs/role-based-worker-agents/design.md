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

**Phase status:** Approved

Owner approved 2026-11-16 ('Yes, approve'), given directly in the authoring session. Amended 2026-11-16 per owner direction ('fold 625 into phase 1 and close 625'): the standards plan first drafted on pull request 625 is absorbed here and that pull request is closed. Amended 2026-11-17 per the owner's decisions: the old agents are removed without a comparison ([ADR 0088](../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)), `dev-hard` resolves to each harness's stronger coding model, the task file records the developer tier, `docs standards migrate` reshapes a standard, and the harness table records the delivery path as built.

## Overview

Replace the eleven per-technology worker agents with three developer agents that differ only in
model strength: `dev-easy`, `dev-medium`, `dev-hard`. Each task carries a difficulty and one
technology. Everything a technology agent knows moves into that technology's folder, which ships
as a template. One docs tool builds the whole prompt for a task. The Arbiter reads its rules from
role and technology folders. `test-dev`, `sql-database-architect`, `azure-reader` and
`github-devops` go away; a read-only `platform-researcher` and a platform-neutral `devops` agent
replace the last two.

The Squad went from 24 agents to 15. The fourteen per-technology agents are removed now, on the
owner's decision of 2026-11-17: the comparison this design planned was not used to decide, and
anyone who wants the old agents installs an older version
([ADR 0088](../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)). The
roster switch is gone, so no run can choose between two sets of workers.

This design covers the [requirements](requirements.md). It absorbs the standards plan first
drafted on [pull request 625](https://github.com/dpalfery/kyber-weave/pull/625), which is closed,
changes one decision of that plan (who fetches the standards), and amends
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
worker-roster: tiered
---
# Developer

You implement one task: the code and its tests. You are not tied to a technology. Your task prompt
tells you which one technology the task uses and carries that technology's rules, skills and
standard.
Those outrank any habit you bring with you.

## What your task prompt contains

It holds these parts, each inside its own tag. If instead it is one line that names a prompt file,
read that file first; it holds the same parts.

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

1. Read the whole task prompt before you touch anything. Do not open the plan, the specification
   or the task file.
2. If your prompt is only a task id, with no `<task>` part, the prompt was not delivered. Run
   `kyber-weave docs packet <task id>` and use its output as your prompt. If that fails, stop and
   report. Never work from a task id alone.
3. If the prompt names a technology and gives no rules or standard for it, stop and report that.
   Do not improvise a standard.
4. If `<left-out>` lists a section you need, fetch it with `docs_get_coding_standard_for_technology`,
   naming the technology and the topic. Fetch nothing else by default.
5. Look up current library documentation for any library you configure or call in a way that is new
   to this repository. Use the standard for which libraries this repository takes.
6. In test-first mode, write the task's tests first, following the standard's Testing section, and
   run the task gate so it fails on them.
7. Implement the change. Match the repository's existing naming and layout unless the standard says
   otherwise.
8. Run the task gate on your changed files. Fix what fails and run it again.
9. Check your change against the standard's Done checklist.
10. Return the completion digest.

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
| Workers fetch standards themselves | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) and the plan first drafted on pull request 625: the worker calls `docs_get_coding_standard_for_technology` in its first step. |
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
9. **Delivery is GitHub-only in code.** The run delivery tool pushes a branch and opens a pull
   request on GitHub. The Azure DevOps, GitLab and Jira folders give the `devops` agent rules and
   skills, but no product code delivers to those platforms. That is instruction only until a
   later specification adds it.

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
| Ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader`, `github-devops` | removed 2026-11-17 | | |

The three developer agents are one body rendered three times. The body states the workflow and the
rules that hold for every technology, and names no technology. `docs-dev` keeps its own rules
because they follow the documentation ontology and the Config Reg, which a host already customises
through its configuration.

One roster ships, so an agent's `worker-roster` key is optional and means nothing more than "this
agent serves the one roster": the five new agents declare `tiered`, and an agent with no key is
dispatchable the same way. `worker-roster: technology` names the roster the removal deleted.


`devops` does what `github-devops` does today (run start, push, pull request, pipeline files) and
names no platform. Six source files name `github-devops` for run start and delivery
(`RunStartEvidenceRules`, `ArbiterRunStartRemedy`, `RunWriterProfiles`, `GateCatalogueRenderer`,
`RunDeliveryService`, `RunDelivery`); each accepts `devops` under the tiered roster. Its task or the host configuration names one platform, and it receives that
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

The pick of ADR 0082 chooses the kind of worker (developer, `docs-dev` or `devops`), and difficulty
chooses the tier:

```text
tier = min(hard, rated difficulty + failed attempts at this task)
```

The conductor passes the number of failed attempts at the task to the task prompt tool, which
returns the agent name. The conductor dispatches that agent. The task file records the developer
tier the architect's rating gives — `dev-easy`, `dev-medium` or `dev-hard` — beside the difficulty
and its source, not the kind of worker, so a reader of the file sees the first attempt the run will
make. A step-up then dispatches a higher tier than the file names, and the file is not rewritten,
so the rating it holds stays the architect's. The same sentence is in the conductor's instructions,
so a run with the Arbiter off behaves the same. The limit of three rework dispatches per failure
cluster is unchanged, and at the limit the architect mediates as today.

`KW-ARB-LADDER-001` moves to the `conductor` role folder unchanged. Its ladder becomes `dev-easy`,
`dev-medium`, `dev-hard`. When the conductor has already dispatched the next tier, the rule's
target equals the rendered profile and it writes nothing.

One rating is used per task. The model-targeting step takes the task file's difficulty as its
answer and makes no second model call, so the agent and the model cannot disagree.

`KW-ARB-OWNER-001` is unaffected: a current plan answers `no-mapping`. The `delegates-to` lists of
`conductor` and `bug-crusher` name the three developer agents, and those of `conductor`,
`architect` and `code-reviewer` name `platform-researcher`, so `KW-ARB-ROSTER-001` allows them. The
removal took the old names out of those lists.

A dispatch with no task, such as a fix `bug-crusher` hands on, starts at `dev-medium` and moves to
`dev-hard` on its second attempt, inside the two attempts that flow already allows.

### Models

The model comes from the agent. Each agent names a model profile in its own definition and the
renderer writes that profile's model for the harness, as today. Nothing in this work assigns a model
at dispatch.

- One profile is added: `dev-hard`. Each of its rows is the model the `architect` profile pins on
  that harness — the harness's stronger coding model — so stepping up to `dev-hard` is a real step
  up on every harness. It stays a profile of its own rather than an alias: the values are the
  same today, and a later rotation of a planning model changes `architect` and leaves `dev-hard`
  alone.
- On GitHub Copilot the owner chose a different model for each tier, so the three values are
  visible where they differ most: `fast` is Claude Haiku 5.5, `general` is GPT-6.1 Terra and
  `dev-hard` is Gemini 4 Hydrogen. `devops` uses `general` too.
- `docs-dev` uses `general`, so documentation work runs on the same model as a medium developer
  task, not on a planning model.
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

### Document types, scoping and the standards bundle

This part was first planned on pull request 625 and is carried here with three changes: the files
take the folder prefix, a parent's rules are included, and the worker receives the result in its
prompt instead of fetching it.

| Fact today | Evidence |
|---|---|
| A technology folder holds one governed file | `DocsLayout.TechnologyStandard` is `standards/<tech>/README.md`; `DocsScaffolder.Scaffold` writes one standard per declared technology. |
| There is no skill document type | `DocType` has no such member; `DocumentLoader.ParseDocType` maps thirteen spellings; `OntologyConfig` requires `technology` only of `coding-standard`. |
| Only a standard may name a technology | `DocSpecValidator.ValidateTechnology` reports `KW-DOC-SPEC-007` for any other type. |
| The standards tool returns the standard only, as text | `DocumentIndex.CodingStandards` and `DocsQueryService.AgentCodingStandards`; `McpAgentCodingStandardsTests` pins the text shape. |
| Standards rank at full weight in search | `DocumentIndex.Authority`: plans and specifications 0.55, decision records 0.9, everything else 1.0. |

**Types and scoping.** A `skill` document type is added, scoped by technology or role. A rules or
skills document may name a technology only when it sits in `standards/<technology>/`, and a role
only when it sits in `standards/<role>/`; anywhere else it stays an error. `docs/rules/` keeps the
rules that apply to every task, with no technology and full search weight. The Arbiter rules file
is YAML, not a governed document: the folder check and `arbiter validate` cover it.

**The bundle.** One lookup in `DocumentIndex` returns, for a technology: the shared rules, the
parent's rules and skills when the template names a parent, the technology's rules, its skills and
its standard. `docs_get_coding_standard_for_technology` renders that as named parts in that order,
rule ids first, the provenance line unchanged as the first line. A topic selects standard sections
as today; rules and skills render whole. The task prompt service uses the same lookup, so the tool
and the prompt can never disagree. The stitch happens when asked, not when indexing, so it is
always current and needs no second stored copy.

**Search weight.** A standard, a skills document and a rules document that names a technology rank
at 0.55, because a worker already holds them in its prompt. The shared rules file keeps 1.0: it is
the small file that always fits, so it is what remains when a budget cut drops a section.

**Scaffolding.** For a declared technology with no shipped template, `docs init` writes the four
files as stubs, and the shared rules file once.

**Precedence.** Technology rules beat parent rules, parent rules beat shared rules, and rules beat
skills. The shared rules file states this once.

The standard's headings, in order:

1. **Naming and layout**: names, versions, project layout, where code goes.
2. **Coding standards**: patterns, error handling.
3. **Testing**: frameworks, what to test, naming, placement, how to run.
4. **Commands**: build, format, lint and test commands.
5. **Security and data handling**.
6. **Done checklist**: what a reviewer checks.

**Existing hosts.** Every host today, this repository included, holds each standard as
`README.md`. That name is read for two minor releases with a warning, not an error. During that
warning period an un-upgraded project is not blocked: every finding the upgrade would raise is a
warning, each naming the two commands — `kyber-weave docs standards migrate` and then
`kyber-weave docs init` — and a run still works. They are `KW-CONFIG-REG-001` for a registry entry
that points at the prefixed name a folder has not gained, `KW-CONFIG-REG-002` for a generated block
whose only differences are those legacy names, `KW-DOC-SPEC-012` for the folder that still holds
`README.md`, and `KW-DOC-SPEC-013` for a standard whose headings are not the six.

`kyber-weave docs standards migrate` renames the files, adds the ones the folder lacks, and
reshapes a standard whose own headings are not the six: each authored section becomes a `###`
under the heading it best fits, its inner headings are demoted with it, every move is printed, and
no authored text is dropped. A standard already under the six headings is left byte for byte
alone, and a second run changes nothing.

Role folders are declared in a new `ontology.roles` list, so validation tells a role from a
technology. A platform folder is a technology folder; the `devops` agent's task names it the way a
developer task names `csharp`.

Content moves, all of it now that the old agents are gone:

- Every technology rule in the eleven technology agents, `test-dev` and `azure-reader` goes to the
  matching template, tracked by the inventory of Requirement 5.5.
- The `test` template's cross-technology rules are copied to `shared-rules.md`; the rest is copied
  to each technology's Testing section. The `test` technology and the `testing` skill go with the
  old agents, so those two places are their only home.
- A `sql-server` template takes the rules `sql-database-architect` holds. The `sql` template goes
  with the old agents.
- `azure` gains the skills and rules `azure-reader` holds. An `aws` template is added.
- A `github` platform template takes the `github-actions` standard and the `github-devops` skill.
  `azure-devops`, `gitlab` and `jira` templates are new.

Templates live in `products/kyber-squad/standards/<technology>/`. A contribution is a pull request
that adds one folder; the completeness check of Requirement 5.8 runs on it. Onboarding installs the
folders for the technologies and platforms the user names. `kyber-weave docs standards diff
[technology]` shows how a host folder differs from the current template and writes nothing.

**Where the squad is installed.** Kyber is deployed at the user level — one
`kyber-weave squad install --global` per harness per machine. A project-level install is a rare
exception, for a repository that has to pin a different version or a different target set. A
user-level install serves every project on that machine, which is why it cannot carry a per-project
choice: with one roster there is nothing per-project left to choose, and the task file records the
roster a run used.

### The task prompt

One service builds the prompt for a task. Three callers use it: the dispatch hook, the docs tool and
`docs packet`. It is built on the service behind `docs packet` today.

**Main path: the dispatch hook inserts it.** The conductor dispatches the worker with a few header
lines: the task id, the number of failed attempts, and the prior-results file when there is one. A
hook on the dispatch call builds the prompt and puts it in place of the task-id line before the
worker starts. Every other line of the dispatch is kept, so a retry's failure report still reaches
the worker. The worker begins with its whole prompt and reads
nothing. The conductor never sees the text.

Kyber-Weave already rewrites dispatch text this way: the Arbiter's hook removes routing headers
from the prompt before a worker starts.

| Harness | Can the hook rewrite the dispatch? | Evidence |
|---|---|---|
| Claude | yes | `updatedInput` carries the rewritten `prompt` (`docs/kyber-arbiter/architecture.md`). |
| Copilot CLI | yes | `CopilotHookAdapter.RenderCliAllowStrip` rewrites `prompt` in the call's arguments. |
| Copilot in VS Code | yes, with limits | `CopilotHookAdapter.RenderLocalPreAllowStrip` writes `updatedInput`. Hooks there are a preview that needs `chat.useHooks` and a trusted workspace, and the sub-agent dispatch is not confirmed by name. |
| Codex, Cursor, Pi, Kilo, OpenCode | yes | Each adapter's strip writes the rewritten input (architecture, delivery per harness). |
| Antigravity | not checked; its current docs describe a rewrite field, and Kyber-Weave does not use it yet | The hooks page (read 2026-11-16) documents a `PreToolUse` output field `overwrite`: "Replacement arguments merged (shallow top-level merge) into `toolCall.args` before execution." The repository's note that no rewrite field exists is out of date. It counts as a rewrite only after the live check in the first task of phase 2 shows the worker received the text and the dispatcher transcript did not. Two things that check must prove: the merge is top-level, so the whole `Subagents` list must be replaced, and `decision` is required, so the hook must pick a value that does not skip the user's approval. |
| Devin, Factory and any other harness | not checked | Confirmed in the first task of phase 2. |

**Fallback: a file.** Where the hook cannot rewrite the dispatch, the prompt is written to
`.agent_scratchpad/<task>/prompt.md`, the dispatch names that file, and the worker's first step is
to read it. The folder is not tracked, so the file is never committed.

**The hook runs with the Arbiter off.** Today the dispatch hook is installed as part of the
Arbiter. Prompt insertion is split out as its own hook action that Squad setup installs on every
harness that supports it, whether or not the Arbiter is enabled. With the Arbiter on, one hook call
does both: the rules run on the short dispatch first, then the prompt is inserted. If the prompt
cannot be built, the dispatch is stopped with the repair command.

**When the hook does not run.** A hook can be absent: Codex skips a hook the user has not trusted,
and Copilot in VS Code needs a setting. The worker then starts with only the header lines. Its
instructions cover this: with no `<task>` part it prints its own prompt with `docs packet`, or
stops. `kyber-weave doctor` reports a harness where the hook is not installed or not trusted.

**The docs tool** `docs_build_task_prompt_for_agent_dispatch` takes the project path and a task id
(and optionally the plan, a prior-results file and the number of failed attempts). It replies with
the agent to dispatch, the difficulty and its source, the technology, anything left out, and the
prompt file's path on a fallback harness. It never returns the prompt text. The conductor calls it
to learn which agent to dispatch.

The prompt holds, in order, each part inside its own tag:

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

Given a technology instead of a task id, the prompt holds the parts from `<shared-rules>` on, for a
dispatch that has no task: a `platform-researcher` request or a fix `bug-crusher` hands on.

The prompt is never written into the task file or a tracked file, so the task file's digest and
staleness check are unchanged. `KW-ARB-PLANREAD-001` stops a dispatch that tells a worker to open a
plan, specification or todo file; neither the expanded prompt nor the prompt file is one of those,
and the rule's criteria gain one line saying so.

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
`<name>.skills.md` and `<name>.rules.md` in its task prompt, the same way a developer agent does.

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

The roster switch is gone. `worker-roster: technology` is not a value any more: in host
configuration it is a configuration error whose message names the change, and a task file written
before the removal that records it is read as the one roster with `KW-DOC-TASKS-019`, so its
developer tasks owe a `Technology:` line like any other. `tiered` is the only roster and what an
absent key means, and a task file still records the roster it was written under.

The comparison the two-roster design planned is retired with the runbook that described it
([ADR 0088](../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)). It was
not used to decide the removal.

### Removal

Carried out on 2026-11-17 on the owner's decision
([ADR 0088](../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md)). The
comparison was not used to decide: two reduced comparison passes on a scratch project both failed
for product defects that were then fixed, and three acceptance runs on the Hotshot Logistics demo
on GitHub Copilot CLI on 2026-11-17 started, planned and stopped before dispatching any developer,
for defects that were also fixed. Anyone who wants the old agents installs an older version.

- The fourteen agents are deleted from the Squad source; a Squad update removes their rendered
  files only where the lock shows the Squad wrote them unedited.
- Agent-keyed task gates keep resolving through the shipped map for two minor releases;
  `docs validate` warns and names `kyber-weave review migrate-task-gates`, which rewrites them as
  `review.technology-gates` and shows the change first.
- A plan or task file naming a removed agent resolves to the developer agent for the task's
  difficulty, or to `architect`, `platform-researcher` or `devops`, and reports the substitution.
- `worker-roster` is no longer a switch: `tiered` is the only roster and the default, a host that
  sets `technology` fails with a configuration error that names the change, and a task file that
  records it is read as the one roster with a warning.

### File formats

| File | Today | After |
|---|---|---|
| Host configuration, model and capability profiles, gate catalogue | YAML | unchanged |
| Arbiter rules | YAML, one file | YAML, one file per folder (`<name>.arbiter-rules.yml`) |
| Task file | JSON (`tasks.json`, `<plan>.tasks.json`) | YAML, converted in the phase that already raises its schema version; JSON is read for two minor releases with a warning |
| Standards, rules, skills, agents | Markdown | unchanged |
| The task prompt | does not exist | Markdown parts inside named tags; inserted by the hook, or written to the untracked scratch folder on a fallback harness |
| Arbiter decision log, gate reports | JSON lines, JSON | machine output, not authored; out of scope |

New files use the `.yml` extension, as every YAML file in the repository does.

## Phases

Each phase ships on its own and leaves a working product.

| Phase | Delivers | Depends on |
|---|---|---|
| 1. Technology folders | The skill document type and scoping, the stitched standards bundle and its search weight, six-heading templates with four files each, the rule inventory, `shared-rules.md` testing rules, role and platform folder templates, onboarding by chosen technology, `docs standards diff`, migration of existing folders. Additive: nothing the old roster reads is deleted | none |
| 2. Task fields and the task prompt | `Difficulty:` and `Technology:`, the task file change, `TaskGateResolver`, the prompt service, the dispatch hook's prompt insertion with the file fallback, the docs tool | Phase 1 |
| 3. Agents | The three developer agents, `platform-researcher`, `devops`, the `dev-hard` profile, the conductor without a profile, the step-up | Phase 2 |
| 4. Arbiter rules from folders | The split of the 36 rules, folder loading, the `MODE-002` change, the command allow-list | Phases 1 and 3 |
| 5. Removal | The comparison runbook, retired without a completed comparison, the owner's decision, removal of the fourteen agents, the `test` technology, the `testing` skill and the `sql` template, and migration | Phases 1 to 4 |

## Data models

| Model | Change |
|---|---|
| Agent frontmatter | `model-profile` becomes optional (the conductor has none); optional `worker-roster`, whose only value is `tiered`. |
| Model profiles | New `dev-hard`, with the `architect` profile's rows; `orchestration` removed. |
| Capability profiles | New `platform-reader`. |
| Host configuration | `worker-roster`, whose only value is `tiered`; `review.technology-gates`; `ontology.roles`; a task prompt budget. |
| Plan and specification task text | Optional `Difficulty:` and `Technology:` lines. |
| Standard frontmatter | Optional `builds-on: <technology>`. |
| Task file | Schema version plus one; YAML; top-level `worker-roster` recording the roster it was written under; per entry `agent` (the developer tier the rating gives), `difficulty`, `difficulty-source`, `technology`. |
| Technology, role and platform folders | The standard is `<name>.standards.md`; new `<name>.arbiter-rules.yml`. |
| Arbiter | Trigger `worker.command`; fact `plan.task.files-all-tests`. |
| Docs tool server | New tool `docs_build_task_prompt_for_agent_dispatch`. |
| Dispatch hook | New prompt-insertion action, installed with or without the Arbiter. |
| Run scratch folder | New untracked file `.agent_scratchpad/<task>/prompt.md`, on a fallback harness only. |

## Error handling

| Case | Behaviour |
|---|---|
| `Difficulty:` holds another value | `docs validate` error naming the task and the three values. |
| No `Difficulty:` line | Treated as `medium`, source `default`. |
| Unknown technology on a task | `docs validate` error naming the task, the value and the declared list. |
| Tiered roster, developer task without a technology | `docs validate` error naming the task. |
| Host folder still using `README.md` | Read, with a warning naming the migrate command, for two minor releases. |
| Host configuration sets `worker-roster: technology` | Configuration error whose message names the removal. |
| Task file records `worker-roster: technology` | Read as the one roster, with `KW-DOC-TASKS-019` naming it. |
| Worker starts with only a task id | It prints its own prompt with `docs packet`, or stops and reports. |
| `Technology:` names two | `docs validate` error telling the author to split the task. |
| Technology named, folder missing | The prompt says so in that technology's section; the rest is returned. |
| Prompt over budget | Rules kept; skills then standard sections cut; cuts listed. |
| Task file missing or stale | No prompt; the reply names the repair command. |
| Developer agent, no gate resolves | Gate run error, non-zero exit. |
| `<name>.arbiter-rules.yml` unreadable or naming an unknown trigger or fact | `arbiter validate` error; shipped rules used for that folder. |
| Technology folder missing a file, or a standard missing a heading | `docs validate` error naming the folder and what is missing; during the warning period an unshaped standard is a warning that names the migrate command. |
| Command outside a technology's allow-list | Refused where the worker guard runs; an instruction elsewhere. |
| JSON task file | Read, with a warning, for two minor releases. |
| Removed agent named in a plan or task file | Resolved to a developer agent by difficulty, or to `architect`, `platform-researcher` or `devops`; substitution reported. |

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
| 1. Technology folders | 12 to 16 files | 6 new classes, 10 to 14 changed | 15 template folders reshaped, 4 role folders, 4 platform folders, 1 inventory, 6 docs |
| 2. Task fields and the task prompt | 18 to 24 files | 8 new classes, 14 to 20 changed | 4 prompt files, 4 docs; the JSON task list name appears in 7 source, 10 test, 8 product and 9 live doc files |
| 3. Agents | 6 to 9 files | 3 new classes, 8 to 12 changed | 5 new agents, 13 rendered sets, 4 docs |
| 4. Arbiter rules from folders | 8 to 12 files | 4 new classes, 6 to 10 changed | 3 rule files, 3 docs |
| 5. Removal | 10 to 15 files | 40 to 60 changed | 14 agents deleted, 1 retired runbook, 39 live docs, 84 product files |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What replaces the technology agents? | Three developer agents by difficulty: `dev-easy`, `dev-medium`, `dev-hard`. | Server and client agents were mechanically identical once technology moved to the task; model strength is the one real difference. | user: explicit decision 2026-11-16 |
| D2 | Who rates difficulty? | The architect; the decision model instead when the Arbiter runs with one. No gate checks the rating. | The decision model is asked when the task file is written, in the call that already picks the agent, so the rating is recorded once. | user: explicit approval 2026-11-16 |
| D3 | What happens when a task fails? | The next attempt goes one tier up; the three-dispatch limit stays. | The owner chose the existing one-step rule over a jump to `dev-hard` on the third try. | user: explicit decision 2026-11-16 |
| D4 | Which agents are removed? | The ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader`, `github-devops`. | `docs-dev` stays: it follows the documentation settings a host already customises. | user: explicit decision 2026-11-16 |
| D5 | Are the old agents deleted at once? | Yes, on the owner's decision of 2026-11-17: the fourteen agents are removed now, without a completed comparison. | The owner decided that keeping both cost too much; anyone who wants them installs an older version. D28 records why the comparison did not decide it. | user: explicit decision 2026-11-17, replacing the 2026-11-16 answer |
| D6 | How is the model assigned? | By the agent: each agent names a model profile, as today. Nothing assigns a model at dispatch. The conductor has no profile. | Run-time assignment was drafted and removed by the owner. Choosing an agent works on all thirteen harnesses. | user: explicit decision 2026-11-16 |
| D7 | Which model profiles change? | `dev-hard` is added, and `orchestration` is removed. Nothing is renamed. `dev-hard` is its own profile whose rows are the `architect` profile's stronger coding model on each harness, so the values coincide today and a planning rotation leaves it alone. | `dev-easy` uses `fast` and `dev-medium` uses `general`, so no preset breaks. On GitHub Copilot the three tiers are Claude Haiku 5.5, GPT-6.1 Terra and Gemini 4 Hydrogen; `docs-dev` uses `general`. | user: explicit approval 2026-11-16; model values set 2026-11-17 |
| D8 | What does a technology or role folder hold? | `<name>.standards.md`, `<name>.rules.md`, `<name>.skills.md`, `<name>.arbiter-rules.yml`. | The plan on pull request 625 named the first three `README.md`, `rules.md` and `skills.md`; the owner renamed them with the folder prefix. | user: explicit decision 2026-11-16 |
| D9 | What are the standard's headings? | Naming and layout; Coding standards; Testing; Commands; Security and data handling; Done checklist. | | user: explicit decision 2026-11-16 |
| D10 | Where do the 36 Arbiter rules go? | Into `conductor`, `architect` and `reviewer` role folders, split by trigger. | The owner named conductor and architect. `reviewer` is added because 13 rules are review rules. | user: explicit approval 2026-11-16 |
| D11 | Is any Arbiter rule locked? | No. | The owner's view, and the code agrees: every rule can already be set to log only. | user: explicit decision 2026-11-16 |
| D12 | How does the task prompt reach the worker? | A hook on the dispatch call replaces the conductor's one-line dispatch with the full prompt. Where a harness's hook cannot rewrite the dispatch, the prompt is written to a file the worker reads first. | Replaces the worker's own fetch that the plan on pull request 625 kept. The worker starts with everything, the conductor never reads or retypes about 12,000 tokens per task, and the worker cannot skip it. | user: explicit decision 2026-11-16 |
| D13 | How are task gates keyed? | By the task's one technology, then its parent, with a fallback to the old agent-keyed entries. | No agent is named after a technology any more. No gate is added and no lists are merged. | user: explicit approval 2026-11-16 |
| D14 | Which file format? | YAML for every data file added or changed, the task file included, extension `.yml`. | The owner asked for one format and approved converting the task file. `.yml` matches every YAML file in the repository. | user: explicit approval 2026-11-16 |
| D15 | What replaces `azure-reader`? | `platform-researcher`, which loads the skills and rules of the technology its task names. | Needs a new capability profile that can run commands. | user: explicit decision 2026-11-16 |
| D16 | Where does testing guidance live? | A Testing section in every standard; cross-technology rules in `shared-rules.md`. | `test-dev` and the `test` technology are removed. | user: explicit decision 2026-11-16 |
| D17 | How many specifications? | One, in five phases. | The owner's direction. | user: explicit decision 2026-11-16 |
| D18 | Does this need ADRs? | Yes: one for tiers, difficulty and the task prompt (amending ADR 0080 and ADR 0082), one for rules by folder, one for the task file format (amending ADR 0063). | Each is a contract hosts depend on. | user: explicit approval 2026-11-16 |
| D19 | What replaces `github-devops`? | A platform-neutral `devops` agent with a `devops` role folder and one platform folder each for GitHub, Azure DevOps, GitLab and Jira. | The agent names no platform; the task or the host configuration does. | user: explicit decision 2026-11-16 |
| D20 | How large is the prompt budget? | 60,000 characters by default; a task must fit with nothing cut. | Sized from today's files at about 47,000 for the largest case, 57,000 with a parent. | user: explicit approval 2026-11-16 |
| D21 | How many technologies may a task name? | One. A template may name one parent it builds on; unrelated technologies are separate tasks. | Keeps the prompt inside the budget and gives a task one set of gates. | user: explicit decision 2026-11-16 |
| D22 | How are the prompt's parts separated? | Named tags around each part; Markdown inside. | The one-format rule covers stored data files; the prompt is text for a model. | user: explicit decision 2026-11-16 |
| D23 | Is there a test role folder? | No. | Testing lives in the shared rules and each standard's Testing section. | user: explicit decision 2026-11-16 |
| D24 | Is the plan on pull request 625 a separate piece of work? | No. It is absorbed into phase 1 and the pull request is closed. | Building it apart would ship file names and a fetch step this design replaces. | user: explicit decision 2026-11-16 |
| D25 | How is a skills file typed and scoped? | A new `skill` document type; rules and skills may name a technology or role only inside that folder. | Carried from the plan on pull request 625, where it was a recommendation. Reusing an existing type would blur rules and procedures. | carried from pull request 625; owner directed the fold 2026-11-16 |
| D26 | When is the bundle stitched? | When asked, from one index lookup shared by the standards tool and the task prompt. | Carried from the plan on pull request 625. Always current; no second stored copy. | carried from pull request 625; owner directed the fold 2026-11-16 |
| D27 | How do standards rank in search? | Standards, skills and technology rules at 0.55; the shared rules file at 1.0. | Carried from the plan on pull request 625. Matches the weight plans and specifications already have. | carried from pull request 625; owner directed the fold 2026-11-16 |
| D28 | Was the comparison used to decide the removal? | No. The owner decided on 2026-11-17 to remove the fourteen agents, and the comparison D5 named is retired with its runbook. | Two reduced comparison passes on a scratch project both failed for product defects that were then fixed; three acceptance runs on the Hotshot Logistics demo on GitHub Copilot CLI on 2026-11-17 started, planned and stopped before dispatching any developer, for defects that were also fixed. Kept both rosters cost too much. | user: explicit decision 2026-11-17, recorded in [ADR 0088](../../adr/0088-the-old-technology-agents-are-removed-without-a-comparison.md) |

## Open questions

None. The one earlier question, the order against pull request 625, is settled by decision D24.

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
| H6 | The task prompt, hook insertion, the file fallback, order and budget (The task prompt) | DocGraph | runbook |
| H7 | Technology-resolved task gates and `review.technology-gates` (Task gates by technology) | ReviewCouncil | architecture |
| H8 | Rules by folder, loading order and overrides (Arbiter rules from folders) | KyberArbiter | architecture |
| H9 | The platform research agent, the `devops` agent and platform folders (The platform research agent; Roster) | KyberSquad | runbook |
| H10 | The roster that is left and why the comparison did not decide the removal (Roster switch and comparison; Removal) | KyberSquad | adr |
| H11 | Migration from agent-keyed gates and removed agent names (Removal) | ReviewCouncil | runbook |
| H12 | Tiers and the task prompt; rules by folder; the task file format (Decisions D1, D10, D12, D14, D18) | KyberSquad | adr |
| H13 | The skill document type and where rules and skills may name a technology or role (Document types, scoping and the standards bundle) | DocGraph | reference |
| H14 | The standards bundle's parts, order and search weight (Document types, scoping and the standards bundle) | DocGraph | runbook |
