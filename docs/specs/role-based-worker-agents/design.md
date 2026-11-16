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
  - cli-researcher
  - run-time model assignment
---

# Role-based worker agents design

**Phase status:** Draft

## Overview

Replace the eleven per-technology worker agents with three developer agents that differ only in
model strength: `dev-easy`, `dev-medium`, `dev-hard`. Each task carries a difficulty and a list of
technologies. Everything a technology agent knows moves into that technology's folder, which ships
as a template. One docs tool builds the whole prompt for a task. The Arbiter reads its rules from
role and technology folders. `test-dev`, `sql-database-architect` and `azure-reader` go away; a
read-only `cli-researcher` replaces the last.

The Squad goes from 24 agents to 15. The old agents stay selectable until a comparison has been run,
then they are removed with a migration path.

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

### Difficulty, retries and models today

| Fact | Evidence |
|---|---|
| A difficulty question already exists | `KW-ARB-DIFFICULTY-001` asks the decision model "easy, standard or hard?" with a 0.8 confidence floor and maps the answer through `model-targeting.difficulty-profiles`. |
| A step-up rule already exists | `KW-ARB-LADDER-001` moves a failed attempt one profile up the ladder, from the ledger alone, with no model call. |
| Both are off by default | `arbiter.model-targeting.mode` defaults to `shadow`: the choice is recorded and nothing is delivered. |
| A model can be set on a dispatch on one harness only | `docs/kyber-arbiter/runbook.md`: the rework dispatch climbs one rung "only where the harness takes a per-dispatch model (Claude); elsewhere the conductor's prose carries the escalation". `architecture.md`: other harnesses record `unsupported-harness`. |
| Nothing rates difficulty with the Arbiter off | No plan or task-file field holds a difficulty. |
| Rework is already capped | `delivery-execution` skill: at most three rework dispatches per failure cluster, then the architect mediates. |
| The agent pick is made when the task file is written | ADR 0082: `PlanTaskAgentPick` asks the decision model once, keeps picks at 0.8 confidence or more, and records them in the task file. |

### Rules, gates and the task prompt today

| Fact | Evidence |
|---|---|
| 36 Arbiter rules ship in one file | `src/KyberWeave.Domain/Arbiter/Rules/default-rules.yml`. Ids are permanent. |
| None is technology-specific | Every trigger is a delegation, planner, review or shortcut event. No rule reads a technology. |
| One rule names a worker | `KW-ARB-MODE-002` answers `not-required` when the target is `test-dev`. |
| A host can already change a rule | `arbiter.rules` in the host configuration sets `shadow` and `effects` per rule id. Eight rules ship in shadow. |
| An answer the Arbiter cannot reach allows | [ADR 0072](../../adr/0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md): no clause matched, no provider, an error or a floor not met all allow and are logged. |
| The Arbiter sees a sub-agent's own tool calls on two harnesses | Runbook: "Only Copilot CLI and Claude run the worker guard". |
| Host task gates are keyed by agent name | `review.task-gates` in the host configuration; four call sites look a key up by agent: `ReviewGatesService`, `PacketHostFacts.AppendGates`, `PacketTestCommandRenderer` and `TriggerFactBuilder`. An unknown agent gets a warning, an empty report and exit code 0. |
| Council gates are keyed by gate id | `review.gates`: this repository declares 44, of which 24 are three skill checks repeated for eight skills. They do not name agents. |
| A task packet command exists | `kyber-weave docs packet <task>` prints the task file's prompt plus a host facts section built by `PacketHostFacts` at print time, capped at 6,000 characters of host rules, never written into the task file. |
| Workers fetch standards themselves | [ADR 0064](../../adr/0064-project-knowledge-lives-in-gates-and-standards-reached-through-tools.md) and decision D3 of the plan on pull request 625: the worker calls `docs_get_coding_standard_for_technology` in its first step. |
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

1. **Run-time model assignment works on one harness of thirteen.** On the other twelve the model
   must still be written into the rendered agent file. That is why three developer agents exist
   instead of one: choosing an agent works everywhere, choosing a model at dispatch does not.
2. **No Arbiter rule is technology-specific today.** Every technology's `arbiter-rules.yml` starts
   empty. All 36 rules land in three role folders. The technology files earn their place only when
   a rule such as the command allow-list of Requirement 9 is written.
