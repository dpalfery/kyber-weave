---
id: specs/interface-driven-unit-tests/tasks
title: Interface-driven unit tests tasks
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
documentation-impact: extends-component
impact-components: [CI Pipelines, KyberSquad, KyberArbiter, DocGraph, ReviewCouncil]
keywords:
  - seams
  - fakes
  - contract tests
  - unit tests
  - integration tests
  - test-first
---

# Implementation Tasks

**Status:** Approved

**Phase status:** Approved (dpalfery, 2026-11-17)

**Development mode:** test-first

**Autonomy:** autonomous

Written 2026-11-17 against the draft [requirements](requirements.md) and [design](design.md). The
inventory each task cites is in [inventory/](inventory/README.md). The machine task list is generated
from this file when it is marked Ready.

| Phase | Tasks | Shippable because |
|---|---|---|
| 1. Seams and fakes | 1.1 to 1.8 | Additive |
| 2. Layers and locators | 2.1 to 2.6 | Behaviour unchanged; smoke tests pass |
| 3. Squad deployment | 3.1 to 3.5 | Contract tests on real and fake; second reviewer |
| 4. The rest of the product | 4.1 to 4.7 | Per folder |
| 5. Tests | 5.1 to 5.6 | Measured |
| Closeout | 6.1 | After 5.6 |

## How these tasks are dispatched

- **One task, one worker, about 20 minutes to an hour.** A task that grows past that is split, not
  stretched. Each converts one class or one folder and reports the direct-resource count of what it
  touched, before and after.
- **Test first.** Write the unit test through fakes, see it fail or see that it could not have been
  written before, then convert.
- **Commands.** Build once, then run the unit test project of the code touched, with a filter. Never
  the full suite inside a task; the gate and CI run it.
- **Crash-safety code** (phase 3) is reviewed by a second, stronger reviewer before merge, and its
  contract tests run against the real file system in the integration project.

## Phase 1: Seams and fakes

- [x] 1.1 Create the shared testing library
  - **Objective:** One library, `tests/KyberWeave.Testing`, that every test project references, holding
    the fakes, builders, the in-memory file system and the contract suites.
  - **Files:** `tests/KyberWeave.Testing/**` (new), the solution file, the test project files
  - **Acceptance criteria:** the library builds and holds no tests; module initializers every test
    assembly needs still run in each; the fakes that exist today are moved into it with one fake per
    interface (duplicates merged: 30 `IArbiterProvider`, 11 `ISquadReleaseSource`, 11
    `ISquadRenderer`, 9 `IProcessEnvironment`, 9 `ISquadUserPaths` and the rest listed in the tests
    inventory); every test that used a local copy uses the shared one.
  - **Depends on:** none
  - _Requirements: 5.5_

- [x] 1.2 Consolidate and extend the file-system seam
  - **Objective:** `IFileSystem` and `IWritableFileSystem` cover every member listed in the design;
    the narrow file interfaces become role views over them.
  - **Files:** `src/KyberWeave.Contracts/Repositories/**`, the physical adapters in `src/KyberWeave.Data/` and `src/KyberWeave.Kernel/`
  - **Acceptance criteria:** every member in the design's file-system row exists; the existing narrow
    interfaces compile against the family; `PhysicalFileSystem` implements all of it; no consumer
    changes behaviour.
  - **Depends on:** none
  - _Requirements: 2.1, 2.10, 3.1_

- [x] 1.3 Build the in-memory file system and its contract suite
  - **Objective:** `InMemoryFileSystem` in the testing library, and one contract suite for the
    file-system seam run against it and against `PhysicalFileSystem`.
  - **Files:** `tests/KyberWeave.Testing/FileSystem/**`, `tests/KyberWeave.Integration.Tests/Contracts/**`
  - **Acceptance criteria:** the suite covers every member and every semantic in requirement 5.3,
    including case-insensitive and case-sensitive instances and fault injection at the Nth
    operation; it passes against the fake in a unit run and against the adapter in the integration
    run on macOS, Linux and Windows in CI.
  - **Depends on:** 1.1, 1.2
  - _Requirements: 5.3, 5.4_

