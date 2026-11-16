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
  - developer tiers
  - dev-easy
  - dev-medium
  - dev-hard
  - platform-researcher
  - technology folder
  - task difficulty
  - task prompt tool
  - arbiter rules by folder
---

# Requirements Document

**Phase status:** Draft

## Introduction

Kyber-Squad ships 24 agents. Eleven of them are one worker per technology (`csharp-dev`, `dal-dev`,
`react-dev`, `typescript-dev`, `python-dev`, `go-dev`, `rust-dev`, `maui-dev`, `tauri-dev`,
`pulumi-dev`, `sql-database-architect`), and each carries its technology's rules in its own text.

This specification replaces them with three developer agents that differ only in model strength,
and moves everything a technology agent knows into that technology's folder: a standard, a rules
file, a skills file and a machine rules file. One docs tool builds the whole prompt for a task, so
the conductor hands a worker a single, complete brief.

Owner decisions made on 2026-11-16 that shape these requirements:

1. **Three developer tiers** replace the server, client and devops split of the first draft:
   `dev-easy`, `dev-medium`, `dev-hard`.
2. **Who rates difficulty.** The architect rates each task. When the Arbiter runs with a decision
   model, that model's rating is used instead. No gate checks the rating.
3. **A failed task moves one tier up** on its next attempt. This is a conductor rule.
4. **Agents that go away:** the ten technology agents, `sql-database-architect` (planning moves to
   `architect`, its rules to a SQL Server technology folder), `test-dev` (testing becomes a section
   of every standard), `azure-reader` (replaced by `platform-researcher`) and `github-devops`
   (replaced by `devops`, which works with GitHub, Azure DevOps, GitLab or Jira through a folder
   per platform).
5. **Agents that stay:** `conductor`, `architect`, `product-owner`, `code-reviewer`, `review-lens`,
   `review-triage`, `bug-crusher`, `bug-crusher-investigator`, `research-agent`, `docs-dev`.
6. **The model comes from the agent.** Each agent names a model profile, as today, and choosing the
   agent chooses the model. Nothing assigns a model at dispatch. The conductor has no model
   profile: it runs on the model the user or the headless caller starts it with.