3. **`rules.md` and `arbiter-rules.yml` are different things with one name.** The first is prose a
   model reads. The second is evaluated by the Arbiter. A host can read a rule in `rules.md` and
   assume it is enforced. The template header of each file must say which it is.
4. **The whole brief passes through the conductor.** A tool that returns the prompt puts shared
   rules, technology rules, skills and standard sections into the conductor's context once per
   dispatch. The plan on pull request 625 avoided that by having the worker fetch. The comparison
   must measure it (open question 3).
5. **A command-line research agent is wider than the agent it replaces.** `azure-reader` cannot run
   a command. `cli-researcher` can, and the allow-list that keeps it read-only is enforced on two
   harnesses and is an instruction on eleven.
6. **Removing `test-dev` removes a second pair of eyes.** Today a different agent can write the
   tests. After this, the developer agent writes code and tests, and the review council is the
   independent check.
7. **Host standards are host-owned.** A rule moved from an agent body into a template reaches new
   hosts only. Requirement 7.4 exists so an existing host can see what it is missing.
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
| `bug-crusher` | stays | `root-fixer` | `dev-easy` |
| `bug-crusher-investigator` | stays | `investigator` | `investigator` |
| `research-agent` | stays | `read-only` | `reader` |
| `docs-dev` | stays | `documentation` | `dev-easy` |
| `github-devops` | stays | `publishing-worker` | `dev-medium` |
| `dev-easy`, `dev-medium`, `dev-hard` | new | `worker` | same name as the agent |
| `cli-researcher` | new; replaces `azure-reader` | `cli-reader` (new) | `reader` |
| Ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader` | removed in phase 5 | | |

The three developer agents are one body rendered three times. The body states the workflow and the
rules that hold for every technology, and names no technology. `docs-dev` keeps its own rules
because they follow the documentation ontology and the Config Reg, which a host already customises
through its configuration.

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
`github-devops`), and difficulty chooses the tier:

```text
tier = min(hard, rated difficulty + failed attempts at this task)
```

The task prompt tool computes the tier from the task file and the attempt tally the conductor
already keeps on the run's execution artifact, and returns the agent name. The conductor dispatches
that agent. The same sentence is in the conductor's instructions, so a run with the Arbiter off
behaves the same. The limit of three rework dispatches per failure cluster is unchanged, and at the
limit the architect mediates as today.

`KW-ARB-LADDER-001` moves to the `conductor` role folder unchanged. Its ladder becomes `dev-easy`,
`dev-medium`, `dev-hard`. When the conductor has already dispatched the next tier, the rule's
target equals the rendered profile and it writes nothing.

### Models

| New profile | From | Used by |
|---|---|---|
| `architect` | `architect` | `architect`, `product-owner` |
| `dev-easy` | `fast` | `dev-easy`, `docs-dev`, `bug-crusher` |
| `dev-medium` | `general` | `dev-medium`, `github-devops` |
| `dev-hard` | new | `dev-hard` |
| `reader` | new; today's readers use `fast` | `research-agent`, `cli-researcher` |
| `reviewer` | `reviewer` | the three review agents |
| `investigator` | `deep-planning` | `bug-crusher-investigator` |

`orchestration` is removed: the conductor is rendered with no model on every harness. `dev-hard` is
its own row, so changing the planning models never changes it. Old profile names stay readable as
aliases for two minor releases, with a warning, so user presets keep working.

Delivery per harness:

| Harness | How the model reaches the agent |
|---|---|
| Claude | On the dispatch. The rendered agent file names no model. When the Arbiter's model targeting is enforced the Arbiter writes it; otherwise the conductor passes it from the profile table. |
| The other twelve | The renderer writes the profile's model into the agent file, as today. Choosing the agent chooses the model. |

Each renderer declares whether its harness takes a model on a dispatch; that one flag drives both
columns. Whether any of the twelve can gain it is not established here (open question 4).

### Technology folders and templates

```text
docs/standards/
  shared-rules.md
  <technology>/
    README.md           the standard, six headings
    rules.md            prose rules a model reads
    skills.md           how to do the work
    arbiter-rules.yml   rules the Arbiter evaluates; may declare none
  conductor/  architect/  reviewer/
    rules.md  skills.md  arbiter-rules.yml