- [x] 1.4 Path seam
  - **Objective:** `IPathResolver` with an explicit base directory, and its adapter over the file-system
    and environment seams; `IFileSystemPathSemantics` folded into it.
  - **Files:** `src/KyberWeave.Contracts/**`, `src/KyberWeave.Kernel/**`, `tests/KyberWeave.Testing/**`
  - **Acceptance criteria:** the members in the design's path row; contract suite run on fake and
    adapter; no member reads the process working directory implicitly.
  - **Depends on:** 1.2, 1.3
  - _Requirements: 2.7_

- [x] 1.5 Process seam
  - **Objective:** `IProcessRunner` as the one process seam, absorbing `IProcessExecutor`,
    `IProgramRunner` and `IBoundedProcessExecutor`; a scripted fake.
  - **Files:** `src/KyberWeave.Contracts/**`, `src/KyberWeave.Kernel/Processes/**`, `src/KyberWeave.Data/Hooks/KernelProcessRunner.cs`, `tests/KyberWeave.Testing/**`
  - **Acceptance criteria:** run, capture, timeout, input, working directory, environment, is-alive,
    find-on-path; contract suite on fake and adapter; the old interfaces become views or are removed
    once unused.
  - **Depends on:** 1.1
  - _Requirements: 2.2_

- [x] 1.6 Environment, platform, identifier, console, lease and resource seams
  - **Objective:** extend `IProcessEnvironment` with platform facts; add `IIdGenerator`, `IConsole`,
    `ILease`, `IEmbeddedResources`; adapters and fakes for each.
  - **Files:** `src/KyberWeave.Contracts/**`, `src/KyberWeave.Kernel/**`, `tests/KyberWeave.Testing/**`
  - **Acceptance criteria:** each has a contract suite on fake and adapter; `TimeProvider` is
    registered in every composition root and the framework's fake time provider is available to tests.
  - **Depends on:** 1.1
  - _Requirements: 2.3, 2.4, 2.5, 2.6, 2.8, 2.9_

- [x] 1.7 Corpus seam
  - **Objective:** `ICorpusSource` for tests of the shipped content, replacing the repository-root walk
    copied into 159 test files.
  - **Files:** `tests/KyberWeave.Testing/Corpus/**`, the 159 test files
  - **Acceptance criteria:** no test file walks the tree to find the repository root; every content
    test reads through the seam.
  - **Depends on:** 1.1
  - _Requirements: 8.1_

- [x] 1.8 Adapter and exceptions registers
  - **Objective:** list every adapter with its seam, and every sealed framework type used directly with
    its reason.
  - **Files:** `docs/reference/seams.md` (new), `docs/reference/cli-composition.md`
  - **Acceptance criteria:** every class that calls a resource directly after phase 1 is either listed
    as an adapter or is on a later task's list.
  - **Depends on:** 1.2 to 1.6
  - _Requirements: 3.2, 3.3_

## Phase 2: Layers and locators

- [x] 2.1 Plan the move of Domain's logic
  - **Objective:** list every class in `KyberWeave.Domain` as data (stays) or logic (moves), and every
    caller of that logic outside `Application`, with the route each caller takes: move to `Application`,
    or take the service through its `Contracts` interface.
  - **Files:** the core inventory, the design's layers section
  - **Acceptance criteria:** every one of Domain's 440 files is classified; every caller in `Data`,
    `Hooks`, `Core` and the hosts has a route; the moves are grouped into tasks of one Domain folder each.
  - **Depends on:** 1.8
  - _Requirements: 4.2, 4.3_

