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

**Status:** Draft

**Phase status:** Draft, awaiting owner approval

**Development mode:** test-first

**Autonomy:** autonomous

Written 2026-11-17 against the draft [requirements](requirements.md) and [design](design.md). The
inventory each task cites is in [inventory/](inventory/README.md). The machine task list is generated
from this file when it is marked Ready.

| Phase | Tasks | Shippable because |
|---|---|---|
| 1. Seams and fakes | 1.1 to 1.8 | Additive |
| 2. Layers and locators | 2.1 to 2.5 | Behaviour unchanged; smoke tests pass |
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

- [ ] 1.1 Create the shared testing library
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

- [ ] 1.2 Consolidate and extend the file-system seam
  - **Objective:** `IFileSystem` and `IWritableFileSystem` cover every member listed in the design;
    the narrow file interfaces become role views over them.
  - **Files:** `src/KyberWeave.Contracts/Repositories/**`, the physical adapters in `src/KyberWeave.Data/` and `src/KyberWeave.Kernel/`
  - **Acceptance criteria:** every member in the design's file-system row exists; the existing narrow
    interfaces compile against the family; `PhysicalFileSystem` implements all of it; no consumer
    changes behaviour.
  - **Depends on:** none
  - _Requirements: 2.1, 2.10, 3.1_

- [ ] 1.3 Build the in-memory file system and its contract suite
  - **Objective:** `InMemoryFileSystem` in the testing library, and one contract suite for the
    file-system seam run against it and against `PhysicalFileSystem`.
  - **Files:** `tests/KyberWeave.Testing/FileSystem/**`, `tests/KyberWeave.Integration.Tests/Contracts/**`
  - **Acceptance criteria:** the suite covers every member and every semantic in requirement 5.3,
    including case-insensitive and case-sensitive instances and fault injection at the Nth
    operation; it passes against the fake in a unit run and against the adapter in the integration
    run on macOS, Linux and Windows in CI.
  - **Depends on:** 1.1, 1.2
  - _Requirements: 5.3, 5.4_

- [ ] 1.4 Path seam
  - **Objective:** `IPathResolver` with an explicit base directory, and its adapter over the file-system
    and environment seams; `IFileSystemPathSemantics` folded into it.
  - **Files:** `src/KyberWeave.Contracts/**`, `src/KyberWeave.Kernel/**`, `tests/KyberWeave.Testing/**`
  - **Acceptance criteria:** the members in the design's path row; contract suite run on fake and
    adapter; no member reads the process working directory implicitly.
  - **Depends on:** 1.2, 1.3
  - _Requirements: 2.7_

- [ ] 1.5 Process seam
  - **Objective:** `IProcessRunner` as the one process seam, absorbing `IProcessExecutor`,
    `IProgramRunner` and `IBoundedProcessExecutor`; a scripted fake.
  - **Files:** `src/KyberWeave.Contracts/**`, `src/KyberWeave.Kernel/Processes/**`, `src/KyberWeave.Data/Hooks/KernelProcessRunner.cs`, `tests/KyberWeave.Testing/**`
  - **Acceptance criteria:** run, capture, timeout, input, working directory, environment, is-alive,
    find-on-path; contract suite on fake and adapter; the old interfaces become views or are removed
    once unused.
  - **Depends on:** 1.1
  - _Requirements: 2.2_

- [ ] 1.6 Environment, platform, identifier, console, lease and resource seams
  - **Objective:** extend `IProcessEnvironment` with platform facts; add `IIdGenerator`, `IConsole`,
    `ILease`, `IEmbeddedResources`; adapters and fakes for each.
  - **Files:** `src/KyberWeave.Contracts/**`, `src/KyberWeave.Kernel/**`, `tests/KyberWeave.Testing/**`
  - **Acceptance criteria:** each has a contract suite on fake and adapter; `TimeProvider` is
    registered in every composition root and the framework's fake time provider is available to tests.
  - **Depends on:** 1.1
  - _Requirements: 2.3, 2.4, 2.5, 2.6, 2.8, 2.9_

- [ ] 1.7 Corpus seam
  - **Objective:** `ICorpusSource` for tests of the shipped content, replacing the repository-root walk
    copied into 159 test files.
  - **Files:** `tests/KyberWeave.Testing/Corpus/**`, the 159 test files
  - **Acceptance criteria:** no test file walks the tree to find the repository root; every content
    test reads through the seam.
  - **Depends on:** 1.1; Q1 answered
  - _Requirements: 8.1_

- [ ] 1.8 Adapter and exceptions registers
  - **Objective:** list every adapter with its seam, and every sealed framework type used directly with
    its reason.
  - **Files:** `docs/reference/seams.md` (new), `docs/reference/cli-composition.md`
  - **Acceptance criteria:** every class that calls a resource directly after phase 1 is either listed
    as an adapter or is on a later task's list.
  - **Depends on:** 1.2 to 1.6
  - _Requirements: 3.2, 3.3_

