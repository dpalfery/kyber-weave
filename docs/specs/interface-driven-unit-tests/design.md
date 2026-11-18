---
id: specs/interface-driven-unit-tests/design
title: Interface-driven unit tests design
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
documentation-impact: extends-component
impact-components: [CI Pipelines, KyberSquad, KyberArbiter, DocGraph, ReviewCouncil]
keywords:
  - seams
  - adapters
  - fakes
  - contract tests
  - in-memory file system
  - TimeProvider
  - composition root
---

# Design Document

## Overview

The product already has most of the interfaces it needs (about 230, most in
`KyberWeave.Contracts/Repositories`) and its composition roots register about 113 classes against
them. What it lacks is discipline below the composition root: logic classes call outside resources
directly, 32 static classes hold logic, static locators build adapters, and the interfaces that exist
are thin and bypassed. This design does four things:

1. Defines a small, fixed set of **seams**, consolidating the overlapping interfaces that exist today.
2. Keeps **adapters** as the only code that touches a real resource, each thin and listed.
3. Moves logic off direct resource calls and out of static classes, layer by layer.
4. Proves each **fake** against its adapter with one shared **contract test suite**, so unit tests
   that use the fake cannot pass while the real code fails.

It completes decision D5 of the layered-refactor completion specification (draft, 2026-11-16): one
unit test project per product project, use cases over fakes, and `KyberWeave.Integration.Tests` for
real I/O. That specification's task 4 (the test split) is delivered here, extended by the
requirement that every behaviour has a unit test.

## Evidence

The inventory, read class by class on 2026-11-17 at commit `a5af901f` of the role-based worker agents
branch, is in [inventory/](inventory/README.md). Summary:

| Project | Direct resource sites | Concrete service dependencies | `new` of a service inside a service | Notes |
|---|---|---|---|---|
| `KyberWeave.Data` | 1,347 (about 145 in adapters) | 12 | 13 | 32 static classes hold logic; `Squad/` alone has 716 sites |
| `KyberWeave.Application` | 171 | 12 | 78 (46 in roots) | `SelfUpdater` 29, `StandardsFolderMigration` has no seam at all |
| `KyberWeave.Cli` | 181 | 5 | 6 | 23 calls to a static configuration loader; 28 `Console` sites |
| `KyberWeave.Domain` | 33 | 0 | 17 | 210 of 440 files are static classes holding logic; 131 are data only |
| `KyberWeave.Core`, `Core.Docs` | 0 | 4 | 50 | 17 static locators; references `Data` and `Application` |
| `KyberWeave.Kernel` | 12 | 0 | 0 | The intended operating-system boundary |
| `KyberWeave.Mcp` | 32 | 2 | 3 | `CodeGraphFreshness` is 17 of the 32 |
| `KyberWeave.Arbiter`, `Hooks` | 6 | 0 | 2 | Already close to the target |
| `KyberWeave.Contracts` | 0 | 0 | 0 | Interfaces and records only |

Tests: 615 test classes, 6,795 test cases. 117 classes are true unit tests. 126 use only a scratch
folder; 372 need a real resource: 176 read the shipped content from the checkout, 91 start a program,
56 read the clock, 25 the environment, 22 random ids. The largest single cause is not a missing
interface: tests construct concrete adapters that already have one (`PhysicalFileSystem` in 27
classes, `DocumentLoader` in 31, `GateReportFacts` in 28, `SquadStateStore` in 12). Fakes are
duplicated: 30 for `IArbiterProvider`, 11 each for `ISquadReleaseSource` and `ISquadRenderer`.

## Architecture

```text
 Programs (composition roots)   Cli   Mcp   Arbiter   Hooks
        │ register adapters against seams; nothing else news a service
        ▼
 Application ── all logic: use cases, services, rules, validators, parsers, renderers; depends on seams only
        │
 Core / Core.Docs ── domain services over Contracts only (no Data, no Application)
        │
 Domain ── data only: records, DTOs, property bags, value objects, enums, options, constants
        │
 Contracts ── the seams (interfaces) and data records
        ▲
 Data ── adapters and stores only: the only code that calls System.IO, Process, Environment, Console, clock, Mutex
        │
 Kernel ── shared low-level types with no project references (an adapter cannot live here: it would need Contracts)
```

Tests mirror it:

```text
 <Project>.Tests  (one per product project)  ── in memory, through fakes
 KyberWeave.Testing                          ── the shared fakes, the in-memory file system,
                                                the contract test suites, builders
 KyberWeave.Integration.Tests                ── contract suites run against the adapters,
                                                plus a few end-to-end smoke tests; CI only
 KyberWeave.Content.Tests                    ── checks of the shipped content, through the corpus seam
 KyberWeave.Architecture.Tests, RepoPolicy.Tests, PromptContent.Tests ── as today
```

## Components and Interfaces

### The seams

One interface family per resource. Where several interfaces cover one resource today, they are
consolidated: the narrow ones stay as role interfaces that the broad one extends, so existing
consumers keep compiling while they move.

| Seam | Interface(s) | Members it must cover | Replaces or absorbs | Adapter |
|---|---|---|---|---|
| File system | `IFileSystem` (read), `IWritableFileSystem` (write), extended | exists, read text, bytes, lines, streams, length, last-write time, attributes, enumerate (no-follow option), create directory, write, atomic write, atomic move with and without overwrite, exclusive create, delete file, delete tree without following links, link create, one-hop and final link target, temporary directory | `IRepositoryFileStore`, `IDoctorFileSystem`, `ISquadFileAccess`, `ISquadLinkFileSystem`, `IFileStatReader`, `IExclusiveFileWriter`, `IContainedFileReader` become role views over it | `PhysicalFileSystem` |
| Paths | `IPathResolver` | full path against an explicit base, are-same, is-within, canonicalize (case and links), physical root | `IFileSystemPathSemantics`; about 270 `Path.GetFullPath` sites | `PhysicalPathResolver` over the file-system seam |
| Process | `IProcessRunner` | run with arguments, working directory, environment, timeout, input; captured output; is-alive; find on path | `IProcessExecutor`, `IProgramRunner`, `IBoundedProcessExecutor`, the static `ProcessRunner.Run` | `KernelProcessRunner` |
| Environment and platform | `IProcessEnvironment`, extended | variables, current, home and temp directories, user and machine name, input redirected, operating system, case-insensitive file system | 70 `OperatingSystem.Is*` sites, direct `Environment.*` | `ProcessEnvironment` |
| Time | `TimeProvider` (framework) | wall clock, monotonic timestamp and elapsed time, timers, delay (`Task.Delay(TimeSpan, TimeProvider)`) | `DateTime.UtcNow`, `Stopwatch`, `Thread.Sleep`, `Task.Delay` | `TimeProvider.System` |
| Identifiers | `IIdGenerator` | new GUID, new random token, random file name | `Guid.NewGuid`, `Random`, `Path.GetRandomFileName` | `SystemIdGenerator` |
| Console | `IConsole` | output and error writers, read line, input redirected | `Console.*` | `SystemConsole` |
| Lease | `ILease` | acquire a named cross-process lease with timeout, release | `new Mutex`, lock-file polling | `MutexLease` |
| Embedded resource | `IEmbeddedResources` | read a named resource | `GetManifestResourceStream` | `AssemblyEmbeddedResources` |
| Configuration | `IKyberWeaveConfigRepository` (exists) | load and save the host configuration | the static `KyberWeaveConfigLoader.TryLoad` | exists |
| Credentials | `ICredentialStore` (exists) | read, write and delete a named secret in the operating system's credential store | direct P/Invoke of the native credential API | `WindowsCredentialStore` and the other platform stores |
| Shipped content | `ICorpusSource` | read the shipped agents, skills, standards, documents and workflows by relative path | the `KyberWeaveTestPaths` root walk copied into 159 test files | `CheckoutCorpusSource` (tests), `IDocumentSource` where it already applies |

The sealed framework types used directly and kept (the exceptions register, Requirement 3.3): `JsonDocument`,
`ZipArchive`, `SHA256`, `ProcessStartInfo` (as data only), `HttpClient` (behind `IHttpClientFactory` or
a seam), `FileStream` (only inside the file-system adapter). Each is listed with its reason in
`docs/reference/seams.md` by task 1.8.

### The in-memory file system

`KyberWeave.Testing` provides `InMemoryFileSystem`, implementing the full file-system seam. It is the
hardest fake, because the product's crash-safety code relies on exact semantics: atomic replace,
exclusive create, case-insensitive but case-preserving names on macOS and Windows, case-sensitive on
Linux (chosen per instance, and per directory as Windows allows), links with one-hop and final resolution, deletion without following
links. It also supports fault injection (fail or "kill" at the Nth operation), which the install and
recovery tests need and cannot do reliably against a real disk today.