- [x] 2.2 Remove Core's references to Data and Application
  - **Objective:** `KyberWeave.Core` and `Core.Docs` depend on `Contracts` and `Domain` only.
  - **Files:** `src/KyberWeave.Core/**`, `src/KyberWeave.Core.Docs/**`, the composition roots
  - **Acceptance criteria:** the references are gone; the 17 static locators (`DoctorServices`,
    `RunBaseState`, `LegacyPorts` and the rest in the core inventory) are deleted and their callers
    take services by injection.
  - **Depends on:** 2.1
  - _Requirements: 1.5, 4.1, 4.4_

- [x] 2.3 Retire the static configuration loader
  - **Objective:** the 23 callers of `KyberWeaveConfigLoader.TryLoad` take `IKyberWeaveConfigRepository`;
    one shared fake of it in the testing library, with a contract suite run on the fake and on
    `FileKyberWeaveConfigRepository`.
  - **Files:** the call sites listed in the hosts inventory
  - **Acceptance criteria:** the static loader is removed; each caller has a unit test with a fake
    repository.
  - **Depends on:** 2.2
  - _Requirements: 1.5, 2.10_

- [x] 2.4 Retire the static process runner
  - **Objective:** the 23 callers of the static `ProcessRunner.Run` take `IProcessRunner`.
  - **Files:** the 12 files listed in the data and tests inventories
  - **Acceptance criteria:** the static entry point is removed; each caller has a unit test with the
    scripted fake.
  - **Depends on:** 1.5, 2.2
  - _Requirements: 1.5, 2.2_