```

The first three files per technology and `shared-rules.md` are the plan on pull request 625. This
design adds `arbiter-rules.yml`, the six headings, and the role folders.

The standard's headings, in order:

1. **Scope and layout**: versions, project layout, where code goes.
2. **Coding standards**: naming, patterns, error handling.
3. **Testing**: frameworks, what to test, naming, placement, how to run.
4. **Commands**: build, format, lint and test commands.
5. **Security and data handling**.
6. **Done checklist**: what a reviewer checks.

Role folders are declared in a new `ontology.roles` list, so validation tells a role from a
technology. A role folder has no standard; the agent file is its standard.

Content moves:

- Every technology rule in the eleven technology agents, `test-dev` and `azure-reader` goes to the
  matching template, tracked by the inventory of Requirement 6.5.
- The `test` template's cross-technology rules go to `shared-rules.md`; the rest goes to each
  technology's Testing section. The `test` technology and the `testing` skill are removed.
- The `sql` template becomes `sql-server` and takes the rules `sql-database-architect` holds.
- `azure` gains the skills and rules `azure-reader` holds. An `aws` template is added.

Templates live in `products/kyber-squad/standards/<technology>/`. A contribution is a pull request
that adds one folder; the completeness check of Requirement 6.8 runs on it. Onboarding installs the
folders for the technologies the user names. `kyber-weave docs standards diff [technology]` shows
how a host folder differs from the current template and writes nothing.

### The task prompt tool

A new tool on the docs server, `docs_build_task_prompt_for_agent_dispatch`, takes the project path
and a task id (and optionally the plan and a prior-results file). It is built on the service behind
`docs packet`, and that command prints the same text.

Returned, in order:

1. A header: task id, agent to dispatch, difficulty and its source, model profile, technologies.
2. The task's own prompt from the task file.
3. Host facts, as `PacketHostFacts` builds them today, with gates resolved by technology.
4. Shared rules.
5. Per technology: its `rules.md`, its `skills.md`, then the sections of its standard that bear on
   the task.
6. A list of anything left out.

Stitching happens when the prompt is built. Nothing is written into the task file, so its digest and
staleness check are unchanged. The budget rule: rules are never cut; skills are cut before standard
sections; within each, the least relevant to the task text goes first; every cut is listed. The
budget is one host configuration value with a shipped default set in the tasks phase from
measurement.

The developer agent's first step is no longer "fetch your standards". It may still call
`docs_get_coding_standard_for_technology` for a topic the prompt left out. The architect and
`product-owner` keep fetching just in time, per technology a task names.

### Task gates by technology

One resolver, `TaskGateResolver`, replaces the four lookups by agent name.

1. An agent with its own `review.task-gates` entry uses it (today's path, kept for `docs-dev`,
   `github-devops` and the old roster).
2. Otherwise, for each technology of the task: `review.technology-gates.<technology>` when the host
   declares it, else the entry of the technology agent the shipped map names.
3. Lists merge in technology order. Same id and same command runs once; same id with a different
   command runs both, reported as `<technology>:<id>`.
4. A developer agent whose task names technologies and resolves no gate is an error with a non-zero
   exit.

No gate is added. In this repository the twelve agent-keyed blocks hold 14 distinct gates and
become about six technology blocks. `review.gates` is untouched.

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
id wins); for an event that concerns a task, the `arbiter-rules.yml` of each technology the task
names; then the host's `arbiter.rules` settings, which keep working as they do today. A file that
fails to parse, or names a trigger or fact the Arbiter lacks, is an `arbiter validate` error and
the shipped rules for that folder are used.

**Is any rule critical?** No rule is locked. Every rule can be set to log only, eight already ship
that way, and an answer the Arbiter cannot reach allows. Six rules decide from machine facts and
guard the tree itself (`READY-001`, `DIFF-001`, `PLANNER-DIFF-001`, `READONLY-001`,
`CHECKOUT-001`, `ROSTER-001`); the templates keep them enforced, and a host may still turn them
down.

`KW-ARB-MODE-002` loses its `test-dev` clause. It gains a clause on a new machine fact,
`plan.task.files-all-tests`, which is true when every file of the task matches the test paths the
host already declares for its gates.

Technology rule files start empty. The first use is the command allow-list below, which needs one
new trigger, `worker.command`, raised by the existing worker guard.

### The command-line research agent

`cli-researcher` takes one or more technologies from its task and receives their `skills.md` and
`rules.md` through the task prompt tool, the same way a developer agent does.

- New capability profile `cli-reader`: `read-only` plus `process.execute: allow`.
- A technology's `skills.md` covers sign-in, the command-line tools and tool servers to use, query
  patterns and output shaping. Tool servers stay first choice where they exist, as `azure-reader`
  works today.
- A technology's `arbiter-rules.yml` may declare allowed command forms, for example `az * list`,
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
gate failures, review findings by severity, tokens and wall time, and the size of the prompts that
passed through the conductor.

### Removal

After the comparison and the owner's recorded decision:

- The thirteen agents are deleted from the Squad source; a Squad update removes their rendered
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
| Arbiter rules | YAML, one file | YAML, one file per folder (`arbiter-rules.yml`) |
| Task file | JSON (`tasks.json`, `<plan>.tasks.json`) | YAML, converted in the phase that already raises its schema version; JSON is read for two minor releases with a warning (open question 1) |
| Standards, rules, skills, agents | Markdown | unchanged |
| Tool output (the standards bundle, the task prompt) | text with tagged sections | not a stored file; unchanged (open question 2) |
| Arbiter decision log, gate reports | JSON lines, JSON | machine output, not authored; out of scope |

New files use the `.yml` extension, as every YAML file in the repository does.

## Phases

Each phase ships on its own and leaves a working product.

| Phase | Delivers | Depends on |
|---|---|---|
| 1. Technology folders | Six-heading templates with four files each, the rule inventory, `shared-rules.md` testing rules, role folder templates, onboarding by chosen technology, `docs standards diff` | Pull request 625 |
| 2. Task fields and the prompt tool | `Difficulty:` and `Technologies:`, the task file change, `TaskGateResolver`, the task prompt tool | Phase 1 |
| 3. Agents and models | The three developer agents, `cli-researcher`, the seven model profiles, run-time model on Claude, the roster switch, the step-up | Phase 2 |
| 4. Arbiter rules from folders | The split of the 36 rules, folder loading, the `MODE-002` change, the command allow-list | Phases 1 and 3 |
| 5. Comparison and removal | The comparison runbook and runs, the decision, removal and migration | Phases 1 to 4 |

## Data models

| Model | Change |
|---|---|
| Agent frontmatter | `model-profile` becomes optional (the conductor has none); optional `worker-roster: technology \| tiered`. |
| Model profiles | Seven profiles as above; aliases for `fast`, `general`, `deep-planning`. |
| Capability profiles | New `cli-reader`. |
| Host configuration | `worker-roster`; `review.technology-gates`; `ontology.roles`; a task prompt budget. |
| Plan and specification task text | Optional `Difficulty:` and `Technologies:` lines. |
| Task file | Schema version plus one; YAML; top-level `worker-roster`; per entry `difficulty`, `difficulty-source`, `technologies`. |
| Technology and role folders | New `arbiter-rules.yml`. |
| Arbiter | Trigger `worker.command`; fact `plan.task.files-all-tests`. |
| Docs tool server | New tool `docs_build_task_prompt_for_agent_dispatch`. |

## Error handling

| Case | Behaviour |
|---|---|
| `Difficulty:` holds another value | `docs validate` error naming the task and the three values. |
| No `Difficulty:` line | Treated as `medium`, source `default`. |
| Unknown technology on a task | `docs validate` error naming the task, the value and the declared list. |
| Tiered roster, file-changing task without technologies | `docs validate` error naming the task. |
| Technology named, folder missing | The prompt says so in that technology's section; the rest is returned. |
| Prompt over budget | Rules kept; skills then standard sections cut; cuts listed. |
| Task file missing or stale | No prompt; the reply names the repair command. |
| Developer agent, no gate resolves | Gate run error, non-zero exit. |
| `arbiter-rules.yml` unreadable or naming an unknown trigger or fact | `arbiter validate` error; shipped rules used for that folder. |
| Technology folder missing a file, or a standard missing a heading | `docs validate` error naming the folder and what is missing. |
| Command outside a technology's allow-list | Refused where the worker guard runs; an instruction elsewhere. |
| Old profile name in a preset; JSON task file | Read, with a warning, for two minor releases. |
| Removed agent named in a plan or task file | Resolved to a developer agent by difficulty; substitution reported. |

## Testing strategy

Test-first, with targeted classes only; no task runs the full suite.

- **New test classes** (estimate: 16 to 20): difficulty parsing, defaulting and validation; the
  decision-model rating and its source; tier computation and step-up; technology parsing and
  validation; `TaskGateResolver`; the prompt tool (order, budget cuts, missing folder, stale task
  file, parity with `docs packet`); folder completeness and the six headings; template install by
  chosen technology; `docs standards diff`; rule loading order and host override; the `MODE-002`
  clause; the command allow-list; model profile aliases; rendering with and without a model;
  developer-agent content (no technology named).
- **Existing tests that change** (estimate: 25 to 40 files before removal): the classes that pin
  the shipped roster, bundle, model profiles, default rules file, task file shape and packet text.
- **Removal** (estimate: 40 to 60 more files): tests that use a removed agent as an example slug
  where the slug must name a shipped agent.

## Sizes

Estimates, for planning only.

| Phase | Source | Tests | Docs and prompts |
|---|---|---|---|
| 1. Technology folders | 6 to 9 files | 4 new classes, 4 to 6 changed | 15 template folders reshaped, 3 role folders, 1 inventory, 4 docs |
| 2. Task fields and the prompt tool | 12 to 16 files | 6 new classes, 10 to 15 changed | 4 prompt files, 4 docs |
| 3. Agents and models | 8 to 12 files | 4 new classes, 8 to 12 changed | 4 new agents, 11 agent edits, 13 rendered sets, 4 docs |
| 4. Arbiter rules from folders | 8 to 12 files | 4 new classes, 6 to 10 changed | 3 rule files, 3 docs |
| 5. Comparison and removal | 10 to 15 files | 40 to 60 changed | 13 agents deleted, 1 runbook, 39 live docs, 84 product files |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | What replaces the technology agents? | Three developer agents by difficulty: `dev-easy`, `dev-medium`, `dev-hard`. | Server and client agents were mechanically identical once technology moved to the task; model strength is the one real difference. | user: explicit decision 2026-11-16 |
| D2 | Who rates difficulty? | The architect; the decision model instead when the Arbiter runs with one. No gate checks the rating. | The decision model is asked when the task file is written, in the call that already picks the agent, so the rating is recorded once. | user: explicit decision 2026-11-16; timing is autonomous, pending owner approval |
| D3 | What happens when a task fails? | The next attempt goes one tier up; the three-dispatch limit stays. | The owner chose the existing one-step rule over a jump to `dev-hard` on the third try. | user: explicit decision 2026-11-16 |
| D4 | Which agents are removed? | The ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader`. | `docs-dev` and `github-devops` stay: different permissions, and `docs-dev` follows the documentation settings. | user: explicit decision 2026-11-16 |
| D5 | Are the old agents deleted at once? | No. They stay selectable until a comparison has run, then phase 5 removes them. | Reconciles the earlier "side by side" decision with "goes away". | autonomous: recommendation, pending owner approval |
| D6 | How is the model assigned? | At dispatch where the harness allows it; otherwise written into the agent file. The conductor has no profile. | Only Claude takes a model on a dispatch today. | user: explicit decision 2026-11-16; fallback is autonomous, pending owner approval |
| D7 | Which model profiles exist? | `architect`, `dev-easy`, `dev-medium`, `dev-hard`, `reader`, `reviewer`, `investigator`. | `investigator` is added to the owner's list because the bug investigator needs deep reasoning and is not a developer. `dev-hard` is separate from planning. | user: explicit decision 2026-11-16; `investigator` is autonomous, pending owner approval |
| D8 | What does a technology folder hold? | `README.md`, `rules.md`, `skills.md`, `arbiter-rules.yml`. | The first three are the plan on pull request 625. | user: explicit decision 2026-11-16 |
| D9 | What are the standard's headings? | Scope and layout; Coding standards; Testing; Commands; Security and data handling; Done checklist. | Proposed to the owner on 2026-11-16; not yet confirmed heading by heading. | autonomous: recommendation, pending owner approval |
| D10 | Where do the 36 Arbiter rules go? | Into `conductor`, `architect` and `reviewer` role folders, split by trigger. | The owner named conductor and architect. `reviewer` is added because 13 rules are review rules. | user: explicit decision 2026-11-16; `reviewer` folder is autonomous, pending owner approval |
| D11 | Is any Arbiter rule locked? | No. | The owner's view, and the code agrees: every rule can already be set to log only. | user: explicit decision 2026-11-16 |
| D12 | Who builds the task prompt? | A docs tool the conductor calls; it returns one prompt. | Reverses decision D3 of the plan on pull request 625. Deterministic, and the worker cannot skip it. | user: explicit decision 2026-11-16 |
| D13 | How are task gates keyed? | By the task's technologies, with a fallback to the old agent-keyed entries. | No agent is named after a technology any more. No gate is added. | autonomous: recommendation, pending owner approval |
| D14 | Which file format? | YAML for every data file added or changed, extension `.yml`. | The owner asked for one format. `.yml` matches every YAML file in the repository. | user: explicit decision 2026-11-16; extension is autonomous |
| D15 | What replaces `azure-reader`? | `cli-researcher`, which loads the skills and rules of the technology its task names. | Needs a new capability profile that can run commands. | user: explicit decision 2026-11-16 |
| D16 | Where does testing guidance live? | A Testing section in every standard; cross-technology rules in `shared-rules.md`. | `test-dev` and the `test` technology are removed. | user: explicit decision 2026-11-16 |
| D17 | How many specifications? | One, in five phases. | The owner's direction. | user: explicit decision 2026-11-16 |
| D18 | Does this need ADRs? | Yes: one for tiers, difficulty and the task prompt (amending ADR 0080 and ADR 0082), one for rules by folder, one for the task file format (amending ADR 0063). | Each is a contract hosts depend on. | autonomous: recommendation, pending owner approval |

