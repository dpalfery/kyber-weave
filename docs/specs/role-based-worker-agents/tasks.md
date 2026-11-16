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
  - role agents
  - worker roster
  - task technologies
  - test-first
---

# Implementation Tasks

**Phase status:** Draft

**Development mode:** test-first

**Autonomy:** autonomous

Written 2026-11-16 against the draft [requirements](requirements.md) and [design](design.md).
Nothing here is approved, and no task file (`tasks.json`) has been written: the owner asked for the
specification only. The approval steps of the conductor's spec path have not run.

These tasks deliver four phases. Each phase is its own pull request and leaves the product working
with the technology roster as the default, so any phase can ship without the next.

| Phase | Delivers | Tasks | Shippable because |
|---|---|---|---|
| 1. Task technologies | `Technologies:` on tasks, packet standards lines, just-in-time standards for the architect | 1.1 to 1.5 | Optional field; helps today's agents too |
| 2. Technology-resolved task gates | One gate resolver, `review.technology-gates`, legacy fallback | 2.1 to 2.4 | Agents with their own entry resolve exactly as before |
| 3. Role agents and the roster switch | Three agents, `worker-roster`, picker filter, Arbiter fact | 3.1 to 3.7 | Default roster stays `technology` |
| 4. Parity and comparison | Rule inventory, comparison runbook, the runs, the decision record | 4.1 to 4.4 | Documentation and measurements only |
| Closeout | Harvest and archive | 5.1 | After 4.4 |