- [ ] 2.5 Move Domain's logic to Application, one Domain folder per task
  - **Objective:** the logic classes in each of Domain's folders (`Agents`, `Arbiter`, `Docs`, `Review`,
    `Squad` and the rest) move to the matching folder in `KyberWeave.Application`; the 33 direct resource
    sites and 17 services built inside services among them move onto seams as they go. Each move follows
    the route the 2.1 plan gives its caller rows: a Domain type that holds data and adds a method keeps
    the data in `Domain` and moves the method, a pure static helper moves as a static helper and each
    `Data` caller either moves with it or keeps a raw-I/O adapter, and a stateful service moves behind a
    `Contracts` interface.
  - **Order, with acceptance criteria** (corrected 2026-11-17 after step 0a failed: a type can move up to
    `Application` only once nothing below `Application` names it, and `Data` sits between `Contracts` and
    `Application`, so the callers move first, leaf first):
    1. **Step 0 — the `Data` and `Core` callers move up, leaf first.** A `Data` file moves to
       `Application` (with the seam it needs) only after every `Data` file that names it has moved, or
       together with them; a split leaves behind only a thin adapter, and where that adapter would be a
       single phase 1 seam call the whole class moves and takes the seam. A vertical pilot (one leaf
       caller, its record split and its `Contracts` member narrowed, in one branch) runs first and
       reports the service registrations and `internal` members the move touches. Acceptance: every
       `Data` and `Core` row of the 2.1 plan is moved, split or recorded as naming data only; `Data` and
       `Core` still do not reference `Application`; each change lands with the test that fails without
       it, written first.
    2. **Step 0b — the record splits and the `Contracts` findings.** The data-with-method types keep their
       data in `Domain` and move their methods; every `Contracts` member that names `Domain` logic is
       narrowed to name data only (D12: interfaces stay in `Contracts`). Acceptance: `KyberWeave.Contracts`
       names no type that will move.
    3. **Step 1 — the folder tasks below**, in the order the `depends on` column gives. Acceptance: each
       folder task meets the acceptance criteria below, and `dotnet build src/KyberWeave.Domain` after
       its `git mv` proves the order.
    Step 0 outcomes recorded 2026-11-18: these `Data` files wait for phase 3, because squad deployment
    code names them or they need the lease semantics task 3.4 settles: `Squad/Rendering`'s
    `FileSquadRenderInputReader`, `CopilotMcpServerProbe` and `SquadOperatorChannelOverlayFile` (they move
    together once `SquadWorkerBaselineFile` has an interface), `Squad/Presets/SquadModelPresetValidator`, `Squad/Parsing/SquadSourceLoader` (its 1,904 lines are named by `SquadDeploymentAdapters`), and
    `Squad/Instances/SquadInstanceLinkSync` (its unbounded, in-process-queuing mutex wait needs an `ILease`
    acquire that waits; decide it with 3.4). These stay adapters in `Data`, each now a phase 4 conversion
    rather than a move: `Doctor/FileCodeGraphIndexWorkspace` (task 4.9), `Update/CopilotPromptRefresher`
    (task 4.1); and `Configuration/FileKyberWeaveConfigRepository` is narrowed under step 0b.
    The squad deployment classes in `Data/Squad/Deployment` are not moved by this task: they reach
    `Application` in tasks 3.2 to 3.4, behind the crash-safety contract tests and the second reviewer
    requirement 10.2 asks for, and the Domain group they pin (2.5.3) moves after 3.4.
    Step 0's `Core` rows closed 2026-11-18: all 13 are gone, and `src/KyberWeave.Core` now holds no
    source files (the project stays — `LayerReferenceTests` and `CoreAssemblySplitTests` pin it).
    None of the 13 held logic of its own; each was a static locator or shim over a service that had
    already moved, so 2.2 deleted eleven of them (`CodeGraphProvisioner`, `DoctorFileSystem`,
    `GateCatalogue`, `ChangedFiles`, `ReviewDecisionModel`, `ReviewLensApplicability`,
    `ReviewVerdictDecisions`, `TaskGateSelection`, `ReviewConfigLoader`, `RunBaseState`,
    `RunStartEvidence`) and 2.3 the two configuration loaders (`KyberWeaveConfigLoader`,
    `KyberWeaveYamlFile`). Every caller now takes a `Contracts` interface
    (`ICodeGraphProvisioner`, `IDoctorFileSystem`, `IReviewGateCatalogue`, `IGitChangeReader`,
    `IReviewDecisionModelFactory`, `IReviewDecisionSteps`, `ITaskGateSelectionService`,
    `IRunStateStore`, `IRunStartEvidence`, `IKyberWeaveConfigRepository`), so the 2.1 inventory's
    prediction for this table — 7 moves, 1 split, 1 interface row — overstated it: there is no
    `Core` move, split or interface row left.
  - **Rules for every folder task:** a move commit is `git mv` only, with the build and the tests green
    before and after it; a seam conversion is a separate commit; each move updates the path-keyed
    artefacts — document code references and `kyber-weave docs drift .`,
    `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`, the `Domain` rows of
    `docs/reference/seams.md`, and the `KyberWeave.Domain.Tests` files of the moved classes.
    `Security` (2.5.10) is the pilot, and `Configuration` (2.5.1) waits for 2.3.
  - **Files:** `src/KyberWeave.Domain/<folder>/**`, `src/KyberWeave.Application/<folder>/**`, their callers
    per the 2.1 plan; split any folder over about 30 classes
  - **Acceptance criteria:** the folder in `Domain` holds data only; the moved classes call no resource
    directly; each moved class keeps or gains a unit test through fakes; behaviour unchanged.
  - **Depends on:** 2.1 and task 2.3 (which needs 2.2); its own steps 0a and 0b run before its folder
    tasks
  - _Requirements: 1.2, 1.4, 2.1 to 2.9, 4.2, 4.3_

- [ ] 2.6 Domain holds data only
  - **Objective:** confirm the end state once every 2.5 task is done.
  - **Acceptance criteria:** no class in `KyberWeave.Domain` has a method beyond construction,
    equality, formatting of itself, or validation of its own values; the count is reported.
  - **Depends on:** 2.5 and 3.4 (the squad deployment group moves after phase 3)
  - _Requirements: 4.2_

## Phase 3: Squad deployment