## Phase 2: Layers and locators

- [ ] 2.1 Decide how Domain sees the seams
  - **Objective:** measure `Domain` referencing `Contracts` against moving the seams into `Kernel`;
    record D7.
  - **Files:** project files, `tests/KyberWeave.Architecture.Tests/**`, the design's decisions table
  - **Acceptance criteria:** the chosen reference builds with no cycle; the architecture tests state it.
  - **Depends on:** 1.8
  - _Requirements: 4.2_

- [ ] 2.2 Remove Core's references to Data and Application
  - **Objective:** `KyberWeave.Core` and `Core.Docs` depend on `Contracts` and `Domain` only.
  - **Files:** `src/KyberWeave.Core/**`, `src/KyberWeave.Core.Docs/**`, the composition roots
  - **Acceptance criteria:** the references are gone; the 17 static locators (`DoctorServices`,
    `RunBaseState`, `LegacyPorts` and the rest in the core inventory) are deleted and their callers
    take services by injection.
  - **Depends on:** 2.1
  - _Requirements: 1.5, 4.1, 4.3_

- [ ] 2.3 Retire the static configuration loader
  - **Objective:** the 23 callers of `KyberWeaveConfigLoader.TryLoad` take `IKyberWeaveConfigRepository`.
  - **Files:** the call sites listed in the hosts inventory
  - **Acceptance criteria:** the static loader is removed; each caller has a unit test with a fake
    repository.
  - **Depends on:** 2.2
  - _Requirements: 1.5, 2.10_

- [ ] 2.4 Retire the static process runner
  - **Objective:** the 23 callers of the static `ProcessRunner.Run` take `IProcessRunner`.
  - **Files:** the 12 files listed in the data and tests inventories
  - **Acceptance criteria:** the static entry point is removed; each caller has a unit test with the
    scripted fake.
  - **Depends on:** 1.5, 2.2
  - _Requirements: 1.5, 2.2_

- [ ] 2.5 Domain onto seams
  - **Objective:** the 33 direct resource sites and 17 services built inside services in `Domain`
    move onto seams passed in.
  - **Files:** `src/KyberWeave.Domain/**` per the core inventory (`DocSpecValidator`, renderers and others)
  - **Acceptance criteria:** no non-adapter class in `Domain` calls a resource; each converted class
    has a unit test.
  - **Depends on:** 2.1
  - _Requirements: 1.2, 2.1 to 2.9_

## Phase 3: Squad deployment

- [ ] 3.1 Crash-safety contract tests for squad deployment
  - **Objective:** for install, update, uninstall, rollback and recovery, inject a fault at every
    file operation, recover, and compare the whole tree with the old or the complete new tree; run on
    the in-memory file system and on the real disk.
  - **Files:** `tests/KyberWeave.Testing/Squad/**`, `tests/KyberWeave.Integration.Tests/Squad/**`
  - **Acceptance criteria:** the seven weaknesses found by the 2026-11-17 review are each a test that
    fails today against the real disk; foreign, adopted and locally edited files are asserted untouched
    in every case.
  - **Depends on:** 1.3
  - _Requirements: 10.2_

- [ ] 3.2 `SquadStateStore` and `SquadStateMigration` onto seams
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

- [ ] 4.1 `KyberWeave.Application/Update` (`SelfUpdater`, 29 sites)
- [ ] 4.2 `KyberWeave.Application/Docs` (56 sites: `StandardsFolderMigration`, `TaskPromptService`, `DocumentIndexHost` and the rest)
- [ ] 4.3 `KyberWeave.Application/Review` and `Arbiter` (71 sites: `GateRunService` deadline onto `TimeProvider`, `ReviewVerdictService` paths)
- [ ] 4.4 `KyberWeave.Data` outside `Squad/` (`Arbiter`, `Review`, `Hooks`, `Docs`, `Configuration` and the rest: about 630 sites)
- [ ] 4.5 `KyberWeave.Cli` (181 sites; `CommandHelpers`, `PlanArguments` and `SquadCommandComposition` first)
- [ ] 4.6 `KyberWeave.Mcp` (32 sites; `CodeGraphFreshness` first)
- [ ] 4.7 `KyberWeave.Arbiter` and `KyberWeave.Hooks` (6 sites)

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
  - **Objective:** per the answer to Q1, the tests of shipped content live together and read through
    the corpus seam.
  - **Depends on:** 1.7
  - _Requirements: 8.1, 8.2_

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
  - **Objective:** write the rules into `AGENTS.md`, the test standard and the review guidance; harvest
    the design into its target documents; record the decisions in an ADR; archive this specification.
  - **Acceptance criteria:** requirement 9 met; `docs validate . --merge-ready` reports no findings.
  - **Depends on:** 5.6
  - _Requirements: 9.1, 9.2_