Retiring the technology agents is not in these tasks. It waits for the decision record of task 4.4
and a follow-up specification ([design, Retirement](design.md#retirement-not-approved-here)).

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
- **Phase order.** Phase 2 starts after 1.5, phase 3 after 2.4, phase 4 after 3.7. Inside a phase,
  tasks with no dependency between them and no shared file may run in parallel.

## Tasks

### Phase 1: Task technologies

- [ ] 1.1 Parse and carry a task's technologies
  - **Objective:** Read a `Technologies:` line from a plan or specification task and write it to the task file as a list.
  - **Files:**
    - `src/KyberWeave.Domain/Arbiter/Plans/PlanDocumentParser.cs`: read the line into the parsed task
    - `src/KyberWeave.Domain/Docs/Validation/PlanTaskFile.cs`: `PlanTaskFileEntry` gains `technologies`; schema version plus one
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileGenerator.cs`: write the field; read the older version without it
    - `tests/KyberWeave.Tests/PlanTaskTechnologiesTests.cs` (new): `KyberWeave.Tests.PlanTaskTechnologiesTests`
  - **Acceptance criteria:** A task with `Technologies: csharp, data-access-layer` yields `["csharp", "data-access-layer"]` in the task file; a task without the line yields no field; an older-version task file still loads; the execution digest of a plan with no such line is unchanged.
  - **Capabilities:** C# parsing and JSON serialization in the documentation engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskTechnologiesTests|FullyQualifiedName~PlanTaskFileGeneratorReadTests"`
  - **Depends on:** none
  - _Requirements: 3.1, 3.2, 3.7_

- [ ] 1.2 Validate a task's technologies
  - **Objective:** Report an unknown technology as an error.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileValidator.cs`: the check, with a new `KW-DOC-TASKS-*` id
    - `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`: new cases
    - `docs/docgraph/governance.md`: the new id in the rule table
  - **Acceptance criteria:** An undeclared technology is an error naming the task, the value and `ontology.technologies`; a task with no technologies reports nothing.
  - **Capabilities:** C# validation rules in the documentation engine, with xUnit tests, and one governed Markdown table edit
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskFileValidatorTests"`
  - **Depends on:** 1.1
  - _Requirements: 3.3, 3.5_

- [ ] 1.3 List each technology's standards call in the packet
  - **Objective:** Make a printed packet name, for each of the task's technologies, the docs-tool call that returns its standards bundle.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PacketHostFacts.cs`: `Render` takes the technologies and appends one line per technology
    - `src/KyberWeave.Cli/Commands/Docs/DocsPacketCommand.cs`: pass the task's technologies
    - `tests/KyberWeave.Tests/PacketHostFactsTests.cs`: new cases
  - **Acceptance criteria:** A task naming `csharp` and `test` prints two lines, each with `docs_get_coding_standard_for_technology` and the technology; no standards text is inlined; a task with no technologies prints the section exactly as today.
  - **Capabilities:** C# text rendering in the documentation engine and CLI, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PacketHostFactsTests"`
  - **Depends on:** 1.1
  - _Requirements: 4.1, 4.2_

- [ ] 1.4 Planners write technologies and read standards just in time
  - **Objective:** Tell the architect and the product-owner to write a `Technologies:` line on every file-changing task, and to fetch a standard through the docs tool only for a technology a task names.
  - **Files:**
    - `products/kyber-squad/agents/architect.md`: replace "applicable standards" resolution with the docs-tool rule
    - `products/kyber-squad/agents/architect/references/plan-authoring.md`: the `Technologies:` line
    - `products/kyber-squad/skills/product-owner/references/tasks-phase.md`: the same two rules
    - `tests/KyberWeave.PromptContent.Tests/SquadStandardsToolContractTests.cs`: new cases
  - **Acceptance criteria:** Each file names `docs_get_coding_standard_for_technology`, forbids reading a standards file unless the tool is missing or fails, and requires the `Technologies:` line; no file names an agent or a skill for routing.
  - **Capabilities:** Agent and skill instruction writing in Markdown, with xUnit prompt-content tests
  - **Command:** `dotnet test tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release --filter "FullyQualifiedName~SquadStandardsToolContractTests"`
  - **Depends on:** 1.1
  - _Requirements: 5.1, 5.2, 5.3, 5.4_

- [ ] 1.5 Document phase 1 and record the decision
  - **Objective:** Write the decision record for task technologies and update the canonical documents.
  - **Files:**
    - `docs/adr/` (new record): task technologies, amending ADR 0080 and ADR 0082
    - `docs/adr/README.md`, `docs/docgraph/governance.md`, `docs/kyber-squad/architecture.md`
  - **Acceptance criteria:** The record states the field, its vocabulary and what it leaves unchanged in ADR 0080 and ADR 0082; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation: an architecture decision record and governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 1.2, 1.3, 1.4
  - _Requirements: 3.1, 5.1_

### Phase 2: Technology-resolved task gates

- [ ] 2.1 Accept technology-keyed task gates in host configuration
  - **Objective:** Parse `review.technology-gates`, a map of technology to gate list with the same gate shape as `review.task-gates`.
  - **Files:**
    - `src/KyberWeave.Domain/Configuration/ReviewYamlSection.cs`, `src/KyberWeave.Domain/Configuration/ReviewConfig.cs`, `src/KyberWeave.Domain/Configuration/ReviewConfigParser.cs`
    - `tests/KyberWeave.Tests/ReviewTechnologyGatesConfigTests.cs` (new): `KyberWeave.Tests.ReviewTechnologyGatesConfigTests`
  - **Acceptance criteria:** A valid block loads; a key not in `ontology.technologies` is refused naming the declared list; an empty list is refused; a configuration without the block loads as today.
  - **Capabilities:** C# YAML configuration parsing and validation, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ReviewTechnologyGatesConfigTests|FullyQualifiedName~ReviewTaskGatesConfigTests"`
  - **Depends on:** 1.5
  - _Requirements: 6.2_

- [ ] 2.2 Resolve task gates in one place
  - **Objective:** Add `TaskGateResolver` and the shared technology-to-agent map; resolve by own entry first, then by the task's technologies with the legacy fallback, merge and conflict rules.
  - **Files:**
    - `src/KyberWeave.Domain/Review/TaskGateResolver.cs` (new)
    - `src/KyberWeave.Domain/Review/Catalogue/GateCatalogueRenderer.cs`: read the shared map instead of its private `AgentTechnology`
    - `tests/KyberWeave.Tests/TaskGateResolverTests.cs` (new): `KyberWeave.Tests.TaskGateResolverTests`
  - **Acceptance criteria:** An agent with its own entry gets it unchanged; an agent without one gets the technology-keyed gates, else the mapped agent's; same id, command and globs runs once; same id with a different command runs both, reported as `<technology>:<id>`; nothing resolved with technologies named is an error with a new `KW-REVIEW-*` id; `GateCatalogueRenderTests` and `GateCatalogueToolchainTests` stay green.
  - **Capabilities:** C# domain logic in the review engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TaskGateResolverTests|FullyQualifiedName~GateCatalogueRenderTests|FullyQualifiedName~GateCatalogueToolchainTests"`
  - **Depends on:** 2.1
  - _Requirements: 6.1, 6.3, 6.4, 6.5, 6.6, 6.7_

- [ ] 2.3 Use the resolver at every lookup
  - **Objective:** Replace the four direct `TaskGates` lookups with the resolver, passing the task's technologies, and warn when a task's files belong to a declared technology it does not name.
  - **Files:**
    - `src/KyberWeave.Application/Review/ReviewGatesService.cs`, `src/KyberWeave.Application/Review/PacketTestCommandRenderer.cs`, `src/KyberWeave.Application/Docs/Validation/PacketHostFacts.cs`, `src/KyberWeave.Application/Arbiter/Facts/TriggerFactBuilder.cs`
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskFileValidator.cs`: the unnamed-technology warning, with a new `KW-DOC-TASKS-*` id
    - `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`: new cases
    - `tests/KyberWeave.Tests/ReviewGatesTechnologyResolutionTests.cs` (new): `KyberWeave.Tests.ReviewGatesTechnologyResolutionTests`
  - **Acceptance criteria:** A gate run for an agent with no entry and a task naming `csharp` runs the `csharp-dev` gates of an unedited host configuration; the packet lists the same gates; `config.task-gates-declared` is true for it; a `.sh` file in a task that names only `csharp` is a warning naming the file and `github-actions`, while a `.cs` file in a task that names `csharp` reports nothing whatever other technologies select `.cs`; every existing case in `ReviewGatesAgentCommandTests`, `PacketHostFactsTests` and `PacketTestCommandTests` passes unchanged.
  - **Capabilities:** C# application services in the review engine and Arbiter facts, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ReviewGatesTechnologyResolutionTests|FullyQualifiedName~ReviewGatesAgentCommandTests|FullyQualifiedName~PacketHostFactsTests|FullyQualifiedName~PacketTestCommandTests|FullyQualifiedName~PlanTaskFileValidatorTests"`
  - **Depends on:** 2.2
  - _Requirements: 3.6, 6.1, 6.7, 6.8, 6.9_

- [ ] 2.4 Document phase 2
  - **Objective:** Describe technology-resolved task gates and the new configuration key.
  - **Files:** `docs/code-review/architecture.md`, `docs/configuration.md`, `docs/ci-pipelines/rule-reference.md`, and the decision record from 1.5
  - **Acceptance criteria:** The resolution order, the fallback map and the new rule id are documented once each; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** Documentation: governed Markdown for architecture and configuration reference
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs drift .`
  - **Depends on:** 2.3
  - _Requirements: 6.2, 6.3_

### Phase 3: Role agents and the roster switch

- [ ] 3.1 Let an agent declare its roster
  - **Objective:** Accept the optional `worker-roster` frontmatter key and expose it on the loaded agent and the embedded Arbiter catalogue.
  - **Files:**
    - `src/KyberWeave.Data/Squad/Parsing/SquadSourceLoader.cs`, `src/KyberWeave.Domain/Squad/Validation/SquadSourceValidator.cs`, `src/KyberWeave.Domain/Arbiter/Squad/ArbiterSquadCatalog.cs`
    - `products/kyber-squad/schemas/` (the agent schema file)
    - `tests/KyberWeave.Tests/SquadWorkerRosterTests.cs` (new): `KyberWeave.Tests.SquadWorkerRosterTests`
  - **Acceptance criteria:** `technology` and `role` load; any other value is refused; an absent key means both rosters; no renderer writes the key into rendered frontmatter.
  - **Capabilities:** C# parsing and validation in the Squad deployment engine, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadWorkerRosterTests|FullyQualifiedName~SquadSourceTests"`
  - **Depends on:** 2.4
  - _Requirements: 2.4, 8.1_

- [ ] 3.2 Choose the roster per host and per run
  - **Objective:** Add the `worker-roster` host key, its Config Reg property and the roster option on the commands that write a task file.
  - **Files:**
    - `src/KyberWeave.Domain/Configuration/KyberWeaveConfig.cs`, `src/KyberWeave.Domain/Configuration/KyberWeaveYamlDocument.cs`, `src/KyberWeave.Domain/Configuration/ConfigRegConfig.cs`
    - `src/KyberWeave.Cli/Commands/Docs/DocsFinalizeSettings.cs`, `src/KyberWeave.Cli/Commands/Docs/DocsTasksSettings.cs`, `src/KyberWeave.Mcp/DocsTools.PlanLifecycle.cs`: the roster option, passed to the task-file writer
    - `tests/KyberWeave.Tests/WorkerRosterConfigTests.cs` (new): `KyberWeave.Tests.WorkerRosterConfigTests`
  - **Acceptance criteria:** No key means `technology`; an illegal value is a load error naming both legal values; the Config Reg block lists `<worker-roster>`; the option overrides the host value for that task file only, and the command's report prints the roster.
  - **Capabilities:** C# configuration and CLI option handling, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~WorkerRosterConfigTests|FullyQualifiedName~ConfigRegTests|FullyQualifiedName~DocsTasksCommandTests|FullyQualifiedName~PlanFinalizeTests"`
  - **Depends on:** 2.4
  - _Requirements: 2.1, 2.2, 2.3_

- [ ] 3.3 Add the three role agents
  - **Objective:** Write `server-dev`, `client-dev` and `devops-dev`, mark the ten technology agents other than `sql-database-architect` with their roster, and add the role agents to the bundle and the conductor's roster.
  - **Files:**
    - `products/kyber-squad/agents/server-dev.md`, `client-dev.md`, `devops-dev.md` (new)
    - the ten technology agent files (`csharp-dev`, `dal-dev`, `go-dev`, `maui-dev`, `pulumi-dev`, `python-dev`, `react-dev`, `rust-dev`, `tauri-dev`, `typescript-dev`): one added `worker-roster: technology` line each; `sql-database-architect` gets none
    - `products/kyber-squad/bundles/full.yml`, `products/kyber-squad/agents/conductor.md` (`delegates-to`)
    - `tests/KyberWeave.Tests/SquadRoleAgentContentTests.cs` (new): `KyberWeave.Tests.SquadRoleAgentContentTests`
    - `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs`, `SquadSourceTests.cs`, `SquadPackAndReleaseTests.cs`: roster counts and lists
  - **Acceptance criteria:** Each role agent uses the `worker` profile, the `general` model profile and the technology agents' exact Copilot tool line; its body names no technology rule, command or library, the interim technology-to-skill table being the one exception; its description says it is used only under roster `role`; its workflow fetches the bundle for every technology the packet lists before the first edit and holds the interim technology-to-skill table; every existing agent still loads with an unchanged name.
  - **Capabilities:** Agent instruction writing in Markdown and YAML frontmatter, with xUnit content tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadRoleAgentContentTests|FullyQualifiedName~SquadCanonicalContentTests|FullyQualifiedName~SquadSourceTests|FullyQualifiedName~SquadPackAndReleaseTests"`
  - **Depends on:** 3.1
  - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 4.3, 4.5, 4.6, 7.2, 8.3, 12.1, 12.3_

- [ ] 3.4 Pick agents from the active roster only
  - **Objective:** Filter the decision model's candidates by roster, record the roster in the task file, and apply the role-roster validation.
  - **Files:**
    - `src/KyberWeave.Application/Docs/Validation/PlanTaskAgentPick.cs`, `PlanTaskFileGenerator.cs`, `PlanTaskFileValidator.cs`
    - `src/KyberWeave.Domain/Docs/Validation/PlanTaskFile.cs`, `PlanTaskPacket.cs` (the ready queue names the roster)
    - `tests/KyberWeave.Tests/PlanTaskAgentPickTests.cs`, `PlanTaskFileValidatorTests.cs`: new cases
  - **Acceptance criteria:** Under `role`, candidates are the three role agents plus agents with no roster key; under `technology`, the role agents are absent; the task file records the roster given by the option, else the host's; under `role`, a file-changing task with no technologies is an error; a `Role:` line naming the other roster is kept with a warning.
  - **Capabilities:** C# application logic in the documentation engine around the decision-model pick, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlanTaskAgentPickTests|FullyQualifiedName~PlanTaskFileValidatorTests"`
  - **Depends on:** 3.1, 3.2
  - _Requirements: 2.4, 2.6, 2.7, 3.4_

- [ ] 3.5 Tell the conductor which roster to dispatch
  - **Objective:** Make the conductor and the delivery skill dispatch only agents of the roster the ready queue names.
  - **Files:**
    - `products/kyber-squad/agents/conductor.md`, `products/kyber-squad/skills/delivery-execution/SKILL.md`
    - `tests/KyberWeave.PromptContent.Tests/ArbiterConductorContractTests.cs`: new cases
  - **Acceptance criteria:** Both files state the rule once and name `<worker-roster>`; neither names a technology agent for routing.
  - **Capabilities:** Agent and skill instruction writing in Markdown, with xUnit prompt-content tests
  - **Command:** `dotnet test tests/KyberWeave.PromptContent.Tests/KyberWeave.PromptContent.Tests.csproj -c Release --filter "FullyQualifiedName~ArbiterConductorContractTests"`
  - **Depends on:** 3.3, 3.4
  - _Requirements: 2.5_

- [ ] 3.6 Record dispatches to the inactive roster
  - **Objective:** Add the `delegation.target-roster-active` fact and one allow-and-log rule.
  - **Files:**
    - `src/KyberWeave.Application/Arbiter/Facts/TriggerFactBuilder.cs`, `src/KyberWeave.Domain/Arbiter/ArbiterTriggerCatalog.cs`, `src/KyberWeave.Domain/Arbiter/Rules/default-rules.yml`
    - `tests/KyberWeave.Tests/Arbiter/ArbiterWorkerRosterTests.cs` (new): `KyberWeave.Tests.Arbiter.ArbiterWorkerRosterTests`
  - **Acceptance criteria:** The fact takes the roster from the `worker-roster` field of the run's `tasks.json` or `<plan>.tasks.json`, else from the host configuration; it is true when the dispatched agent belongs to that roster or declares none, false when it belongs to the other, and absent when the target is not a Squad agent; the rule has a new id, every answer allows, and the decision is logged; role agents are classified `implementation` with profile `worker`; no existing rule id or rule body changes.
  - **Capabilities:** C# Arbiter facts and YAML rule authoring, with xUnit tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ArbiterWorkerRosterTests|FullyQualifiedName~ArbiterClassificationTests"`
  - **Depends on:** 3.1, 3.2
  - _Requirements: 7.1, 7.3, 7.4, 7.5_

- [ ] 3.7 Prove the render on every harness and document phase 3
  - **Objective:** Pin the role agents' rendered output for all thirteen harnesses and update the Squad documents.
  - **Files:**
    - `tests/KyberWeave.Tests/SquadRoleAgentRenderTests.cs` (new): `KyberWeave.Tests.SquadRoleAgentRenderTests`
    - `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/kyber-squad/model-profiles.md`, `docs/kyber-arbiter/architecture.md`, `docs/configuration.md`
  - **Acceptance criteria:** Each renderer writes three more agents and removes none; a Squad update over an existing deployment adds files only; no renderer source names an agent; the documents state how to switch roster and what changes model tier; `docs validate .` and `docs drift .` report zero findings.
  - **Capabilities:** C# render tests across harness renderers with xUnit, and governed Markdown documentation
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadRoleAgentRenderTests"`
  - **Depends on:** 3.3, 3.5, 3.6
  - _Requirements: 8.1, 8.2, 12.1_

### Phase 4: Parity and comparison

- [ ] 4.1 Inventory the rules that live only in technology agents
  - **Objective:** List every technology-specific rule in the eleven technology agents' bodies and map each to the standards file that states it, or mark it missing.
  - **Files:** `docs/kyber-squad/worker-rule-map.md`
  - **Acceptance criteria:** Every rule of `tauri-dev`, `sql-database-architect` and `csharp-dev`'s completion gate has a row; each row names a standards file and section, or `missing`; rules that hold for every technology are marked for the shared baseline.
  - **Capabilities:** Documentation: careful reading of agent instructions and coding standards, written as a governed Markdown table
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 3.7
  - _Requirements: 9.1_

- [ ] 4.2 Give every missing rule a home
  - **Objective:** Add each rule marked missing in 4.1 to its shipped standard (or its technology rules file, if the plan on pull request 625 has shipped), move the every-technology rules to the shared worker baseline, and remove the interim skill table if the bundle is live.
  - **Files:**
    - `products/kyber-squad/standards/<technology>/README.md` for each technology with a missing rule
    - `products/kyber-squad/shared/worker-baseline.md`
    - `docs/kyber-squad/onboarding.md`: how an existing host learns of rules added to shipped standards
    - `tests/KyberWeave.Tests/SquadRoleAgentContentTests.cs`: a case per moved baseline rule
  - **Acceptance criteria:** No row of the inventory is `missing`; each moved rule is stated once; the technology agents' bodies are not edited; the onboarding document tells an existing host how to compare its standards with the shipped ones.
  - **Capabilities:** Coding-standard and agent-baseline authoring in Markdown, with xUnit content tests
  - **Command:** `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SquadRoleAgentContentTests|FullyQualifiedName~SquadCanonicalContentTests"`
  - **Depends on:** 4.1
  - _Requirements: 4.4, 9.2, 9.3, 9.4_

- [ ] 4.3 Write the comparison runbook
  - **Objective:** Define the comparison: three plans, one run per roster, one base commit, one harness, a preset that gives every worker the same model, and the measurements to record.
  - **Files:** `docs/kyber-squad/roster-comparison-runbook.md` (new), `docs/kyber-squad/README.md`
  - **Acceptance criteria:** The runbook names the three plans, the exact commands to start each run, where each measurement of Requirement 10.2 is read from, and a results table to fill in.
  - **Capabilities:** Documentation: an operational runbook in governed Markdown
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 3.7
  - _Requirements: 10.1, 10.2, 10.3, 12.2_

- [ ] 4.4 Run the comparison and record the decision
  - **Objective:** Run the six runs of the runbook, fill in the results, and write the decision record on retiring, keeping, or partly keeping the technology agents.
  - **Files:** `docs/kyber-squad/roster-comparison-runbook.md` (results), `docs/adr/` (new record), `docs/adr/README.md`
  - **Acceptance criteria:** Six runs are recorded with every measurement or a stated reason one is missing; the record answers, from the results, whether the server and client split is kept or replaced by one developer agent, and whether `sql-database-architect` folds into `server-dev`; the owner has approved the record.
  - **Capabilities:** Running end-to-end Squad delivery runs, reading run evidence, and writing an architecture decision record
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - **Depends on:** 4.2, 4.3
  - _Requirements: 10.3, 10.4, 11.1, 11.2, 11.3, 11.4, 11.5, 11.6_

### Closeout

- [ ] 5.1 Documentation closeout
  - **Objective:** Verify each requirement against the delivered code and review evidence, harvest the [Design harvest](design.md#design-harvest) into canonical documents, update the specification index, and archive this specification.
  - **Files:** the canonical documents named by the Design harvest, `docs/specs/README.md`, this folder (moved to `docs/archive/specs/`)
  - **Acceptance criteria:** Every decision and harvest item maps to a live document; `docs validate . --merge-ready` and `docs drift .` report zero findings after the move.
  - **Capabilities:** Documentation closeout: harvesting a specification into canonical documents and archiving it
  - **Command:** `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready`
  - **Depends on:** 4.4
  - _Requirements: 1.2, 11.1_

## Test contract

| Task | Test project or file | Runner | Observable behaviour | Failing run's expected reason | Passing acceptance |
|---|---|---|---|---|---|
| 1.1 | `tests/KyberWeave.Tests`: `PlanTaskTechnologiesTests`, `PlanTaskFileGeneratorReadTests` | task's Command | A `Technologies:` line reaches the task file as a list | The field and the parser branch do not exist | New class passes; the read tests stay green |
| 1.2 | `tests/KyberWeave.Tests`: `PlanTaskFileValidatorTests` | task's Command | Unknown technology is an error | The check does not exist, so no diagnostic is raised | New cases pass; existing cases unchanged |
| 1.3 | `tests/KyberWeave.Tests`: `PacketHostFactsTests` | task's Command | One standards line per technology in the packet | `Render` has no technologies input | New cases pass; existing output unchanged |
| 1.4 | `tests/KyberWeave.PromptContent.Tests`: `SquadStandardsToolContractTests` | task's Command | Planner instructions carry the two rules | The sentences are absent | New cases pass |
| 1.5 | none: documentation | docs gates | Decision record and documents exist | not applicable | Zero findings |
| 2.1 | `tests/KyberWeave.Tests`: `ReviewTechnologyGatesConfigTests`, `ReviewTaskGatesConfigTests` | task's Command | `review.technology-gates` parses and is validated | The key is unknown to the parser | New class passes; task-gate tests stay green |
| 2.2 | `tests/KyberWeave.Tests`: `TaskGateResolverTests`, `GateCatalogueRenderTests`, `GateCatalogueToolchainTests` | task's Command | The five resolution outcomes of the design | `TaskGateResolver` does not exist | New class passes; catalogue tests stay green |
| 2.3 | `tests/KyberWeave.Tests`: `ReviewGatesTechnologyResolutionTests`, `ReviewGatesAgentCommandTests`, `PacketHostFactsTests`, `PacketTestCommandTests`, `PlanTaskFileValidatorTests` | task's Command | A role-style agent runs the mapped gates end to end; an unnamed technology is warned about | The gate run answers "no gates declared", and no warning is raised | New class passes; the three existing classes unchanged |
| 2.4 | none: documentation | docs gates | Resolution documented | not applicable | Zero findings |
| 3.1 | `tests/KyberWeave.Tests`: `SquadWorkerRosterTests`, `SquadSourceTests` | task's Command | The roster key loads and validates | The loader rejects or ignores the key | New class passes |
| 3.2 | `tests/KyberWeave.Tests`: `WorkerRosterConfigTests`, `ConfigRegTests`, `DocsTasksCommandTests`, `PlanFinalizeTests` | task's Command | Host key, registry property and the task-file roster option | The key and option do not exist | New class passes; existing classes stay green |
| 3.3 | `tests/KyberWeave.Tests`: `SquadRoleAgentContentTests`, `SquadCanonicalContentTests`, `SquadSourceTests`, `SquadPackAndReleaseTests` | task's Command | Three role agents with the stated profile, tools and body rules | The agent files do not exist | All four classes pass |
| 3.4 | `tests/KyberWeave.Tests`: `PlanTaskAgentPickTests`, `PlanTaskFileValidatorTests` | task's Command | Candidates follow the roster; the task file records it | Candidates are unfiltered | New cases pass |
| 3.5 | `tests/KyberWeave.PromptContent.Tests`: `ArbiterConductorContractTests` | task's Command | Conductor instructions carry the roster rule | The sentence is absent | New cases pass |
| 3.6 | `tests/KyberWeave.Tests`: `ArbiterWorkerRosterTests`, `ArbiterClassificationTests` | task's Command | The fact and its allow-and-log rule | The fact is unknown to the catalogue | New class passes; classification stays green |
| 3.7 | `tests/KyberWeave.Tests`: `SquadRoleAgentRenderTests` | task's Command | Thirteen harnesses render three more agents, remove none | No failing run: a characterization test over the agents 3.3 added; it fails only if a renderer drops or alters one | Class passes on all thirteen |
| 4.1, 4.3, 4.4 | none: documentation and measured runs | docs gates | Inventory, runbook, results, decision record | not applicable | Zero findings; owner approval of the record |
| 4.2 | `tests/KyberWeave.Tests`: `SquadRoleAgentContentTests`, `SquadCanonicalContentTests` | task's Command | Baseline rules present once | The rules are not in the baseline | Classes pass |
| 5.1 | none: closeout | docs gates with `--merge-ready` | Specification archived, harvest complete | not applicable | Zero findings |

## Dependency graph

- Phase 1: 1.1 first; 1.2, 1.3 and 1.4 in parallel; 1.5 last.
- Phase 2: 2.1, then 2.2, then 2.3, then 2.4.
- Phase 3: 3.1 and 3.2 in parallel; 3.3 after 3.1; 3.4 and 3.6 after 3.1 and 3.2; 3.5 after 3.3 and 3.4; 3.7 last.
- Phase 4: 4.1 and 4.3 in parallel; 4.2 after 4.1; 4.4 after 4.2 and 4.3.
- 5.1 after 4.4.

## Requirement coverage

| Requirement | Tasks |
|---|---|
| 1 | 3.3, 5.1 |
| 2 | 3.1, 3.2, 3.4, 3.5 |
| 3 | 1.1, 1.2, 2.3, 3.4 |
| 4 | 1.3, 3.3, 4.2 |
| 5 | 1.4, 1.5 |
| 6 | 2.1, 2.2, 2.3, 2.4 |
| 7 | 3.3, 3.6 |
| 8 | 3.1, 3.3, 3.7 |
| 9 | 4.1, 4.2 |
| 10 | 4.3, 4.4 |
| 11 | 4.4, 5.1 (decision only; delivery is a follow-up specification) |
| 12 | 3.3, 3.7, 4.3 |