- [x] 3.1 Crash-safety contract tests for squad deployment
  - **Objective:** for install, update, uninstall, rollback and recovery, inject a fault at every
    file operation, recover, and compare the whole tree with the old or the complete new tree; run on
    the in-memory file system and on the real disk.
  - **Files:** `tests/KyberWeave.Testing/Squad/**`, `tests/KyberWeave.Integration.Tests/Squad/**`
  - **Acceptance criteria:** the seven weaknesses found by the 2026-11-17 review are each a test that
    fails today against the real disk; foreign, adopted and locally edited files are asserted untouched
    in every case.
  - **Depends on:** 1.3
  - _Requirements: 10.2_

- [x] 3.2 `SquadStateStore` and `SquadStateMigration` onto seams
  - **Files:** `src/KyberWeave.Data/Squad/Deployment/SquadStateStore.cs`, `SquadStateMigration.cs`
  - **Acceptance criteria:** no direct resource call; injected as `ISquadStateStore`; unit tests on
    the in-memory file system; contract tests pass on both.
  - **Depends on:** 3.1, 1.4, 1.6
  - _Requirements: 1.1, 2.1, 10.2_

- [ ] 3.3 `SquadDeploymentPlanner` onto seams
  - **Files:** `src/KyberWeave.Data/Squad/Deployment/SquadDeploymentPlanner.cs`
  - **Acceptance criteria:** an instance class behind `ISquadDeploymentPlanner`; no direct resource
    call; unit tests; contract tests pass on both.
  - **Depends on:** 3.2
  - _Requirements: 1.4, 2.1, 10.2_

- [ ] 3.4 `SquadTransaction` onto seams, fixing the seven weaknesses
  - **Files:** `src/KyberWeave.Data/Squad/Deployment/SquadTransaction.cs`
  - **Acceptance criteria:** no direct resource call; the named mutex behind `ILease`; ids and time
    through their seams; every 3.1 test passes on both file systems; second reviewer's verdict recorded.
  - **Depends on:** 3.3
  - _Requirements: 2.1, 2.4, 2.5, 2.8, 10.2_

- [ ] 3.5 The other static classes in Data onto seams
  - **Objective:** the 32 static classes in `KyberWeave.Data` that hold logic (`SquadSourceLoader`,
    `SquadPacker`, `SquadInstanceLinkSync`, `DocsRootPath` and the rest in the data inventory) become
    instance classes behind interfaces.
  - **Files:** one task per class or small group, as listed in the data inventory
  - **Acceptance criteria:** each converted class has no direct resource call and has a unit test.
  - **Depends on:** 3.4
  - _Requirements: 1.4, 2.1 to 2.9_

## Phase 4: The rest of the product

Each task below is one folder; split further where the inventory shows more than about 30 sites.

- [x] 4.1 `KyberWeave.Application/Update` (`SelfUpdater`, 29 sites)
- [ ] 4.2 `KyberWeave.Application/Docs` (56 sites: `StandardsFolderMigration`, `TaskPromptService`, `DocumentIndexHost` and the rest)
- [ ] 4.3 `KyberWeave.Application/Review` and `Arbiter` (71 sites: `GateRunService` deadline onto `TimeProvider`, `ReviewVerdictService` paths)
- [ ] 4.4 `KyberWeave.Data` outside `Squad/` (`Arbiter`, `Review`, `Hooks`, `Docs`, `Configuration` and the rest: about 630 sites)
- [x] 4.5 `KyberWeave.Cli` (181 sites; `CommandHelpers`, `PlanArguments` and `SquadCommandComposition` first)
- [x] 4.6 `KyberWeave.Mcp` (32 sites; `CodeGraphFreshness` first)
- [x] 4.7 `KyberWeave.Arbiter` and `KyberWeave.Hooks` (6 sites)
- [x] 4.8 `KyberWeave.Application` folders no other task names (12 classes: `Agents/AgentFileReader`, `Doctor/CodeGraphIndexProvisioner`, `Doctor/McpServerLiveProbe`, `Doctor/PostCheckoutHookInstaller`, `Hooks/EditHookService`, `RunStart/RunStartRunner`, `Skills/SkillFileReader`, `Squad/Briefs/SquadBriefBuilder`, `Squad/Deployment/FactoryMcpConfiguration`, `Squad/Instances/SquadInstanceService`, `Squad/Packaging/SquadPackService`, `Utilities/StatusLineProductArtifactSource`)
- [x] 4.9 `KyberWeave.Kernel` logic and the clock inside adapters (`Parsing/FileReferenceExtractor` and `VersionControl/CodeGraphIndexStaleness` onto the file-system and path seams; `Data/Doctor/FileCodeGraphIndexWorkspace` and `Kernel/Processes/StdioSession` take `TimeProvider` instead of the system clock and `Thread.Sleep`)
- [x] 4.10 Credential seam (`ICredentialStore` exists; record it as a seam; `Data/Arbiter/Credentials/WindowsCredentialStore` and its siblings become its adapters, the only code that calls the native credential API; a fake and a contract suite; callers take the interface)