7. **Technology folders** follow the plan on
   [pull request 625](https://github.com/dpalfery/kyber-weave/pull/625), with the standard named
   `<name>.standards.md` beside `<name>.rules.md` and `<name>.skills.md`. This specification adds a machine
   rules file for the Arbiter beside them. Role folders have the same four files.
11. **The old agents stay for now.** They remain selectable until the owner decides to remove them.
8. **Standards are templates.** Kyber-Weave ships one template folder per technology, the community
   can submit more, and onboarding installs the ones the user picks.
9. **One stored-file format.** Data files this work adds or changes are YAML, the task file
   included.
10. **One specification** covers all of it.

Terms used below:

- **Developer agent**: `dev-easy`, `dev-medium` or `dev-hard`.
- **Technology agent**: one of the eleven per-technology workers named above.
- **Technology folder**: `docs/standards/<technology>/` in a host repository.
- **Role folder**: a folder of the same shape for a role instead of a technology (`conductor`,
  `architect`, `reviewer`, `devops`).
- **Platform folder**: a technology folder for a delivery platform (`github`, `azure-devops`,
  `gitlab`, `jira`).
- **Folder file names**: every file in a technology or role folder starts with the folder's name,
  shown below as `<name>`: `csharp.standards.md`, `csharp.rules.md`, `csharp.skills.md`,
  `csharp.arbiter-rules.yml`.
- **Machine rules file**: `<name>.arbiter-rules.yml`, rules the Arbiter evaluates. `<name>.rules.md` is prose a
  model reads; the Arbiter cannot evaluate it.
- **Task prompt**: the single text a worker receives for one task.
- **Decision model**: the small model the Arbiter asks for judgement calls (JEV).
- **Task gate**: a command a worker runs on its own change before it reports done.
- **Worker roster**: the set of worker agents a run may dispatch. `technology` is today's set;
  `tiered` is the new set.

The evidence behind each requirement is in the [design](design.md#evidence).

## Requirements

### Requirement 1: Three developer agents replace the technology agents

**User Story:** As the owner, I want three developer agents that differ only in model strength, so
that a task goes to the cheapest agent that can do it and no agent is tied to a technology.

#### Acceptance Criteria

1.1. THE Squad source SHALL define three worker agents named `dev-easy`, `dev-medium` and `dev-hard`.

1.2. THE three developer agents SHALL share one body, one capability profile (`worker`) and one
Copilot tool list; they SHALL differ only in name, description and model profile.

1.3. A developer agent's body SHALL name no technology-specific rule, command, library or skill.

1.4. THE Squad source SHALL define one read-only research agent named `platform-researcher` that replaces
`azure-reader` (Requirement 8).

1.5. THE agents `conductor`, `architect`, `product-owner`, `code-reviewer`, `review-lens`,
`review-triage`, `bug-crusher`, `bug-crusher-investigator`, `research-agent` and `docs-dev` SHALL
stay, with unchanged names.

1.6. WHEN the removal phase (Requirement 13) completes THEN the ten technology agents,
`sql-database-architect`, `test-dev`, `azure-reader` and `github-devops` SHALL no longer be in the
Squad source.

1.7. THE `architect` agent SHALL take over the schema and data-model planning
`sql-database-architect` does today; writing SQL SHALL be developer-agent work.

1.8. THE thirteen harness renderers SHALL render the new agents with no renderer code that names an
agent.

1.9. EVERY agent that may dispatch a technology agent, `azure-reader` or `github-devops` today SHALL
list its replacement in its `delegates-to`, so no existing Arbiter rule refuses the dispatch.

1.10. THE Squad source SHALL define a delivery agent named `devops` that replaces `github-devops`,
with the `publishing-worker` capability profile, and that takes its platform from the task or from
the host configuration (Requirement 6.7).

1.11. EACH agent SHALL name its model profile in its own definition, as today; the system SHALL NOT
assign a model at dispatch as part of this work.

1.12. THE `dev-hard` agent SHALL use a model profile of its own, separate from the planning
profiles, so a change to one does not change the other.

1.13. THE `conductor` agent SHALL have no model profile; it SHALL run on the model its session was
started with, by the user or by the headless caller.

### Requirement 2: Every task carries a difficulty

**User Story:** As the owner, I want each task rated easy, medium or hard by judgement, so that the
conductor can pick a developer agent without a gate deciding it.

#### Acceptance Criteria

2.1. THE plan and specification task formats SHALL accept a `Difficulty:` line with the value
`easy`, `medium` or `hard`.

2.2. THE `architect` and `product-owner` instructions SHALL require a `Difficulty:` line on every
task that changes files.

2.3. WHEN a task file is written THEN each task entry SHALL carry its difficulty.

2.4. IF a task has no `Difficulty:` line THEN the system SHALL treat it as `medium`.

2.5. IF a `Difficulty:` line holds another value THEN `docs validate` SHALL report an error naming
the task and the three legal values.

2.6. WHEN the Arbiter is enabled with a decision model and that model rates the task at or above its
confidence floor THEN the model's rating SHALL be used for the dispatch instead of the architect's.

2.7. WHEN the Arbiter is off, has no decision model, or the model's answer is below its confidence
floor THEN the architect's rating SHALL be used.

2.8. THE system SHALL add no gate and no blocking rule that judges whether a difficulty rating is
correct.

2.9. THE rating in use for a dispatch, and its source (`architect`, `decision-model` or `default`),
SHALL be recorded where the run's other dispatch facts are recorded.

2.10. A task SHALL have one rating in use at a time; no later step SHALL rate it again and choose a
different model for the same attempt.

### Requirement 3: A failed task moves one tier up

**User Story:** As the owner, I want a task that failed to be retried by the next stronger developer
agent, so that a wrong difficulty rating costs one attempt and corrects itself.

#### Acceptance Criteria

3.1. WHEN a developer agent's attempt at a task fails and a higher tier exists THEN the conductor
SHALL dispatch the next attempt to the next tier up.

3.2. THE order SHALL be `dev-easy`, `dev-medium`, `dev-hard`.

3.3. IF the failed attempt ran on `dev-hard` THEN the next attempt SHALL stay on `dev-hard`, and the
existing limit of three rework dispatches per failure cluster SHALL apply unchanged.

3.4. THE step-up rule SHALL be a rule in the `conductor` role folder (Requirement 7), and the
conductor's instructions SHALL state the same rule in prose for a run with the Arbiter off.

3.5. A failed attempt SHALL never be retried on a lower tier.

3.6. A developer-agent dispatch that belongs to no task SHALL start on `dev-medium` and SHALL follow
the same step-up.

### Requirement 4: A task declares one technology

**User Story:** As an architect, I want each task to name the one technology it is written in, so
that the right standards, rules, skills and gates are chosen by the machine and fit in one prompt.

#### Acceptance Criteria

4.1. THE plan and specification task formats SHALL accept a `Technology:` line naming exactly one
technology.

4.2. WHEN a task file is written THEN each task entry SHALL carry its technology.

4.3. IF a task names a technology the host's `ontology.technologies` does not declare THEN
`docs validate` SHALL report an error naming the task, the value and the declared list.

4.4. IF the active roster is `tiered` and a task that changes files has no `Technology:` line THEN
`docs validate` SHALL report an error naming the task.

4.5. IF a `Technology:` line names more than one technology THEN `docs validate` SHALL report an
error that tells the author to split the task.

4.6. A technology template SHALL be able to name one parent technology it builds on (for example
React on TypeScript); a task in that technology SHALL also receive the parent's rules and skills.

4.7. THE `architect` and `product-owner` instructions SHALL require work that spans two unrelated
technologies (for example an endpoint and its database migration) to be written as separate tasks
with a dependency between them.

4.8. THE `architect` and `product-owner` SHALL read a technology's standard through
`docs_get_coding_standard_for_technology`, only for a technology a task names, and SHALL read a
standards file directly only when the tool is missing or reports a failure.

4.9. THE `Capabilities:` line SHALL stay as it is: plain words, no fixed vocabulary.

### Requirement 5: A technology folder holds everything about a technology

**User Story:** As a host repository owner, I want one folder per technology that holds its
standard, rules, skills and machine rules, so that I change how my project uses a technology in one
place.

#### Acceptance Criteria

5.1. A technology folder and a role folder SHALL each hold four files, every one named with the
folder's own name as its prefix: `<name>.standards.md` (the standard), `<name>.rules.md`,
`<name>.skills.md` and `<name>.arbiter-rules.yml`.

5.2. THE standard SHALL use these six headings, in this order: Naming and layout; Coding standards;
Testing; Commands; Security and data handling; Done checklist.

5.3. THE Testing section SHALL state how tests are written, named, placed and run for that
technology, and SHALL replace what `test-dev` and the `test` standard state today.

5.4. Rules that hold for every technology, including the cross-technology testing rules, SHALL live
once in `shared-rules.md`.

5.5. THE delivery SHALL produce an inventory of every technology-specific rule in the bodies of the
eleven technology agents, `test-dev` and `azure-reader`, each mapped to the file and heading that
now states it.

5.6. WHEN the inventory marks a rule as stated nowhere THEN the rule SHALL be added to that
technology's template before the agent that held it is removed.

5.7. THE rules `sql-database-architect` holds SHALL move to a SQL Server technology folder.

5.8. `docs validate` SHALL report a technology folder that lacks one of the four files, or a
standard that lacks one of the six headings, naming the folder and what is missing.

5.9. `<name>.arbiter-rules.yml` SHALL be valid when it declares no rule.

5.10. THE system SHALL ship no role folder for testing; testing guidance SHALL live only in
`shared-rules.md` and in each standard's Testing section.

### Requirement 6: Standards ship as templates

**User Story:** As a new user, I want to pick my technologies during onboarding and receive a
complete folder for each, so that my agents follow sound standards on the first run.

#### Acceptance Criteria

6.1. Kyber-Weave SHALL ship one template folder per supported technology, each holding the four
files of Requirement 5.1.

6.2. WHEN the user names technologies during onboarding THEN the system SHALL install the template
folder for each named technology and no other.

6.3. IF a host already has a file the template would write THEN the system SHALL leave the host's
file unchanged and report it.

6.4. THE system SHALL provide a command that shows, per technology, how a host's folder differs from
the current template, and changes nothing.

6.5. THE repository SHALL document how to contribute a new technology template, and one check SHALL
decide whether a submitted template is complete (Requirement 5.8).

6.6. THE `conductor`, `architect`, `reviewer` and `devops` role folders SHALL ship as templates in
the same way, and a host SHALL be able to override them in its own repository.

6.7. Kyber-Weave SHALL ship one platform folder template each for GitHub, Azure DevOps, GitLab and
Jira; onboarding SHALL install the ones the user names, and the `devops` agent SHALL receive the
rules and skills of the platform its task names.

### Requirement 7: The Arbiter reads its rules from role and technology folders

**User Story:** As a host repository owner, I want the Arbiter's rules to sit beside the standards
they belong to, so that I see and override them where I already work.

#### Acceptance Criteria

7.1. EACH of the 36 shipped Arbiter rules SHALL move to the machine rules file of exactly one role
folder, with its id, question, answers and effects unchanged.

7.2. WHEN the Arbiter evaluates an event THEN it SHALL apply the rules of the role folders, and,
when the event concerns a task, the rules of that task's technology.

7.3. WHEN a host folder declares a rule id the shipped template also declares THEN the host's
declaration SHALL be used.

7.4. THE existing `arbiter.rules` settings in the host configuration SHALL keep working and SHALL
be applied after the folders are read.

7.5. No Arbiter rule id SHALL be reused or renumbered.

7.6. No rule SHALL be locked: a host SHALL be able to turn any rule down to log only, through the
switch that rule has today.

7.7. IF a machine rules file cannot be parsed, or names a trigger or fact the Arbiter does not have,
THEN `kyber-weave arbiter validate` SHALL report an error naming the file and the rule, and the
Arbiter SHALL use the shipped rules for that folder.

7.8. THE rule that exempts `test-dev` by name from the test-first check SHALL instead exempt a task
whose files are all test files, decided from the task's file list.

7.9. WHEN the Arbiter is off THEN nothing in this requirement SHALL be needed for a run to work.

### Requirement 8: A platform research agent reads live systems

**User Story:** As an architect or bug investigator, I want one read-only agent that can inspect a
cloud or platform through its own tools, so that plans rest on how a deployed system really is.

#### Acceptance Criteria

8.1. THE `platform-researcher` agent SHALL take the technology to inspect from its task (for example
`azure`, `aws`, `kubernetes`) and SHALL load that technology's skills and rules.

8.2. THE technology's `<name>.skills.md` SHALL state how to sign in, which command-line tools and tool
servers to use, how to query, and how to shape output.

8.3. THE technology's `<name>.rules.md` SHALL state that the agent changes nothing and prints no secret.

8.4. THE agent's capability profile SHALL deny file writes, publishing and delegation, and SHALL
allow command execution.

8.5. THE technology's `<name>.arbiter-rules.yml` SHALL be able to declare the command forms that are
allowed, and a command outside them SHALL be refused on a harness where the Arbiter sees a
sub-agent's own tool calls.

8.6. THE design SHALL state on which harnesses 9.5 is enforced and on which it is an instruction
only.

8.7. IF the task names a technology with no folder THEN the agent SHALL report that and SHALL NOT
improvise commands.

### Requirement 9: The worker starts with its whole task prompt

**User Story:** As a conductor, I want to dispatch a worker with a task id and have the machine
give the worker its full prompt, so that the worker starts with everything and I never read or
retype it.

#### Acceptance Criteria

9.1. ONE service SHALL build the task prompt; the dispatch hook, the docs tool and
`kyber-weave docs packet` SHALL all use it and SHALL produce the same text for the same task.

9.2. THE task prompt SHALL contain, in this order: the task's own prompt from the task file; the
host facts; the shared rules; the parent technology's rules and skills, when the template names a
parent; then the task technology's rules, skills and standard.

9.3. WHEN a conductor dispatches a worker with a task id on a harness whose dispatch hook can
rewrite the dispatch THEN the hook SHALL replace the dispatch text with the full task prompt before
the worker starts, and the worker SHALL NOT need to read a file to get it.

9.4. THE conductor's dispatch SHALL carry the task id and SHALL NOT carry the prompt content; the
prompt text SHALL NOT enter the conductor's context on any harness.

9.5. WHERE a harness's dispatch hook cannot rewrite the dispatch, the system SHALL write the task
prompt to one untracked file in the run's scratch folder, the dispatch SHALL name that file, and
the worker's first step SHALL be to read it.

9.6. THE design SHALL state, per harness, which of 9.3 and 9.5 applies, from evidence.

9.7. THE dispatch hook of 9.3 SHALL work when the Arbiter is off.

9.8. IF the hook cannot build the prompt THEN the dispatch SHALL be stopped with a reason that
names the repair command; a worker SHALL never start with only the task id.

9.9. THE docs tool server SHALL provide a tool that takes a task id and replies with the agent to
dispatch, the difficulty in use and its source, the technology, anything left out, and, on a
harness of 9.5, the prompt file's path. Its reply SHALL NOT contain the prompt text.

9.10. THE task prompt SHALL NOT be written into the task file or any tracked file, so the task
file's digest and staleness check are unchanged.

9.11. EACH stitched part SHALL be enclosed in a named tag that marks where it starts and ends; the
content inside a tag SHALL stay Markdown.

9.12. WHEN the stitched text would exceed the prompt budget THEN rules SHALL be kept whole, skills
and standard sections SHALL be cut by relevance to the task, and the prompt SHALL list what was
left out.

9.13. THE shipped prompt budget SHALL be large enough that, for every shipped template together
with its parent, a task receives its rules, skills and standard with nothing cut; one test SHALL
prove this for every template.

9.14. A developer agent's instructions SHALL NOT require it to fetch its standards at start; it MAY
call `docs_get_coding_standard_for_technology` for a topic the prompt left out.

9.15. IF the task file is missing or does not match its plan THEN no prompt SHALL be built and the
reply SHALL say which command repairs it.

9.16. IF the named technology has no folder THEN the prompt SHALL say so in that part and the rest
SHALL still be built.

9.17. WHEN a dispatch names a technology and no task id THEN the prompt SHALL hold the shared rules
and that technology's rules, skills and standard, for a dispatch that belongs to no task.

9.18. No existing Arbiter rule SHALL treat the expanded prompt, or a dispatch that names the prompt
file, as a dispatch that tells a worker to read a plan.

### Requirement 10: Task gates resolve by technology

**User Story:** As a host repository owner, I want task gates declared per technology, so that they
keep working when no agent is named after a technology.

#### Acceptance Criteria

10.1. THE host configuration SHALL accept task gates keyed by technology.

10.2. WHEN a developer agent runs its task gates THEN the system SHALL run the gates declared for
the task's technology.

10.3. IF a host declares no technology-keyed gates for a technology THEN the system SHALL use the
gates declared under the technology agent that technology maps to, from one shipped map.

10.4. IF no gate resolves for the task's technology and its template names a parent THEN the
system SHALL use the parent's gates.

10.5. IF a developer agent's task names a technology and no gate resolves THEN the system SHALL
report an error and SHALL exit non-zero.

10.6. THE task prompt, the test command it carries and the Arbiter's gates-declared fact SHALL use
the same resolution as the gate run.

10.7. THE council's `review.gates` SHALL NOT change.

10.8. THIS specification SHALL add no gate.

### Requirement 11: One stored-file format

**User Story:** As the owner, I want every file this work adds or changes to use one format, so that
people and tools learn one syntax.

#### Acceptance Criteria

11.1. EVERY data file this work adds SHALL be YAML.

11.2. THE design SHALL list every data file the work reads or writes with its format today, and
SHALL state for each that is not YAML whether it converts and when.

11.3. IF a file's format changes THEN the system SHALL read the old format for at least two minor
releases and SHALL report it as a warning that names the conversion command.

11.4. Prose files (`README.md`, `<name>.rules.md`, `<name>.skills.md`, agent files) SHALL stay Markdown.

11.5. THE task file SHALL be written as YAML.

### Requirement 12: The old and new rosters can run side by side until removal

**User Story:** As the owner, I want to switch a run between today's agents and the new ones, so
that I can compare them before the old ones are deleted.

#### Acceptance Criteria

12.1. THE host configuration SHALL accept a `worker-roster` value of `technology` or `tiered`,
defaulting to `technology` until the removal phase.

12.2. THE task file SHALL record the roster it was written under, and every later reader SHALL take
the run's roster from the task file.

12.3. WHEN the roster is `technology` THEN a run SHALL behave as it does today, apart from the task
prompt of Requirement 9, and no file that roster reads SHALL be removed before the removal phase.

12.4. THE delivery SHALL define a comparison procedure: the same approved plan run once per roster
from the same base commit on the same harness.

12.5. THE procedure SHALL record, per run: tasks completed, attempts per task and tier of each
attempt, task-gate failures, review findings by severity, tokens and wall time.

### Requirement 13: Removal has a migration path

**User Story:** As a host repository owner, I want old agent names to keep working for a stated
period, so that my configuration does not silently stop gating.

#### Acceptance Criteria

13.1. THE removal phase SHALL start only after the comparison of Requirement 12 has been run and the
owner has recorded the decision.

13.2. A host's agent-keyed task-gate entries SHALL keep resolving for at least two minor releases
after removal, through the shipped map of Requirement 10.3.

13.3. THE system SHALL provide a command that rewrites a host's agent-keyed task gates as
technology-keyed gates, prints the change before writing, and changes nothing else.

13.4. `docs validate` SHALL report each agent-keyed entry for a removed agent as a warning that
names the migration command.

13.5. A plan or task file that names a removed agent SHALL resolve to a developer agent by the
task's difficulty and SHALL report the substitution.

13.6. A Squad update SHALL remove a removed agent's rendered files only when the lock records that
the Squad wrote them and the host has not edited them.

13.7. WHEN removal completes THEN `worker-roster` SHALL default to `tiered`, and the value
`technology` SHALL be reported as an error that names this change.