## Open questions

1. **Task file format (D14).** Convert the task file from JSON to YAML in phase 2? Recommendation:
   yes. Its schema changes in that phase anyway, and JSON stays readable for two minor releases.
2. **Tool output.** Do the standards bundle and the task prompt count under the one-format rule?
   Recommendation: no. They are text handed to a model, not stored files; keep tagged sections.
3. **Prompt through the conductor (D12).** Return the full prompt, or write it to the run's scratch
   folder and return its path? Recommendation: return the prompt as decided, measure the conductor's
   context in the comparison, and switch only if the numbers say so.
4. **Run-time model on more harnesses (D6).** Check each of the twelve for a per-dispatch model
   before phase 3? Recommendation: yes, as the first task of phase 3.
5. **Old agents (D5).** Keep them selectable until the comparison, or delete them when the new
   agents ship? Recommendation: keep them until the comparison.
6. **Headings (D9).** Are the six headings right? Recommendation: yes.
7. **Order against pull request 625.** Phase 1 cannot start before that plan lands.
   Recommendation: land it first, with its decision D3 changed to match D12 here.

## Scale

`documentation-impact: extends-component`, `impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]`.

## Design harvest

| ID | Design item (section) | Target component | Target doc type |
|---|---|---|---|
| H1 | The roster, the developer tiers and the removed agents (Roster) | KyberSquad | architecture |
| H2 | Difficulty, its sources and the step-up (Difficulty; Choosing the agent and stepping up) | KyberSquad | architecture |
| H3 | Model profiles and per-harness delivery (Models) | KyberSquad | reference |
| H4 | Technology and role folders, the six headings, templates and contribution (Technology folders and templates) | DocGraph | reference |
| H5 | Installing templates and comparing a host folder with its template (Technology folders and templates) | DocGraph | onboarding |
| H6 | The task prompt tool, its order and budget (The task prompt tool) | DocGraph | runbook |
| H7 | Technology-resolved task gates and `review.technology-gates` (Task gates by technology) | ReviewCouncil | architecture |
| H8 | Rules by folder, loading order and overrides (Arbiter rules from folders) | KyberArbiter | architecture |
| H9 | The command-line research agent and its allow-list (The command-line research agent) | KyberSquad | runbook |
| H10 | The roster switch and the comparison procedure (Roster switch and comparison) | KyberSquad | runbook |
| H11 | Migration from agent-keyed gates and removed agent names (Removal) | ReviewCouncil | runbook |
| H12 | Tiers and the task prompt; rules by folder; the task file format (Decisions D1, D10, D12, D14, D18) | KyberSquad | adr |