For every task in phase 4:
  - **Acceptance criteria:** every class in the folder is an adapter or has no direct resource call;
    every converted class has a unit test through fakes; the folder's count before and after is
    reported.
  - **Depends on:** phase 2 for its layer
  - _Requirements: 1.1 to 1.5, 2.1 to 2.10_

## Phase 5: Tests

- [ ] 5.1 Create one unit test project per product project
  - **Objective:** `<Project>.Tests` for every product project with logic, per D5.
  - **Files:** `tests/**`, the solution file, CI workflows
  - **Acceptance criteria:** each project exists, builds and runs in CI; empty product projects
    (`KyberWeave.Core.Squad` has no source) are removed or given content, not given a test project.
  - **Depends on:** 1.1
  - _Requirements: 6.1_

- [ ] 5.2 Move logic tests onto fakes, by seam
  - **Objective:** the 126 scratch-folder classes and the 372 real-resource classes, grouped by the seam
    that forced them, take fakes; one task per seam group as listed in the tests inventory.
  - **Acceptance criteria:** each converted test class touches no resource and sits in the unit project
    of the code it tests.
  - **Depends on:** the phase 3 or 4 task that made the product class testable
  - _Requirements: 5.1, 5.2_

- [ ] 5.3 Adapter and smoke tests into the integration project
  - **Objective:** only contract runs against adapters, end-to-end smoke tests and real third-party
    program tests remain in `KyberWeave.Integration.Tests`.
  - **Acceptance criteria:** every integration test is one of the three kinds in requirement 7.1; each
    smoke-tested flow also has unit tests.
  - **Depends on:** 5.2
  - _Requirements: 6.2, 7.1_

- [ ] 5.4 Content tests through the corpus seam
  - **Objective:** per D10, the tests of shipped content live together in `KyberWeave.Content.Tests` and read through
    the corpus seam.
  - **Depends on:** 1.7
  - _Requirements: 8.1, 8.2, 8.3_

- [ ] 5.5 CI and gates
  - **Objective:** CI runs the unit projects and the integration project as separate required jobs;
    developer task gates and the review council's test gate run unit projects only.
  - **Files:** `.github/workflows/**`, `.kyber-weave/kyber-weave.yml`
  - **Depends on:** 5.3
  - _Requirements: 7.2, 7.3_

- [ ] 5.6 Measure
  - **Objective:** one full local run of every unit test project and the content tests, timed.
  - **Acceptance criteria:** within 120 seconds on the reference developer machine; integration
    project time reported separately.
  - **Depends on:** 5.1 to 5.5
  - _Requirements: 6.3_

## Closeout

- [ ] 6.1 Closeout
  - **Objective:** write the rules into `AGENTS.md`, the test standard and the review guidance (no
    build check or architecture test enforces them, D11); harvest
    the design into its target documents; record the decisions in an ADR; archive this specification.
  - **Acceptance criteria:** requirement 9 met; `docs validate . --merge-ready` reports no findings.
  - **Depends on:** 5.6
  - _Requirements: 9.1, 9.2, 9.3_