It is built in this repository, not taken from a package. Two candidates were considered:
`System.IO.Abstractions` with its testing helpers, which does not model links, exclusive create or
case-preserving lookup faithfully; and writing our own against our seam, which is the size of the
seam and nothing more. The second is chosen; the decision is D3.

### Contract tests

Every seam has one contract test suite in `KyberWeave.Testing`, written once as an abstract test class
with a factory for the implementation under test. It is run twice: by the owning unit test project
against the fake, and by `KyberWeave.Integration.Tests` against the adapter. The file-system suite
covers every member and every semantic listed above, on each platform CI runs. A fake that passes its
contract suite is trusted by unit tests; a behaviour the fake cannot reproduce is a failing contract
test, not a silent difference. This answers the risk recorded in `FakeSquadReleaseSource.cs`, where a
fake kept the suite green while real installs failed.

### Static classes and locators

Each static class that holds logic becomes an instance class registered against an interface (most
already exist in `Contracts`: `ISquadDeploymentPlanner`, `ISquadSourceLoader`, `ISquadPackager`,
`ISquadStateMigration` and others). Its static entry point is removed, not kept as a shim, once its
callers move. The static locators in `KyberWeave.Core`, `Core.Docs` and `Domain` (`DoctorServices`,
`RunBaseState`, `LegacyPorts` and 14 more) are deleted; their callers take the services by injection.
Pure static helpers (string formatting, parsing of a value passed in) stay static.

### Layers

`KyberWeave.Core` and `Core.Docs` drop their references to `Data` and `Application`.

`KyberWeave.Domain` holds data only (D7). Its logic, 210 static classes and about 38,000 lines,
moves to `KyberWeave.Application`: a class that reads a resource or that another service depends on
becomes an instance service behind an interface in `Contracts`; a pure function over its arguments stays
a static helper. Because `Domain` then needs no seam, the seams stay in `Contracts`, which already
references `Domain` for its records; no reference cycle arises.

Moving logic out of `Domain` changes who can reach it. `Application` references `Data`, so code in
`Data`, `Hooks` and `Core` that calls `Domain` logic today cannot call it in `Application`: each such
caller either moves to `Application` too or takes the service through its `Contracts` interface,
registered in a composition root. The first task of phase 2 lists these callers and picks one of the
two for each.

### The squad deployment code

`SquadTransaction`, `SquadDeploymentPlanner`, `SquadStateStore` and `SquadStateMigration` (293 direct
file-system sites) protect users' files. They move onto the file-system, path, lease, identifier and
time seams in their own phase, behind the crash-safety contract tests: for install, update, uninstall,
rollback and recovery, a fault injected at every operation is followed by recovery and an exact
comparison of the tree. These tests run against the in-memory file system in the unit project and
against the real disk in the integration project. The independent review of 2026-11-17 on the parked
journal-batching branch listed seven existing weaknesses in this code; each becomes a contract test
that fails today, so the move to seams also fixes them (D9).

### What stays an integration test

| Kind | Why it cannot be a unit test | Example |
|---|---|---|
| Adapter contract tests | They prove the adapter matches the real resource | `PhysicalFileSystem` against APFS, ext4, NTFS |
| End-to-end smoke tests | They prove the composition root wires real adapters | one install, update, uninstall and recovery per scope; one review gate run; one delivery with a fake `gh` |
| Real third-party programs | They prove our use of the program's interface | `git` facts against a real repository; `sqlite3`; a real release download behind a flag |

Everything else is a unit test.

## Data Models

No product data model changes. Two test data models are added in `KyberWeave.Testing`: the in-memory
file-system tree (entries with name, kind, bytes, link target, attributes, timestamps) and the fault
plan (operation index or predicate, fault kind).

## Error Handling

Adapters translate resource errors into the exceptions or result types the seam declares, and nothing
else. Logic that handled `IOException` from `System.IO` handles the seam's declared errors; the
contract suites pin which errors each member raises, so the fake raises the same ones.

## Testing Strategy

- Each task writes its tests first and runs them failing, as today.
- A class is converted only with a unit test that proves its behaviour through fakes and that would
  have needed the real resource before.
- Each seam's contract suite lands in the same task as the seam; the adapter run of it is the
  integration side.
- The time budget is measured on every full local unit run, under the rule already in `AGENTS.md`.
- Phase-level smoke: after each phase, the integration project and one acceptance run of the squad on
  a demo project pass.

## Phases

