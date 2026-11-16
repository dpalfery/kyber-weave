---
id: specs/role-based-worker-agents/tasks
title: Role-based worker agents tasks
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-11-16
component: KyberSquad
documentation-impact: extends-component
impact-components: [KyberSquad, ReviewCouncil, DocGraph, KyberArbiter]
keywords:
  - developer tiers
  - technology folders
  - task prompt
  - dispatch hook
  - test-first
---

# Implementation Tasks

**Phase status:** Approved

Owner approved 2026-11-16 ('Approve tasks'), given directly in the authoring session.

**Development mode:** test-first

**Autonomy:** autonomous

Written 2026-11-16 against the draft [requirements](requirements.md) and [design](design.md). The
owner approved all three on 2026-11-16. The specification awaits the final approve-and-execute
decision; until then it is not Ready and no machine task list has been generated from this file.

These tasks deliver five phases. Each phase is its own pull request and leaves the product working
with today's agents as the default, so any phase can ship without the next.

| Phase | Delivers | Tasks | Shippable because |
|---|---|---|---|
| 1. Technology folders | Four prefixed files per folder, six headings, templates, role and platform folders, onboarding by choice, migration of existing folders | 1.1 to 1.10 | Additive: nothing today's agents read is removed |
| 2. Task fields and the task prompt | Difficulty and technology on tasks, the YAML task list file, gates by technology, the prompt service, hook insertion with the file fallback | 2.1 to 2.12 | Optional fields; today's agents receive the richer prompt |
| 3. Agents | Three developer agents, `platform-researcher`, `devops`, the `dev-hard` profile, the roster switch, the step-up | 3.1 to 3.9 | The default roster stays `technology` |
| 4. Arbiter rules from folders | The 36 rules split into role folders, folder loading, the command allow-list | 4.1 to 4.6 | Rule ids and effects are unchanged |
| 5. Comparison and removal | The comparison, the owner's decision, migration, removal of the old agents | 5.1 to 5.6 | Removal starts only after the owner's recorded decision |
| Closeout | Harvest and archive | 6.1 | After 5.6 |

