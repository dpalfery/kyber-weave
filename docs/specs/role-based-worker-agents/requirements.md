---
id: specs/role-based-worker-agents/requirements
title: Role-based worker agents requirements
doc-type: requirements
status: draft
owner: dpalfery
last-reviewed: 2026-11-16
component: KyberSquad
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]
keywords:
  - role agents
  - worker roster
  - server-dev
  - client-dev
  - devops-dev
  - task technologies
  - task gates
---

# Requirements Document

**Phase status:** Draft

## Introduction

Kyber-Squad ships one worker agent per technology: `csharp-dev`, `dal-dev`, `react-dev`,
`typescript-dev`, `python-dev`, `go-dev`, `rust-dev`, `maui-dev`, `tauri-dev`, `pulumi-dev`,
`sql-database-architect`, plus `test-dev`, `docs-dev` and `github-devops`. The owner proposes a
small set of role agents instead: a server-side developer, a client-side developer and a devops
developer, beside the existing `code-reviewer`. Technology knowledge would live only in each
technology's standards folder, and each task would say which technologies it touches.

Three owner decisions made on 2026-11-16 shape these requirements:

1. **Side by side.** The role agents are added. No current agent is deleted, so the same work can be
   run on either set of workers and the results compared.
2. **Technology knowledge comes from the standards bundle.** The plan on
   [pull request 625](https://github.com/dpalfery/kyber-weave/pull/625) puts a rules file, a skills
   file and the standard in each technology folder and has the docs tool return them as one bundle.
   This specification depends on that plan and does not design a second delivery route.
3. **The architect reads standards just in time.** It asks the docs tool for the one technology a
   task needs instead of reading every standard.

Terms used below:

- **Worker roster**: the set of worker agents a run may dispatch. `technology` is today's set;
  `role` is the new set.
- **Role agent**: `server-dev`, `client-dev` or `devops-dev`.
- **Technology agent**: one of the eleven per-technology workers named above. Ten of them belong to
  the `technology` roster only; `sql-database-architect` serves both rosters.
- **Shared worker**: `test-dev`, `docs-dev` and `github-devops`, which serve both rosters.
- **Task gate**: a command a worker runs on its own change before it reports done, declared by the
  host repository under `review.task-gates`.
- **Standards bundle**: what `docs_get_coding_standard_for_technology` returns for one technology.
- **Decision model**: the small model the Arbiter and the task-file writer ask for judgement calls.

The evidence behind each requirement is in the [design](design.md#evidence).

## Requirements

### Requirement 1: Role agents exist beside the technology agents

**User Story:** As the owner, I want role agents added without removing any current agent, so that I
can compare the two rosters on the same work.

#### Acceptance Criteria

1.1. THE Squad source SHALL define three new worker agents named `server-dev`, `client-dev` and
`devops-dev`.

1.2. WHEN the role agents are added THEN every agent that exists today SHALL remain in the source,
in the `full` bundle and in every harness's rendered output, with an unchanged name.

1.3. THE `server-dev` agent SHALL cover the work `csharp-dev`, `dal-dev`, `typescript-dev`,
`python-dev`, `go-dev` and `rust-dev` cover today.

1.4. THE `client-dev` agent SHALL cover the work `react-dev`, `maui-dev` and `tauri-dev` cover today.

1.5. THE `devops-dev` agent SHALL cover the work `pulumi-dev` covers today and the authoring of
pipeline files (workflows, Dockerfiles, build scripts) that `github-devops` covers today.

1.6. THE role agents SHALL use the existing `worker` capability profile and the same Copilot tool
list the technology agents use.

1.7. THE `code-reviewer`, `test-dev`, `docs-dev`, `github-devops` and `sql-database-architect`
agents SHALL stay as they are and SHALL be dispatchable under both rosters.

1.8. A role agent's body SHALL name no technology-specific rule, command, library or skill; it
SHALL state only the role's scope, the workflow and the rules that hold for every technology. The
interim table of criterion 4.5 is the one exception.

### Requirement 2: One roster is active per run

**User Story:** As a conductor, I want exactly one worker roster active in a run, so that I never
choose between a technology agent and a role agent for the same task.

#### Acceptance Criteria

2.1. THE host configuration SHALL accept a `worker-roster` value of `technology` or `role`.

2.2. IF the host configuration declares no `worker-roster` THEN the system SHALL use `technology`.

2.3. WHEN a task file is written with an explicit roster option THEN that value SHALL override the
host configuration for that plan or specification only, and the command's report SHALL state the
roster in use.

2.4. WHEN the task-file writer asks the decision model to pick an agent THEN the candidates SHALL be
the active roster's workers plus the shared workers, and no agent of the other roster.

2.5. WHEN the conductor chooses an agent for a task with no recorded pick THEN its instructions SHALL
limit the choice to the active roster's workers plus the shared workers.

2.6. IF a plan task names an agent of the inactive roster with an explicit `Role:` line THEN the
system SHALL keep the owner's override and SHALL report it as a warning when the task file is written.

2.7. THE task file SHALL record the roster it was written under, and every later reader (the ready
queue, validation and the Arbiter) SHALL take the run's roster from the task file.

### Requirement 3: A task declares its technologies

**User Story:** As an architect, I want each task to name the technologies it touches, so that the
worker receives the matching standards without knowing them in advance.

#### Acceptance Criteria

3.1. THE plan and specification task formats SHALL accept a `Technologies:` line listing one or more
technology names.

3.2. WHEN a task file is written THEN each task entry SHALL carry its technologies as a list.

3.3. IF a task names a technology the host's `ontology.technologies` does not declare THEN
`docs validate` SHALL report it as an error naming the task, the value and the declared list.

3.4. IF the active roster is `role` and a task that changes files has no `Technologies:` line THEN
`docs validate` SHALL report it as an error naming the task.

3.5. IF the active roster is `technology` and a task has no `Technologies:` line THEN the system
SHALL accept the task, so existing plans stay valid.

3.6. WHEN a task names technologies and its file scope contains a file that no named technology's
gates select but another declared technology's gates do THEN `docs validate` SHALL report a warning
naming the file and that technology.

3.7. THE `Capabilities:` line SHALL stay as it is: plain words, no fixed vocabulary.

### Requirement 4: The packet delivers the matching standards

**User Story:** As a role agent, I want my task packet to tell me which standards bundles to fetch, so
that I apply the right technology rules without reading every standard.

#### Acceptance Criteria

4.1. WHEN a packet is printed for a task that declares technologies THEN its host facts SHALL list
each technology with the exact docs-tool call that returns its standards bundle.

4.2. THE packet SHALL NOT inline standards text; the worker fetches each bundle through the docs
tool, by topic, when the work needs it.

4.3. WHEN a role agent starts a task THEN its workflow SHALL fetch the bundle for every technology
the packet lists before its first edit.

4.4. THE role agents SHALL receive technology rules and technology skills only from the standards
bundle once the plan on pull request 625 has shipped.

4.5. UNTIL the plan on pull request 625 has shipped, a role agent SHALL load today's technology
skill for each listed technology through one shipped table that maps a technology to a skill name.

4.6. IF a packet lists a technology and the docs tool reports no standard for it THEN the worker
SHALL report that as an open item and SHALL NOT invent the standard.

### Requirement 5: The architect reads standards just in time

**User Story:** As the owner, I want the architect to ask the docs tool for one technology's standard
when a task needs it, so that planning does not load every standard.

#### Acceptance Criteria

5.1. WHEN the architect needs a technology's rules to plan a task THEN it SHALL call
`docs_get_coding_standard_for_technology` for that technology, with a topic where one applies.

5.2. THE architect's instructions SHALL forbid reading a standards file directly, except when the
docs tool is missing or reports a failure.

5.3. THE architect SHALL fetch a standard only for a technology at least one task of the plan names.

5.4. THE same rule SHALL apply to `product-owner` when it writes a specification's tasks.

### Requirement 6: Task gates resolve by technology for role agents

**User Story:** As a host repository owner, I want role agents to run the task gates I already
declared, so that I can try the role roster without rewriting my configuration.

#### Acceptance Criteria

6.1. WHEN a role agent runs its task gates for a task THEN the system SHALL run the gates declared
for each technology the task names.

6.2. THE host configuration SHALL accept task gates keyed by technology.

6.3. IF a host declares no technology-keyed gates for a technology THEN the system SHALL use the
gates declared under the technology agent that technology maps to, from one shipped map.

6.4. WHEN two technologies of one task resolve to gates with the same id and the same command THEN
the system SHALL run that gate once.

6.5. IF two technologies of one task resolve to gates with the same id and different commands THEN
the system SHALL run both and report each under its technology and id.

6.6. IF a role agent's task names technologies and no gate resolves for any of them THEN the system
SHALL report an error, not a warning, and SHALL exit non-zero.

6.7. WHEN a technology agent or a shared worker runs its task gates THEN the system SHALL resolve
them by agent name exactly as it does today.

6.8. THE packet's host facts, the packet's test command and the Arbiter's `config.task-gates-declared`
fact SHALL use the same resolution as the gate run.

6.9. THE council's `review.gates` SHALL NOT change: they are keyed by gate id and selected by
`include` globs, not by agent.

### Requirement 7: The Arbiter treats role agents as workers

**User Story:** As the owner, I want every Arbiter rule that guards a technology agent to guard a role
agent the same way, so that the comparison measures the roster and not a difference in enforcement.

#### Acceptance Criteria

7.1. THE role agents SHALL be classified as implementation targets with the `worker` profile, so
every rule keyed on that class or profile applies without a rule change.

7.2. THE conductor's `delegates-to` list SHALL include the role agents.

7.3. WHEN a dispatch names an agent of the inactive roster THEN the Arbiter SHALL record a machine
fact saying so, and the shipped rule for it SHALL allow and log.

7.4. THE rules that name `test-dev`, `docs-dev` or `github-devops` SHALL NOT change.

7.5. No Arbiter rule id SHALL be reused or renumbered.

### Requirement 8: Every harness renders the role agents

**User Story:** As a user of any supported harness, I want the role agents rendered for my harness, so
that the role roster is usable wherever the technology roster is.

#### Acceptance Criteria

8.1. THE thirteen harness renderers SHALL render the three role agents with no renderer code change
that names an agent.

8.2. WHEN the Squad is updated on a host THEN the role agents SHALL be written beside the existing
agents and no existing agent file SHALL be removed.

8.3. THE role agents' descriptions SHALL state that they are used only when the host's worker roster
is `role`, so a harness that routes by description does not prefer them by default.

### Requirement 9: Nothing a technology agent knows is lost

**User Story:** As the owner, I want every rule that lives only in a technology agent's body to have a
home a role agent can reach, so that the role roster is not weaker by omission.

#### Acceptance Criteria

9.1. THE delivery SHALL produce an inventory of every technology-specific rule in the eleven
technology agents' bodies, each mapped to the standards file that states it or marked missing.

9.2. WHEN a rule is marked missing THEN it SHALL be added to that technology's shipped standard, or
to its rules file once the plan on pull request 625 has shipped.

9.3. THE rules that hold for every technology (build-output isolation, the diagnostics baseline and
sweep, no masked validation commands, no unbounded test-fix loop) SHALL live in the shared worker
baseline or the role agent body, stated once.

9.4. THE delivery SHALL state how a host that already owns its standards learns of rules added to
the shipped standards, because `docs init` does not rewrite a host's standards.

### Requirement 10: The two rosters can be compared

**User Story:** As the owner, I want a repeatable comparison of the two rosters, so that the decision
to retire the technology agents rests on measurements.

#### Acceptance Criteria

10.1. THE delivery SHALL define a comparison procedure: the same approved plan run once per roster
on the same harness and model preset, from the same base commit.

10.2. THE procedure SHALL record, per run: tasks completed, task-gate failures and rework rounds,
review-council findings by severity, agent picks and their confidence, standards-tool calls per
task, tokens and wall time.

10.3. THE comparison SHALL cover at least one server-only plan, one client-only plan and one plan
whose tasks cross server and client.

10.4. THE result SHALL be recorded as an architecture decision record that states whether the
technology agents are retired, kept, or kept for named technologies.

### Requirement 11: Retirement is a separate, later decision with a migration path

**User Story:** As a host repository owner, I want old agent names to keep working for a stated
period if the technology agents are retired, so that my configuration does not silently stop gating.

#### Acceptance Criteria

11.1. THE technology agents SHALL NOT be removed, renamed or deprecated by this specification's
approved phases; retirement starts only after the decision record of Requirement 10.4.

11.2. IF retirement is decided THEN a host's agent-keyed `review.task-gates` entries SHALL keep
resolving for at least two minor releases, through the shipped map of Requirement 6.3.

11.3. IF retirement is decided THEN the system SHALL provide a command that rewrites a host's
agent-keyed task gates as technology-keyed gates, prints the change before writing, and changes
nothing else in the file.

11.4. IF retirement is decided THEN `docs validate` SHALL report each agent-keyed entry for a
retired agent as a warning that names the migration command, for the whole alias period.

11.5. IF retirement is decided THEN a plan or task file that names a retired agent SHALL resolve to
its role agent and SHALL report the substitution.

11.6. IF retirement is decided THEN a Squad update SHALL remove a retired agent's rendered files
only when the lock records that the Squad wrote them and the host has not edited them.

### Requirement 12: Models and cost stay visible

**User Story:** As the owner, I want to see what the role roster does to model choice and cost, so
that a quality change is not mistaken for a roster effect.

#### Acceptance Criteria

12.1. THE role agents SHALL each name one model profile, and the design SHALL state which
technology agents change model tier as a result, per harness.

12.2. THE comparison of Requirement 10 SHALL run both rosters on a model preset that gives every
worker the same model, so the roster is the only variable.

12.3. THE role agents SHALL add no new model profile unless the comparison shows one is needed.