| Phase | Delivers | Shippable because |
|---|---|---|
| 1. Seams and fakes | Consolidated seams, adapters, the shared fakes library, the in-memory file system, contract suites | Additive; nothing calls the new members yet |
| 2. Layers and locators | Core references fixed, Domain's logic moved to Application, static locators removed, the static configuration loader and static process runner retired | Behaviour unchanged; smoke tests pass |
| 3. Squad deployment | The four squad deployment classes and the 32 static classes in `Data` onto seams, with crash-safety contract tests | Reviewed by a second reviewer; contract tests on real and fake |
| 4. The rest of the product | Application (including the logic moved from Domain), Cli, Mcp, Arbiter, Hooks onto seams | Per folder, each shippable |
| 5. Tests | Logic tests moved onto fakes; one unit test project per product project; adapter tests and smoke tests in the integration project; content tests through the corpus seam; unit run within 120 seconds | Measured |
| Closeout | Rules in `AGENTS.md` and the test standard, review guidance, harvest, archive | After phase 5 |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Is this new scope? | No. It completes decision D5 of the layered-refactor completion specification. | Owner, 2026-11-17: "It's part of the original requirements of this modernization ... it's completing the goal." | Owner direction |
| D2 | One file-system interface or many? | One family: `IFileSystem` and `IWritableFileSystem`, extended; the narrow interfaces become role views over it. | Seven narrow file interfaces each added for one caller; consumers kept compiling during the move. | Proposed |
| D3 | Build the in-memory file system or take a package? | Build it, against our seam. | Packages do not model links, exclusive create or case-preserving names faithfully; our seam is the size we need. | Proposed |
| D4 | How is a fake kept honest? | One contract suite per seam, run against the fake (unit) and the adapter (integration). | A fake kept the suite green while real installs failed (`FakeSquadReleaseSource.cs`). | Proposed |
| D5 | Monotonic clock interface? | No new interface: `TimeProvider` already has timestamps, elapsed time, timers and delay. | Framework type, not sealed against substitution; `FakeTimeProvider` exists in the framework's testing package. | Proposed |
| D6 | Keep static entry points as shims? | No. Remove each once its callers move. | The layered refactor left 25 shim types; shims keep the old path reachable. | Proposed |
| D7 | What does `Domain` hold, and how does it see the seams? | Data only. Its logic moves to `Application`, where the services live; `Domain` needs no seam, so the seams stay in `Contracts`. | Owner, 2026-11-17: "The domain project should house DTOs, property bags. It should not have logic. All logic should be in the application project where the services live." `Contracts` already references `Domain`, so `Domain` referencing `Contracts` would be a cycle. | Owner direction |
| D8 | Where do integration tests run? | CI only, as their own required job; developer agents and task gates run unit projects. | Owner, 2026-11-17: "the integration tests become a safety net on the CI pipeline." | Owner direction |
| D9 | The seven crash-safety weaknesses found on 2026-11-17 | Each becomes a contract test in phase 3 and is fixed there. | Independent review of the parked journal-batching branch. | Proposed |
| D10 | Is a test that reads the shipped content a unit test? | Neither unit nor integration: a third kind, content tests, in `KyberWeave.Content.Tests`, reading through the read-only corpus seam. They write nothing and start nothing, so they run locally with the unit projects and count toward the 120 seconds. | 176 test classes and two projects (`PromptContent.Tests`, `RepoPolicy.Tests`) check the shipped content; the content is the subject, so it cannot be faked. Owner, 2026-11-17: "Yes, third kind." | Owner direction |
| D11 | Enforce "only adapters touch resources" with an architecture test? | No. Written guidance in `AGENTS.md` and the test standard, and reviewers raise direct resource access outside an adapter as a finding. | Owner, 2026-11-17, chose guidance and review over a build check; consistent with the standing rule that test rules are guidance, not guards. | Owner direction |

## Open questions

None. Q1 to Q3 are answered in D10, D7 and D11.

## Design harvest

| Item | Target component | Target document |
|---|---|---|
| The seams table and the adapter register | CI Pipelines | `docs/reference/cli-composition.md` and a new `docs/reference/seams.md` |
| The test project layout and what is an integration test | CI Pipelines | `docs/standards/test/` and `AGENTS.md` |
| The in-memory file system and contract suites | CI Pipelines | `docs/standards/test/` |
| Crash-safety contract tests for squad deployment | KyberSquad | `docs/kyber-squad/architecture.md` |
| D1 to D11 | CI Pipelines | An ADR for D2 to D8 and D11 |