Phase 1 cannot start until the plan on
[pull request 625](https://github.com/dpalfery/kyber-weave/pull/625) has landed with the changes the
[design](design.md#open-questions) names.

## How these tasks are dispatched

- **One task, one worker, tests and code together.** Each implementation task writes its tests,
  runs them failing, then implements. No task runs the full suite.
- **Commands.** Build once, then run the row's classes, from the repository root:
  - build: `dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release`
  - test: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "<the row's filter>"`
  - prompt wording: `dotnet test tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release --filter "<the row's filter>"`
  - docs: `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **A run that reports zero tests verified nothing.**
- **Rule ids.** Every new `KW-*` id takes the next free number in its family; none is reused.
- **Phase order.** A phase starts after the last task of the phase before it. Inside a phase, tasks
  with no dependency between them and no shared file may run in parallel.

## Tasks

### Phase 1: Technology folders

- [ ] 1.1 Name and check the four folder files
  - **Objective:** Make a technology or role folder hold four files that start with the folder's name, and report a folder or standard that is incomplete.
  - **Files:**
    - `src/KyberWeave.Domain/Configuration/DocsLayout.cs`: paths for `<name>.standards.md`, `<name>.rules.md`, `<name>.skills.md`, `<name>.arbiter-rules.yml`
    - `src/KyberWeave.Domain/Configuration/OntologyConfig.cs`: a `roles` list beside `technologies`
    - `src/KyberWeave.Domain/Docs/Validation/DocSpecValidator.cs`: the four-file check and the six-heading check, each with a new `KW-DOC-SPEC-*` id
    - `tests/KyberWeave.Tests/StandardsFolderShapeTests.cs` (new): `KyberWeave.Tests.StandardsFolderShapeTests`
  - **Acceptance criteria:** A folder that still holds its standard as `README.md` is read and raises a warning that names the migrate command, not an error; otherwise a folder missing one of the four files is an error naming the folder and the file; a standard missing one of the six headings, or holding them out of order, is an error naming the heading; an Arbiter rules file that declares no rule is valid; a role declared in `ontology.roles` is checked the same way.
  - **Capabilities:** C# validation rules and configuration parsing in the documentation engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~StandardsFolderShapeTests"`
  - **Depends on:** none
  - _Requirements: 5.1, 5.2, 5.8, 5.9, 5.11_

- [ ] 1.2 Reshape the shipped templates
  - **Objective:** Give every shipped technology template the four prefixed files and the six headings, with a Testing section in each standard.
  - **Files:**
    - `products/kyber-squad/standards/`: every technology folder
    - `src/KyberWeave.Domain/Docs/Scaffolding/KyberStandardsTemplates.cs`: the template list and file names
    - `tests/KyberWeave.Tests/KyberStandardsTemplateShapeTests.cs` (new): `KyberWeave.Tests.KyberStandardsTemplateShapeTests`
  - **Acceptance criteria:** Every shipped template passes the checks of task 1.1; every standard's headings are Naming and layout, Coding standards, Testing, Commands, Security and data handling, Done checklist, in that order; no content a technology agent reads today is deleted.
  - **Capabilities:** Technical writing of coding standards in Markdown for many languages, with C# template registration and xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberStandardsTemplateShapeTests"`
  - **Depends on:** 1.1
  - _Requirements: 5.1, 5.2, 5.3, 6.1_

- [ ] 1.3 Inventory what the agents know
  - **Objective:** List every technology-specific rule in the bodies of the eleven technology agents, `test-dev` and `azure-reader`, and map each to the template file and heading that states it, or mark it missing.
  - **Files:**
    - `docs/kyber-squad/agent-rule-inventory.md` (new)
    - `docs/kyber-squad/README.md`: index row
  - **Acceptance criteria:** Every rule sentence in those thirteen agent bodies has a row; each row names a template file and heading or says "missing"; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation analysis: reading agent instructions and coding standards and writing a governed Markdown inventory
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 1.2
  - _Requirements: 5.5_

- [ ] 1.4 Move the missing rules into the templates
  - **Objective:** Add every rule the inventory marks missing to its technology's template, add a SQL Server template and an AWS template, and put the cross-technology testing rules in the shared rules file.
  - **Files:**
    - `products/kyber-squad/standards/`: the affected technology folders, new `sql-server/` and `aws/`, and `azure/`
    - `products/kyber-squad/standards/shared-rules.md`
    - `docs/kyber-squad/agent-rule-inventory.md`: every row resolved
    - `tests/KyberWeave.Tests/KyberStandardsTemplateShapeTests.cs`: the two new templates
  - **Acceptance criteria:** No inventory row says "missing"; the SQL Server template states the rules `sql-database-architect` holds; the Azure template's skills and rules state what `azure-reader` holds, including "change nothing" and "print no secret"; the `test` template, the `sql` template and the `testing` skill are still present.
  - **Capabilities:** Technical writing of coding standards, rules and skills in Markdown, including SQL Server, Azure and AWS
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberStandardsTemplateShapeTests"`
  - **Depends on:** 1.3
  - _Requirements: 5.3, 5.4, 5.6, 5.7, 8.2, 8.3_

- [ ] 1.5 Add the role and platform folder templates
  - **Objective:** Ship template folders for the `conductor`, `architect`, `reviewer` and `devops` roles and for the GitHub, Azure DevOps, GitLab and Jira platforms.
  - **Files:**
    - `products/kyber-squad/standards/`: `conductor/`, `architect/`, `reviewer/`, `devops/`, `github/`, `azure-devops/`, `gitlab/`, `jira/`
    - `src/KyberWeave.Domain/Docs/Scaffolding/KyberStandardsTemplates.cs`: register them
    - `tests/KyberWeave.Tests/KyberStandardsTemplateShapeTests.cs`: role and platform cases
  - **Acceptance criteria:** Each of the eight folders passes the checks of task 1.1; the GitHub template states what the `github-actions` standard and the `github-devops` skill state today; no folder for a testing role exists.
  - **Capabilities:** Technical writing of delivery-platform standards (GitHub, Azure DevOps, GitLab, Jira) in Markdown, with C# template registration
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberStandardsTemplateShapeTests"`
  - **Depends on:** 1.2
  - _Requirements: 5.10, 6.6, 6.7_

- [ ] 1.6 Let a template name one parent
  - **Objective:** Accept a `builds-on` key in a standard's frontmatter that names one other declared technology.
  - **Files:**
    - `src/KyberWeave.Domain/Docs/Validation/DocSpecValidator.cs`: the key, with a new `KW-DOC-SPEC-*` id for an undeclared parent, a parent that names itself, or a chain longer than one
    - `products/kyber-squad/standards/`: `react`, `tauri` and `data-access-layer` name their parent
    - `tests/KyberWeave.Tests/StandardsBuildsOnTests.cs` (new): `KyberWeave.Tests.StandardsBuildsOnTests`
  - **Acceptance criteria:** A declared parent is accepted; an undeclared parent, a self reference and a parent that itself names a parent are each an error naming the folder.
  - **Capabilities:** C# validation rules in the documentation engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~StandardsBuildsOnTests"`
  - **Depends on:** 1.1
  - _Requirements: 4.6_

- [ ] 1.7 Install only the chosen templates
  - **Objective:** Make onboarding and `docs init` install the template folder for each technology, role and platform the user names, and leave any file the host already has untouched.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`, `src/KyberWeave.Cli/Commands/Docs/DocsInitSettings.cs`: the choice
    - `src/KyberWeave.Application/Docs/Scaffolding/DocsScaffolder.cs`: install by name, skip existing files and report them
    - `tests/KyberWeave.Tests/DocsInitTemplateChoiceTests.cs` (new): `KyberWeave.Tests.DocsInitTemplateChoiceTests`
  - **Acceptance criteria:** Naming two technologies installs exactly those two folders plus the shared rules file; a file that exists is left byte-for-byte unchanged and listed in the report; `--kyber-standards` still installs every template.
  - **Capabilities:** C# command-line and file scaffolding work, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DocsInitTemplateChoiceTests|FullyQualifiedName~DocsScaffolderTests"`
  - **Depends on:** 1.2, 1.5
  - _Requirements: 6.2, 6.3_

- [ ] 1.8 Show how a host folder differs from its template
  - **Objective:** Add `kyber-weave docs standards diff [name]`, which prints the difference between a host folder and the current template and writes nothing.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Docs/DocsStandardsDiffCommand.cs` (new), `src/KyberWeave.Cli/CliApplication.cs`: register it
    - `src/KyberWeave.Application/Docs/Scaffolding/StandardsTemplateDiff.cs` (new)
    - `tests/KyberWeave.Tests/DocsStandardsDiffCommandTests.cs` (new): `KyberWeave.Tests.DocsStandardsDiffCommandTests`
    - `docs/cli-command-surface.md`: the command
  - **Acceptance criteria:** The command lists files the host lacks, files only the host has, and changed files per folder; with no name it covers every installed folder; it exits 0 and changes no file.
  - **Capabilities:** C# command-line work with text comparison, xUnit tests, and one governed Markdown table edit
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DocsStandardsDiffCommandTests"`
  - **Depends on:** 1.2
  - _Requirements: 6.4_

- [ ] 1.9 Document phase 1 and how to contribute a template
  - **Objective:** Describe the folder layout, the six headings, installing by choice and the diff command, and write the contribution guide for a new technology template.
  - **Files:**
    - `docs/docgraph/onboarding.md`, `docs/configuration.md`, `docs/documentation-ontology.md`
    - `CONTRIBUTING.md`: a section on submitting a technology template
    - `products/kyber-squad/standards/README.md`
  - **Acceptance criteria:** The guide states the four files, the six headings and the one check that decides completeness; the onboarding guide and the configuration guide agree on how many templates ship; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation: onboarding and contribution guides in governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 1.4, 1.5, 1.6, 1.7, 1.8, 1.10
  - _Requirements: 6.5_

- [ ] 1.10 Migrate existing standards folders
  - **Objective:** Add `kyber-weave docs standards migrate`, which renames a host's standards files to the prefixed names and adds the missing ones, and run it on this repository.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Docs/DocsStandardsMigrateCommand.cs` (new), `src/KyberWeave.Cli/CliApplication.cs`
    - `src/KyberWeave.Application/Docs/Scaffolding/StandardsFolderMigration.cs` (new)
    - `docs/standards/`: this repository's seven folders
    - `src/KyberWeave.Domain/Docs/Scaffolding/ConfigRegMarkdown.cs`: the standard's registry path
    - `tests/KyberWeave.Tests/DocsStandardsMigrateCommandTests.cs` (new): `KyberWeave.Tests.DocsStandardsMigrateCommandTests`
    - `docs/cli-command-surface.md`
  - **Acceptance criteria:** The command prints each rename and each added file before writing; file content is unchanged byte for byte; a second run changes nothing; the registry property for a technology's standard points at the new name; this repository's folders pass the checks of task 1.1 with no warning.
  - **Capabilities:** C# command-line and file migration work, with xUnit tests, and governed Markdown moves
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DocsStandardsMigrateCommandTests"`
  - **Depends on:** 1.1
  - _Requirements: 5.12_

### Phase 2: Task fields and the task prompt

- [ ] 2.1 Confirm which harnesses let a hook rewrite the dispatch
  - **Objective:** For each of the thirteen harnesses, establish from its current documentation and one live check whether a hook on the dispatch call can replace the dispatch text and whether the replaced text stays out of the dispatching agent's own context, and record the result.
  - **Files:**
    - `docs/kyber-arbiter/architecture.md`: the per-harness table, including the correction that Antigravity documents an `overwrite` field
    - `docs/kyber-squad/architecture.md`: which delivery path each harness uses
  - **Acceptance criteria:** Every harness has a row with "rewrite" or "file", the documentation link and the date checked; a harness is "rewrite" only when the live check showed the worker received the replaced text and the dispatching agent's transcript did not; the row says what happens when the hook is not trusted or not enabled; for Antigravity the row states the decision value used and that the whole `Subagents` list is replaced; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Research of coding-agent harness hook documentation and governed Markdown writing
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** none
  - _Requirements: 9.6_

- [ ] 2.2 Read a task's difficulty and technology
  - **Objective:** Read `Difficulty:` and `Technology:` lines from a plan or specification task and carry them on the parsed task.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/Plans/PlanDocumentParser.cs`: both lines
    - `src/KyberWeave.Domain/Docs/Validation/PlanTaskFile.cs`: `difficulty`, `difficulty-source`, `technology` on an entry; schema version plus one
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileGenerator.cs`: write the fields; a missing difficulty becomes `medium` with source `default`
    - `tests/KyberWeave.Tests/PlanTaskDifficultyAndTechnologyTests.cs` (new): `KyberWeave.Tests.PlanTaskDifficultyAndTechnologyTests`
  - **Acceptance criteria:** `Difficulty: hard` and `Technology: csharp` reach the entry; a task with neither line gets `medium`, source `default`, and no technology; an older-version file still loads.
  - **Capabilities:** C# parsing and serialization in the documentation engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskDifficultyAndTechnologyTests"`
  - **Depends on:** none
  - _Requirements: 2.1, 2.3, 2.4, 4.1, 4.2, 4.9_

- [ ] 2.3 Write the machine task list as YAML
  - **Objective:** Write the generated task list beside a plan or specification as YAML, and keep reading the JSON form with a warning.
  - **Files:**
    - `src/KyberWeave.Domain/Docs/Validation/PlanTaskFileRules.cs`: the `.yml` names beside the `.json` names
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileGenerator.cs`, `src/KyberWeave.Application/Docs/Validation/PlanTaskService.cs`: write YAML, read either
    - `src/KyberWeave.Application/Docs/Validation/OpenWorkValidator.cs`, `src/KyberWeave.Application/Docs/Validation/PlanTaskFileValidator.cs`: both names
    - `src/KyberWeave.Domain/Arbiter/Hooks/ArchitectShellGuard.cs`, `src/KyberWeave.Application/Arbiter/Hooks/ConductorGuard.cs`: both names
    - the agent, skill and document files that name the JSON file (8 product files, 9 live documents)
    - `src/KyberWeave.Cli/Commands/Docs/`: a convert option on the tasks command
    - `tests/KyberWeave.Tests/PlanTaskFileYamlTests.cs` (new): `KyberWeave.Tests.PlanTaskFileYamlTests`
  - **Acceptance criteria:** A write produces YAML; a JSON file from an older release loads and raises one warning that names the convert command; converting produces a YAML file with the same entries and digest inputs; no JSON file is written.
  - **Capabilities:** C# serialization and command-line work in the documentation engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskFileYamlTests|FullyQualifiedName~PlanTaskFileGeneratorReadTests"`
  - **Depends on:** 2.2
  - _Requirements: 11.1, 11.2, 11.3, 11.4, 11.5_

- [ ] 2.4 Validate difficulty and technology
  - **Objective:** Report a wrong difficulty value, an undeclared technology and a task that names two technologies.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileValidator.cs`: three checks, each with a new `KW-DOC-TASKS-*` id
    - `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`: new cases
    - `docs/docgraph/governance.md`: the new ids
  - **Acceptance criteria:** `Difficulty: tricky` is an error naming the three legal values; an undeclared technology is an error naming the declared list; two technologies on one line is an error telling the author to split the task; a task with neither line reports nothing.
  - **Capabilities:** C# validation rules in the documentation engine, with xUnit tests, and one governed Markdown table edit
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskFileValidatorTests"`
  - **Depends on:** 2.2
  - _Requirements: 2.5, 2.8, 4.3, 4.5_

- [ ] 2.5 Let the decision model rate difficulty
  - **Objective:** When a decision model is configured, ask it for the task's difficulty in the call that already picks the agent, and record whose rating is in use.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskAgentPick.cs`: the question and criteria of `KW-ARB-DIFFICULTY-001`; `standard` maps to `medium`
    - `tests/KyberWeave.Tests/PlanTaskDifficultyRatingTests.cs` (new): `KyberWeave.Tests.PlanTaskDifficultyRatingTests`
  - **Acceptance criteria:** An answer at 0.8 confidence or more replaces the architect's rating with source `decision-model`; a lower answer, an error or no model keeps the architect's rating with source `architect`; one model call is made per task, not two.
  - **Capabilities:** C# work on decision-model calls in the documentation engine, with xUnit tests using a stub model
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskDifficultyRatingTests|FullyQualifiedName~PlanTaskAgentPickTests"`
  - **Depends on:** 2.2
  - _Requirements: 2.6, 2.7, 2.9_

- [ ] 2.6 Resolve task gates by technology
  - **Objective:** Replace the four lookups by agent name with one resolver that uses the task's technology, its parent, and a fallback to the old agent-keyed entries.
  - **Files:**
    - `src/KyberWeave.Domain/Configuration/ReviewConfigParser.cs`: `review.technology-gates`
    - `src/KyberWeave.Application/Review/TaskGateResolver.cs` (new)
    - `src/KyberWeave.Application/Review/ReviewGatesService.cs`, `src/KyberWeave.Application/Review/PacketTestCommandRenderer.cs`, `src/KyberWeave.Application/Docs/Validation/PacketHostFacts.cs`, `src/KyberWeave.Application/Arbiter/Facts/TriggerFactBuilder.cs`: call the resolver
    - `src/KyberWeave.Domain/Review/Catalogue/GateCatalogueRenderer.cs`: the technology-to-agent map moves to one shared type
    - `tests/KyberWeave.Tests/TaskGateResolverTests.cs` (new): `KyberWeave.Tests.TaskGateResolverTests`
  - **Acceptance criteria:** An agent with its own entry resolves as today; a task technology with a technology-keyed entry uses it; without one, the mapped agent's entry is used; with neither, the parent's gates are used; with nothing at all and a technology named, the gate run is an error with a non-zero exit; the council's gates are untouched.
  - **Capabilities:** C# configuration parsing and review-gate resolution, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TaskGateResolverTests|FullyQualifiedName~ReviewTaskGatesConfigTests|FullyQualifiedName~PacketHostFactsTests|FullyQualifiedName~GateCatalogueRenderTests"`
  - **Depends on:** 2.2, 1.6
  - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5, 10.6, 10.7, 10.8_

- [ ] 2.7 Build the task prompt in one service
  - **Objective:** Add one service that builds a task's prompt: the task's own text, host facts, shared rules, the parent's rules and skills, then the technology's rules, skills and standard, each inside a named tag, within a budget.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/TaskPromptService.cs` (new), with its contract in `src/KyberWeave.Contracts/Services/`
    - `src/KyberWeave.Domain/Configuration/KyberWeaveConfig.cs`: the prompt budget, default 60,000 characters
    - `tests/KyberWeave.Tests/TaskPromptServiceTests.cs` (new): `KyberWeave.Tests.TaskPromptServiceTests`
    - `tests/KyberWeave.Tests/TaskPromptBudgetTests.cs` (new): `KyberWeave.Tests.TaskPromptBudgetTests`
  - **Acceptance criteria:** The parts appear in the stated order, each in its tag; over the budget, rules are whole, skills are cut before standard sections, and every cut is listed in the last part; a technology with no folder says so in its part and the rest is built; a technology with no task id yields the parts from the shared rules on; for every shipped template with its parent, nothing is cut at the default budget; nothing is written to a tracked file.
  - **Capabilities:** C# text assembly and budgeting in the documentation engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TaskPromptServiceTests|FullyQualifiedName~TaskPromptBudgetTests"`
  - **Depends on:** 2.2, 2.6, 1.2
  - _Requirements: 9.1, 9.2, 9.10, 9.11, 9.12, 9.13, 9.16, 9.17_

- [ ] 2.8 Use the service from the packet command and a docs tool
  - **Objective:** Make `docs packet` print the service's prompt, and add a docs tool that tells the conductor which agent to dispatch without returning the prompt text.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Docs/DocsPacketCommand.cs`: call the service
    - `src/KyberWeave.Mcp/DocsTools.cs`, `src/KyberWeave.Mcp/DocsServerInstructions.cs`: the tool `docs_build_task_prompt_for_agent_dispatch`
    - `tests/KyberWeave.Tests/DocsTaskPromptToolTests.cs` (new): `KyberWeave.Tests.DocsTaskPromptToolTests`
    - `docs/docgraph/mcp-runbook.md`: the tool
  - **Acceptance criteria:** The command and the service give identical text for one task; the tool's reply names the agent, the difficulty and its source, the technology and anything left out, and holds no prompt text; a missing or out-of-date task list yields no prompt and names the repair command.
  - **Capabilities:** C# command-line and MCP tool work, with xUnit tests, and one governed Markdown runbook edit
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DocsTaskPromptToolTests|FullyQualifiedName~DocsPacketCommandTests"`
  - **Depends on:** 2.7
  - _Requirements: 9.1, 9.9, 9.15_

- [ ] 2.9 Insert the prompt through the dispatch hook
  - **Objective:** On every harness whose hook can rewrite the dispatch, expand the task-id line of a dispatch into the full prompt before the worker starts and keep its other lines, with the Arbiter on or off.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/Hooks/Adapters/`: the Claude, Copilot, Codex, Cursor, plug-in and Antigravity adapters
    - `src/KyberWeave.Domain/Arbiter/Hooks/`: a prompt-insertion action that runs after the rules, and alone when the Arbiter is off
    - `src/KyberWeave.Domain/Arbiter/Rules/default-rules.yml`: one criteria line on `KW-ARB-PLANREAD-001`
    - `src/KyberWeave.Application/Hooks/`, `src/KyberWeave.Application/Doctor/`: install the hook when the Arbiter is off; report a harness where it is missing or not trusted
    - `tests/KyberWeave.Tests/DispatchPromptInsertionTests.cs` (new): `KyberWeave.Tests.DispatchPromptInsertionTests`
  - **Acceptance criteria:** For each rewrite harness, the hook's output carries the full prompt in the field that harness documents; a retry's failure section and every other line of the dispatch survive; the failed-attempt count and prior-results file named on the dispatch reach the prompt; a dispatch that names a technology and no task gets the shared rules and that technology's parts; a fresh host with the Arbiter off has the hook installed, and the doctor command reports a harness where it is not; with the Arbiter off the insertion still happens; with it on, the rules judge the short dispatch first; when the prompt cannot be built the dispatch is stopped with the repair command and no worker starts; an expanded prompt is not judged as telling a worker to read a plan.
  - **Capabilities:** C# work on coding-agent harness hook adapters, with xUnit tests over recorded hook payloads
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DispatchPromptInsertionTests"`
  - **Depends on:** 2.1, 2.7
  - _Requirements: 9.3, 9.4, 9.7, 9.8, 9.17, 9.18, 9.20_

- [ ] 2.10 Fall back to a prompt file
  - **Objective:** Where a harness's hook cannot rewrite the dispatch, write the prompt to one untracked file in the run's scratch folder and have the dispatch name it.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/TaskPromptService.cs`: write the prompt file under the run's scratch folder, one per task
    - `src/KyberWeave.Mcp/DocsTools.cs`: the file's path in the tool's reply on a fallback harness
    - `tests/KyberWeave.Tests/TaskPromptFileFallbackTests.cs` (new): `KyberWeave.Tests.TaskPromptFileFallbackTests`
  - **Acceptance criteria:** On a fallback harness the file holds the same text the service builds and the tool's reply gives its path; on a rewrite harness no file is written; the file is under an ignored folder.
  - **Capabilities:** C# file and MCP tool work, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TaskPromptFileFallbackTests"`
  - **Depends on:** 2.8, 2.9
  - _Requirements: 9.5_

- [ ] 2.11 Planners write difficulty and one technology
  - **Objective:** Tell the architect and the product-owner to write a `Difficulty:` and a `Technology:` line on every file-changing task, to split work that spans two unrelated technologies, and to fetch a standard through the docs tool only for a technology a task names.
  - **Files:**
    - `products/kyber-squad/agents/architect.md`, `products/kyber-squad/agents/architect/references/plan-authoring.md`
    - `products/kyber-squad/skills/product-owner/references/tasks-phase.md`
    - `tests/KyberWeave.PromptContent.Tests/PlannerDifficultyAndTechnologyContractTests.cs` (new): `KyberWeave.PromptContent.Tests.PlannerDifficultyAndTechnologyContractTests`
  - **Acceptance criteria:** Each file requires both lines, gives the three difficulty values with one line of guidance each, states the split rule with the endpoint-and-migration example, and forbids reading a standards file unless the docs tool is missing or fails.
  - **Capabilities:** Agent and skill instruction writing in Markdown, with xUnit prompt-content tests
  - **Command:** `dotnet test tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release --filter "FullyQualifiedName~PlannerDifficultyAndTechnologyContractTests"`
  - **Depends on:** 2.4
  - _Requirements: 2.2, 4.7, 4.8_

- [ ] 2.12 Document phase 2 and record the decisions
  - **Objective:** Write the decision records for the task fields and the task prompt, and for the YAML task list, and update the canonical documents.
  - **Files:**
    - `docs/adr/` (two new records), `docs/adr/README.md`
    - `docs/docgraph/governance.md`, `docs/docgraph/mcp-runbook.md`, `docs/kyber-squad/architecture.md`, `docs/configuration.md`
  - **Acceptance criteria:** The first record states difficulty, one technology per task and hook insertion with the file fallback, and what it changes in ADR 0080 and ADR 0082; the second states the YAML task list and what it changes in ADR 0063; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation: architecture decision records and governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 2.3, 2.5, 2.8, 2.10, 2.11
  - _Requirements: 11.2_

### Phase 3: Agents

- [ ] 3.1 Add the roster switch
  - **Objective:** Accept a `worker-roster` value of `technology` or `tiered` in the host configuration and on an agent, record it when the machine task list is written, and require a technology on file-changing tasks under `tiered`.
  - **Files:**
    - `src/KyberWeave.Domain/Configuration/KyberWeaveConfig.cs`: the key, default `technology`
    - `src/KyberWeave.Domain/Squad/Validation/SquadSourceValidator.cs`, `src/KyberWeave.Domain/Arbiter/Squad/ArbiterSquadCatalog.cs`: the optional agent key
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileGenerator.cs`, `src/KyberWeave.Application/Docs/Validation/PlanTaskFileValidator.cs`: record it; the tiered check
    - `src/KyberWeave.Domain/Docs/Scaffolding/ConfigRegMarkdown.cs`: the `<worker-roster>` property
    - `products/kyber-squad/agents/`: `worker-roster: technology` on the fourteen agents to be removed
    - `tests/KyberWeave.Tests/WorkerRosterTests.cs` (new): `KyberWeave.Tests.WorkerRosterTests`
  - **Acceptance criteria:** An agent with no key serves both rosters; the fourteen agents to be removed declare `technology` and are otherwise unchanged; no host key means `technology`; another value is a configuration error listing the two; the written task list records the roster and later readers take it from there; under `tiered` a task a developer agent will do with no technology is an error, and a documentation or delivery task needs none; under `technology` a run behaves as it does today.
  - **Capabilities:** C# configuration, squad source validation and documentation-engine work, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~WorkerRosterTests"`
  - **Depends on:** none
  - _Requirements: 4.4, 12.1, 12.2, 12.3, 12.6_

- [ ] 3.2 Add the `dev-hard` profile and drop the conductor's
  - **Objective:** Add a `dev-hard` model profile separate from the planning profiles, make an agent's model profile optional, and render the conductor with no model on every harness.
  - **Files:**
    - `products/kyber-squad/profiles/models.yml`: `dev-hard` added, `orchestration` removed
    - `products/kyber-squad/agents/conductor.md`: no `model-profile`
    - `src/KyberWeave.Domain/Squad/Validation/SquadSourceValidator.cs`: the key is optional
    - `src/KyberWeave.Domain/Squad/Rendering/`: an agent with no profile renders no model
    - `tests/KyberWeave.Tests/ModelProfileOptionalTests.cs` (new): `KyberWeave.Tests.ModelProfileOptionalTests`
  - **Acceptance criteria:** `dev-hard` has a row for every harness and shares no row with `architect` or `deep-planning`; the conductor's rendered file names no model on any of the thirteen harnesses; every other agent renders the model it renders today; no profile is renamed.
  - **Capabilities:** C# squad rendering and YAML profile editing, with xUnit tests over rendered agent files
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ModelProfileOptionalTests|FullyQualifiedName~SquadSourceTests"`
  - **Depends on:** none
  - _Requirements: 1.11, 1.12, 1.13_

- [ ] 3.3 Add the `platform-reader` capability profile
  - **Objective:** Add a capability profile that reads and runs commands and can neither write files, publish nor delegate.
  - **Files:**
    - `products/kyber-squad/profiles/capabilities.yml`: `platform-reader`
    - `tests/KyberWeave.Tests/PlatformReaderProfileTests.cs` (new): `KyberWeave.Tests.PlatformReaderProfileTests`
  - **Acceptance criteria:** The profile allows file read, search, network read and command execution, and denies file write, publishing and delegation; every harness renders it.
  - **Capabilities:** YAML capability profile editing, with C# xUnit tests over rendered permissions
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlatformReaderProfileTests"`
  - **Depends on:** none
  - _Requirements: 8.4_

- [ ] 3.4 Add the three developer agents
  - **Objective:** Add `dev-easy`, `dev-medium` and `dev-hard` with the instructions in the [design](design.md#developer-agent-instructions).
  - **Files:**
    - `products/kyber-squad/agents/dev-easy.md`, `dev-medium.md`, `dev-hard.md` (new)
    - `products/kyber-squad/bundles/`: the `full` bundle
    - `tests/KyberWeave.Tests/DeveloperAgentContentTests.cs` (new): `KyberWeave.Tests.DeveloperAgentContentTests`
  - **Acceptance criteria:** The three files differ only in name, description and model profile (`fast`, `general`, `dev-hard`); each uses the `worker` profile and today's worker tool list and carries `worker-roster: tiered`; no body names a technology, a library, a command of one language or a skill; no body tells the agent to fetch its standards at start; each body tells the agent what to do when its prompt holds only a task id; every agent that exists today is still present.
  - **Capabilities:** Agent instruction writing in Markdown, with C# xUnit content tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DeveloperAgentContentTests|FullyQualifiedName~SquadCanonicalContentTests|FullyQualifiedName~SquadPackAndReleaseTests"`
  - **Depends on:** 3.1, 3.2
  - _Requirements: 1.1, 1.2, 1.3, 9.14, 9.19_

- [ ] 3.5 Add `platform-researcher` and `devops`
  - **Objective:** Add a read-only research agent that takes its technology from the request, and a delivery agent that names no platform.
  - **Files:**
    - `products/kyber-squad/agents/platform-researcher.md`, `products/kyber-squad/agents/devops.md` (new)
    - `src/KyberWeave.Domain/RunStart/RunStartEvidenceRules.cs`, `src/KyberWeave.Domain/Arbiter/ArbiterRunStartRemedy.cs`, `src/KyberWeave.Domain/Arbiter/Hooks/RunWriterProfiles.cs`, `src/KyberWeave.Application/Docs/Mcp/RunDeliveryService.cs`, `src/KyberWeave.Application/Docs/Delivery/RunDelivery.cs`: accept `devops` wherever `github-devops` is named
    - `products/kyber-squad/bundles/`: the `full` bundle
    - `tests/KyberWeave.Tests/PlatformAgentContentTests.cs` (new): `KyberWeave.Tests.PlatformAgentContentTests`
  - **Acceptance criteria:** `platform-researcher` uses `platform-reader`, names no cloud or platform, and says to report and stop when its technology has no folder; `devops` uses `publishing-worker`, does what `github-devops` does today and names no platform; run start and delivery accept a dispatch to `devops` exactly as they accept one to `github-devops`; both carry `worker-roster: tiered`; `azure-reader` and `github-devops` are still present.
  - **Capabilities:** Agent instruction writing in Markdown for research and delivery roles, with C# xUnit content tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlatformAgentContentTests|FullyQualifiedName~SquadCanonicalContentTests"`
  - **Depends on:** 3.1, 3.3
  - _Requirements: 1.4, 1.10, 1.14, 8.1, 8.7_

- [ ] 3.6 Pick the worker by kind and tier, and step up on failure
  - **Objective:** Under the tiered roster, pick the kind of worker as today and the tier from difficulty plus failed attempts, and list the new agents wherever the old ones may be dispatched.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskAgentPick.cs`: candidates by roster
    - `src/KyberWeave.Application/Docs/Validation/TaskPromptService.cs`: `tier = min(hard, difficulty + failed attempts)`; a dispatch with no task starts at medium
    - `products/kyber-squad/agents/conductor.md`, `bug-crusher.md`, `architect.md`, `code-reviewer.md`: `delegates-to`
    - `tests/KyberWeave.Tests/DeveloperTierSelectionTests.cs` (new): `KyberWeave.Tests.DeveloperTierSelectionTests`
  - **Acceptance criteria:** Easy with no failures gives `dev-easy`; easy with one failure gives `dev-medium`; hard with any failures gives `dev-hard`; no input gives a lower tier after a failure; a dispatch with no task gives `dev-medium`, then `dev-hard`; a dispatch from each listed caller to its new agent is allowed by `KW-ARB-ROSTER-001`; under the `technology` roster the pick is unchanged.
  - **Capabilities:** C# work on agent selection in the documentation engine, agent frontmatter edits, and xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~DeveloperTierSelectionTests|FullyQualifiedName~PlanTaskAgentPickTests|FullyQualifiedName~ArbiterClassificationTests"`
  - **Depends on:** 3.4, 3.5, 2.8
  - _Requirements: 1.9, 3.1, 3.2, 3.3, 3.5, 3.6_

- [ ] 3.7 Teach the conductor, the architect and the bug flow
  - **Objective:** Tell the conductor to dispatch with a task id only and to move a failed task one tier up; give the architect schema and data-model planning; tell the bug flow to hand fixes to a developer agent.
  - **Files:**
    - `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/conductor/references/`, `products/kyber-squad/skills/delivery-execution/SKILL.md`
    - `products/kyber-squad/agents/architect.md`
    - `products/kyber-squad/skills/bug-crusher/SKILL.md`
    - `tests/KyberWeave.PromptContent.Tests/TieredRosterConductorContractTests.cs` (new): `KyberWeave.PromptContent.Tests.TieredRosterConductorContractTests`
  - **Acceptance criteria:** The conductor's text says a dispatch carries the task id and never the prompt, names the docs tool as the source of the agent, and states the one-tier step-up with the three-dispatch limit; the architect's text covers schema and data-model planning; the bug flow names the developer agents under the tiered roster; each rule is stated once.
  - **Capabilities:** Agent and skill instruction writing in Markdown, with xUnit prompt-content tests
  - **Command:** `dotnet test tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release --filter "FullyQualifiedName~TieredRosterConductorContractTests"`
  - **Depends on:** 3.6
  - _Requirements: 1.7, 3.4, 9.4_

- [ ] 3.8 Check every harness renders the new agents
  - **Objective:** Prove the thirteen renderers produce the five new agents and remove none, with no renderer code that names an agent.
  - **Files:**
    - `tests/KyberWeave.Tests/TieredAgentRenderTests.cs` (new): `KyberWeave.Tests.TieredAgentRenderTests`
  - **Acceptance criteria:** For each harness the rendered set contains the five new agents and every agent rendered before; no file under the rendering source names a developer agent.
  - **Capabilities:** C# xUnit tests over rendered agent output for many coding-agent harnesses
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TieredAgentRenderTests"`
  - **Depends on:** 3.4, 3.5
  - _Requirements: 1.8_

- [ ] 3.9 Document phase 3
  - **Objective:** Describe the developer tiers, the two new agents, the roster switch and the step-up in the canonical documents.
  - **Files:**
    - `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/configuration.md`, `products/kyber-squad/README.md`
  - **Acceptance criteria:** The documents list the agents of each roster, how to switch, and how a tier is chosen; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation: architecture and onboarding guides in governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 3.7, 3.8
  - _Requirements: 1.5_

### Phase 4: Arbiter rules from folders

- [ ] 4.1 Split the shipped rules into three role files
  - **Objective:** Move each of the 36 shipped rules into the Arbiter rules file of the `conductor`, `architect` or `reviewer` template, unchanged, and load the shipped set from those files.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/Rules/default-rules.yml`: split into three files
    - `src/KyberWeave.Domain/Arbiter/ArbiterConfigLoader.cs`, `src/KyberWeave.Domain/Areas/Arbiter.props`: load the three
    - `products/kyber-squad/standards/conductor/`, `architect/`, `reviewer/`: the role templates' rules files
    - `tests/KyberWeave.Tests/ArbiterRuleFolderSplitTests.cs` (new): `KyberWeave.Tests.ArbiterRuleFolderSplitTests`
  - **Acceptance criteria:** The loaded rule set equals the set before the split, rule for rule: same ids, questions, answers, effects and shadow flags; each rule is in exactly one file; the counts are 21, 2 and 13.
  - **Capabilities:** C# work on rule loading in the Arbiter, YAML editing, and xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterRuleFolderSplitTests|FullyQualifiedName~RuleValidatorTests"`
  - **Depends on:** 1.5
  - _Requirements: 7.1, 7.5_

- [ ] 4.2 Load host and technology rule files
  - **Objective:** Apply a host's role folder over the shipped one, add the rules of a task's technology and its parent, then apply the host's existing rule settings, and fall back to the shipped rules when a file is bad.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/ArbiterConfigLoader.cs`, `src/KyberWeave.Domain/Arbiter/Rules/RuleValidator.cs`
    - `src/KyberWeave.Application/Arbiter/Facts/TriggerFactBuilder.cs`: the task's technology
    - `tests/KyberWeave.Tests/ArbiterRuleFolderLoadingTests.cs` (new): `KyberWeave.Tests.ArbiterRuleFolderLoadingTests`
  - **Acceptance criteria:** A host declaration of a shipped id replaces it; a technology's rules apply only to events for a task in that technology or its child; the host's `arbiter.rules` settings still apply, last; a file that does not parse or names an unknown trigger or fact is an `arbiter validate` error naming the file and rule, and the shipped rules are used for that folder; every rule can still be turned down to log only through the switch it has today; with the Arbiter off nothing is loaded and a run works.
  - **Capabilities:** C# work on rule loading and validation in the Arbiter, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterRuleFolderLoadingTests"`
  - **Depends on:** 4.1, 2.2
  - _Requirements: 7.2, 7.3, 7.4, 7.6, 7.7, 7.9_

- [ ] 4.3 Exempt tests-only tasks by fact
  - **Objective:** Replace the `test-dev` clause of the test-first rule with a machine fact that is true when every file of the task is a test file.
  - **Files:**
    - `src/KyberWeave.Application/Arbiter/Facts/TriggerFactBuilder.cs`: `plan.task.files-all-tests`
    - the `conductor` rules file: the clause on `KW-ARB-MODE-002`
    - `tests/KyberWeave.Tests/ArbiterTestsOnlyTaskTests.cs` (new): `KyberWeave.Tests.ArbiterTestsOnlyTaskTests`
  - **Acceptance criteria:** A task whose files all match the host's test paths answers `not-required`; a task with one non-test file does not; a dispatch to `test-dev` under the old roster still answers `not-required`.
  - **Capabilities:** C# work on Arbiter facts and YAML rule editing, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterTestsOnlyTaskTests"`
  - **Depends on:** 4.1
  - _Requirements: 7.8_

- [ ] 4.4 Enforce a technology's allowed commands
  - **Objective:** Let a technology's Arbiter rules file declare the command forms a research agent may run, and refuse any other command where the Arbiter sees a sub-agent's own tool calls.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/Hooks/WorkerGuardRules.cs`: raise a `worker.command` trigger
    - `src/KyberWeave.Domain/Arbiter/Rules/RuleValidator.cs`: the trigger and its facts
    - `products/kyber-squad/standards/azure/`, `aws/`: allowed read forms
    - `tests/KyberWeave.Tests/ArbiterCommandAllowListTests.cs` (new): `KyberWeave.Tests.ArbiterCommandAllowListTests`
    - `docs/kyber-arbiter/architecture.md`: where it is enforced and where it is an instruction
  - **Acceptance criteria:** With Azure's list, `az vm list` is allowed and `az vm delete` is refused on Claude and Copilot CLI; a technology with no list refuses nothing; on a harness with no worker guard nothing is refused and the list appears in the prompt's rules part.
  - **Capabilities:** C# work on Arbiter hooks and rule validation, YAML rule writing for cloud command-line tools, and xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterCommandAllowListTests"`
  - **Depends on:** 4.2, 3.5
  - _Requirements: 8.5, 8.6_

- [ ] 4.5 Make the model rules follow the tiers
  - **Objective:** Set the ladder to `dev-easy`, `dev-medium`, `dev-hard`, and have the model-targeting step take the recorded difficulty instead of asking again.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/Rules/ModelTargetingRules.cs`
    - `tests/KyberWeave.Tests/ModelTargetingTieredTests.cs` (new): `KyberWeave.Tests.ModelTargetingTieredTests`
  - **Acceptance criteria:** Under the tiered roster no second difficulty call is made for a task that has a recorded rating; a dispatch already on the next tier is left as it is; under the `technology` roster the two model rules behave as today.
  - **Capabilities:** C# work on Arbiter model-targeting rules, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ModelTargetingTieredTests"`
  - **Depends on:** 4.1, 3.6
  - _Requirements: 2.10, 3.4_

- [ ] 4.6 Document phase 4 and record the decision
  - **Objective:** Write the decision record for rules by folder and update the Arbiter documents.
  - **Files:**
    - `docs/adr/` (new record), `docs/adr/README.md`
    - `docs/kyber-arbiter/architecture.md`, `docs/kyber-arbiter/runbook.md`
  - **Acceptance criteria:** The record states the three role files, the loading order, the override rule and that no rule is locked; the runbook says how a host overrides one rule in a folder; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation: an architecture decision record and governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 4.2, 4.3, 4.4, 4.5
  - _Requirements: 7.6_

### Phase 5: Comparison and removal

- [ ] 5.1 Write the comparison runbook
  - **Objective:** Define how to run one approved plan once per roster from one base commit on one harness, and what to record.
  - **Files:**
    - `docs/kyber-squad/roster-comparison-runbook.md` (new), `docs/kyber-squad/README.md`
  - **Acceptance criteria:** The runbook names the three kinds of plan to run, the fixed inputs, and for each measure (tasks completed, attempts and tier per task, task-gate failures, review findings by severity, tokens, wall time) where the number is read from; a measure with no source is marked as such.
  - **Capabilities:** Documentation: a measurement runbook in governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** none
  - _Requirements: 12.4, 12.5_

- [ ] 5.2 Run the comparison and record the owner's decision
  - **Objective:** Run the runbook, write the results, and record the owner's decision on removing the old agents as a decision record.
  - **Files:**
    - `docs/kyber-squad/roster-comparison-results.md` (new)
    - `docs/adr/` (new record), `docs/adr/README.md`
  - **Acceptance criteria:** Every measure of the runbook has a value per run; the record states remove, keep, or keep for named technologies, and carries the owner's approval; tasks 5.3 to 5.6 do not start without it.
  - **Capabilities:** Running delivery plans on a coding-agent harness, collecting measurements, and writing a decision record
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 5.1
  - _Requirements: 13.1_

- [ ] 5.3 Migrate agent-keyed task gates
  - **Objective:** Add `kyber-weave review migrate-task-gates`, which rewrites a host's agent-keyed task gates as technology-keyed gates, and warn about entries for removed agents.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Review/ReviewMigrateTaskGatesCommand.cs` (new), `src/KyberWeave.Cli/CliApplication.cs`
    - `src/KyberWeave.Domain/Configuration/ReviewConfigParser.cs`: the warning
    - `tests/KyberWeave.Tests/ReviewMigrateTaskGatesTests.cs` (new): `KyberWeave.Tests.ReviewMigrateTaskGatesTests`
    - `docs/cli-command-surface.md`
  - **Acceptance criteria:** The command prints the change before writing and changes nothing else in the file; entries that are equal collapse into one technology; an entry for a removed agent still resolves and raises a warning that names the command.
  - **Capabilities:** C# command-line work with YAML rewriting, and xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ReviewMigrateTaskGatesTests"`
  - **Depends on:** 5.2
  - _Requirements: 13.2, 13.3, 13.4_

- [ ] 5.4 Resolve removed agent names and clean rendered files
  - **Objective:** Resolve a removed agent named in a plan or machine task list to a developer agent by difficulty, and make a Squad update remove a removed agent's rendered files only when the Squad wrote them unedited.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskAgentPick.cs`: the substitution and its report
    - `src/KyberWeave.Application/Squad/`: removal guarded by the lock
    - `tests/KyberWeave.Tests/RemovedAgentResolutionTests.cs` (new): `KyberWeave.Tests.RemovedAgentResolutionTests`
  - **Acceptance criteria:** A task that names `csharp-dev` and is rated hard resolves to `dev-hard` and reports the substitution; a rendered file the host edited is kept and reported; an unedited one the lock records is removed.
  - **Capabilities:** C# work on agent selection and squad update, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~RemovedAgentResolutionTests"`
  - **Depends on:** 5.2
  - _Requirements: 13.5, 13.6_

- [ ] 5.5 Remove the old agents
  - **Objective:** Delete the ten technology agents, `sql-database-architect`, `test-dev`, `azure-reader` and `github-devops`, with the `test` technology, the `testing` skill and the `sql` template, and make `tiered` the default roster.
  - **Files:**
    - `products/kyber-squad/agents/`: the fourteen files
    - `products/kyber-squad/skills/`, `products/kyber-squad/standards/`, `products/kyber-squad/bundles/`
    - `src/KyberWeave.Domain/Configuration/KyberWeaveConfig.cs`: the default, and `technology` as an error that names the change
    - `products/kyber-squad/agents/conductor.md`, `bug-crusher.md`, `architect.md`, `code-reviewer.md`: `delegates-to`
    - the tests that name a removed agent where the name must be a shipped agent
  - **Acceptance criteria:** The Squad source holds fifteen agents; no shipped file names a removed agent except the migration map and its tests; `worker-roster: technology` is an error naming the change; the targeted classes pass.
  - **Capabilities:** Squad source maintenance across agents, skills and standards, C# configuration work, and xUnit test updates
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadCanonicalContentTests|FullyQualifiedName~SquadSourceTests|FullyQualifiedName~SquadPackAndReleaseTests|FullyQualifiedName~WorkerRosterTests"`
  - **Depends on:** 5.3, 5.4
  - _Requirements: 1.6, 13.7_

- [ ] 5.6 Sweep the documents after removal
  - **Objective:** Remove or rewrite every live document that names a removed agent.
  - **Files:**
    - the live documents under `docs/` and `products/kyber-squad/` that name a removed agent
  - **Acceptance criteria:** No live document outside `docs/archive` names a removed agent except where it describes the migration; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation maintenance across governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 5.5
  - _Requirements: 1.6_

### Closeout

- [ ] 6.1 Closeout
  - **Objective:** Verify each requirement against the delivered code and review evidence, harvest the [Design harvest](design.md#design-harvest) into canonical documents, update the specification index, and archive this specification.
  - **Files:** the canonical documents named by the Design harvest, `docs/specs/README.md`, this folder (moved to `docs/archive/specs/`)
  - **Acceptance criteria:** Every decision and harvest item maps to a live document; `docs validate . --merge-ready` and `docs drift .` report zero findings after the move.
  - **Capabilities:** Documentation closeout: harvesting a specification into canonical documents and archiving it
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready`
  - **Depends on:** 5.6
  - _Requirements: 1.5, 13.1_

## Test contract

| Task | Test project or file | Runner | Observable behaviour | Failing run's expected reason | Passing acceptance |
|---|---|---|---|---|---|
| 1.1 | `tests/KyberWeave.Tests`: `StandardsFolderShapeTests` | task's Command | Incomplete folder or standard is reported | The checks do not exist | New class passes |
| 1.2 | `tests/KyberWeave.Tests`: `KyberStandardsTemplateShapeTests` | task's Command | Every shipped template is complete | Templates hold one file and other headings | New class passes |
| 1.3 | none: documentation | docs gates | Inventory exists and is complete | not applicable | Zero findings |
| 1.4 | `tests/KyberWeave.Tests`: `KyberStandardsTemplateShapeTests` | task's Command | Two new templates are complete | The folders do not exist | Class passes |
| 1.5 | `tests/KyberWeave.Tests`: `KyberStandardsTemplateShapeTests` | task's Command | Eight role and platform templates are complete | The folders do not exist | Class passes |
| 1.6 | `tests/KyberWeave.Tests`: `StandardsBuildsOnTests` | task's Command | A parent is accepted or rejected | The key is unknown | New class passes |
| 1.7 | `tests/KyberWeave.Tests`: `DocsInitTemplateChoiceTests`, `DocsScaffolderTests` | task's Command | Only chosen templates install; existing files survive | Install is all or nothing | New class passes; scaffolder tests stay green |
| 1.8 | `tests/KyberWeave.Tests`: `DocsStandardsDiffCommandTests` | task's Command | Differences are printed, nothing is written | The command does not exist | New class passes |
| 1.9 | none: documentation | docs gates | Guides exist | not applicable | Zero findings |
| 1.10 | `tests/KyberWeave.Tests`: `DocsStandardsMigrateCommandTests` | task's Command | Files are renamed and added, content unchanged | The command does not exist | New class passes |
| 2.1 | none: research and documentation | docs gates | Per-harness delivery path recorded | not applicable | Zero findings |
| 2.2 | `tests/KyberWeave.Tests`: `PlanTaskDifficultyAndTechnologyTests` | task's Command | Both lines reach the entry | The fields do not exist | New class passes |
| 2.3 | `tests/KyberWeave.Tests`: `PlanTaskFileYamlTests`, `PlanTaskFileGeneratorReadTests` | task's Command | YAML is written, JSON is read with a warning | Only JSON is written | New class passes; read tests stay green |
| 2.4 | `tests/KyberWeave.Tests`: `PlanTaskFileValidatorTests` | task's Command | Three errors are raised | The checks do not exist | New cases pass |
| 2.5 | `tests/KyberWeave.Tests`: `PlanTaskDifficultyRatingTests`, `PlanTaskAgentPickTests` | task's Command | The model's rating and its source are recorded | The question is not asked | New class passes; pick tests stay green |
| 2.6 | `tests/KyberWeave.Tests`: `TaskGateResolverTests`, `ReviewTaskGatesConfigTests`, `PacketHostFactsTests`, `GateCatalogueRenderTests` | task's Command | The five resolution outcomes | `TaskGateResolver` does not exist | New class passes; the others stay green |
| 2.7 | `tests/KyberWeave.Tests`: `TaskPromptServiceTests`, `TaskPromptBudgetTests` | task's Command | Order, tags, cuts and the budget over every template | The service does not exist | Both classes pass |
| 2.8 | `tests/KyberWeave.Tests`: `DocsTaskPromptToolTests`, `DocsPacketCommandTests` | task's Command | Same text from the command; a short reply from the tool | The tool does not exist | New class passes; packet tests stay green |
| 2.9 | `tests/KyberWeave.Tests`: `DispatchPromptInsertionTests` | task's Command | The hook output carries the full prompt per harness | The adapters only strip headers | New class passes |
| 2.10 | `tests/KyberWeave.Tests`: `TaskPromptFileFallbackTests` | task's Command | A file on a fallback harness, none elsewhere | No file is written | New class passes |
| 2.11 | `tests/KyberWeave.PromptContent.Tests`: `PlannerDifficultyAndTechnologyContractTests` | task's Command | Planner instructions carry the rules | The sentences are absent | New class passes |
| 2.12 | none: documentation | docs gates | Two decision records and documents | not applicable | Zero findings |
| 3.1 | `tests/KyberWeave.Tests`: `WorkerRosterTests` | task's Command | The key loads, defaults, records and gates the technology check | The key is unknown | New class passes |
| 3.2 | `tests/KyberWeave.Tests`: `ModelProfileOptionalTests`, `SquadSourceTests` | task's Command | `dev-hard` exists; the conductor renders no model | The profile is required and absent | New class passes |
| 3.3 | `tests/KyberWeave.Tests`: `PlatformReaderProfileTests` | task's Command | The profile's permissions | The profile does not exist | New class passes |
| 3.4 | `tests/KyberWeave.Tests`: `DeveloperAgentContentTests`, `SquadCanonicalContentTests`, `SquadPackAndReleaseTests` | task's Command | Three developer agents with the stated content | The files do not exist | All three classes pass |
| 3.5 | `tests/KyberWeave.Tests`: `PlatformAgentContentTests`, `SquadCanonicalContentTests` | task's Command | Two agents with the stated content | The files do not exist | Both classes pass |
| 3.6 | `tests/KyberWeave.Tests`: `DeveloperTierSelectionTests`, `PlanTaskAgentPickTests`, `ArbiterClassificationTests` | task's Command | The tier for each difficulty and failure count | No tier is computed | New class passes; the others stay green |
| 3.7 | `tests/KyberWeave.PromptContent.Tests`: `TieredRosterConductorContractTests` | task's Command | Conductor, architect and bug-flow rules | The sentences are absent | New class passes |
| 3.8 | `tests/KyberWeave.Tests`: `TieredAgentRenderTests` | task's Command | Thirteen harnesses render five more agents | No failing run: a characterization test over agents 3.4 and 3.5 added; it fails only if a renderer drops one | Class passes on all thirteen |
| 3.9 | none: documentation | docs gates | Documents updated | not applicable | Zero findings |
| 4.1 | `tests/KyberWeave.Tests`: `ArbiterRuleFolderSplitTests`, `RuleValidatorTests` | task's Command | The split set equals the old set | The three files do not exist | New class passes; validator tests stay green |
| 4.2 | `tests/KyberWeave.Tests`: `ArbiterRuleFolderLoadingTests` | task's Command | Override, technology rules, fallback | Host folders are not read | New class passes |
| 4.3 | `tests/KyberWeave.Tests`: `ArbiterTestsOnlyTaskTests` | task's Command | A tests-only task is exempt | The fact does not exist | New class passes |
| 4.4 | `tests/KyberWeave.Tests`: `ArbiterCommandAllowListTests` | task's Command | A command outside the list is refused | The trigger does not exist | New class passes |
| 4.5 | `tests/KyberWeave.Tests`: `ModelTargetingTieredTests` | task's Command | One rating per task; no double step | A second call is made | New class passes |
| 4.6 | none: documentation | docs gates | Decision record and documents | not applicable | Zero findings |
| 5.1, 5.2, 5.6 | none: documentation and measured runs | docs gates | Runbook, results, decision record, swept documents | not applicable | Zero findings; owner approval of the record |
| 5.3 | `tests/KyberWeave.Tests`: `ReviewMigrateTaskGatesTests` | task's Command | Gates are rewritten by technology | The command does not exist | New class passes |
| 5.4 | `tests/KyberWeave.Tests`: `RemovedAgentResolutionTests` | task's Command | A removed name resolves; edited files survive | No substitution exists | New class passes |
| 5.5 | `tests/KyberWeave.Tests`: `SquadCanonicalContentTests`, `SquadSourceTests`, `SquadPackAndReleaseTests`, `WorkerRosterTests` | task's Command | Fifteen agents; `tiered` is the default | The classes still expect the old agents | All four classes pass |
| 6.1 | none: closeout | docs gates with `--merge-ready` | Specification archived, harvest complete | not applicable | Zero findings |

## Dependency graph

- Phase 1: 1.1 first; 1.2, 1.6 and 1.10 after it; 1.3, 1.5 and 1.8 after 1.2; 1.4 after 1.3; 1.7 after 1.2 and 1.5; 1.9 last.
- Phase 2: 2.1 and 2.2 in parallel; 2.3, 2.4 and 2.5 after 2.2; 2.6 after 2.2; 2.7 after 2.6; 2.8 after 2.7; 2.9 after 2.1 and 2.7; 2.10 after 2.8 and 2.9; 2.11 after 2.4; 2.12 last.
- Phase 3: 3.1, 3.2 and 3.3 in parallel; 3.4 after 3.1 and 3.2; 3.5 after 3.1 and 3.3; 3.6 after 3.4 and 3.5; 3.7 after 3.6; 3.8 after 3.4 and 3.5; 3.9 last.
- Phase 4: 4.1 first; 4.2, 4.3 and 4.5 after it; 4.4 after 4.2; 4.6 last.
- Phase 5: 5.1, then 5.2; 5.3 and 5.4 in parallel; 5.5 after both; 5.6 last.
- 6.1 after 5.6.

## Requirement coverage

| Requirement | Tasks |
|---|---|
| 1 | 3.2, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 5.5, 5.6 |
| 2 | 2.2, 2.4, 2.5, 2.11, 4.5 |
| 3 | 3.6, 3.7, 4.5 |
| 4 | 1.6, 2.2, 2.4, 2.11, 3.1 |
| 5 | 1.1, 1.2, 1.3, 1.4, 1.5, 1.10 |
| 6 | 1.2, 1.5, 1.7, 1.8, 1.9 |
| 7 | 4.1, 4.2, 4.3, 4.6 |
| 8 | 1.4, 3.3, 3.5, 4.4 |
| 9 | 2.1, 2.7, 2.8, 2.9, 2.10, 3.4, 3.7 |
| 10 | 2.6 |
| 11 | 2.3, 2.12 |
| 12 | 3.1, 3.4, 3.5, 5.1 |
| 13 | 5.2, 5.3, 5.4, 5.5 |
