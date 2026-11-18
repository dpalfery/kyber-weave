---
id: archive/specs/interface-driven-unit-tests/requirements
title: Interface-driven unit tests requirements
doc-type: requirements
status: archived
owner: dpalfery
last-reviewed: 2026-11-18
component: CI Pipelines
documentation-impact: extends-component
impact-components: [CI Pipelines, KyberSquad, KyberArbiter, DocGraph, ReviewCouncil]
keywords:
  - unit tests
  - integration tests
  - interfaces
  - seams
  - adapters
  - fakes
  - in-memory file system
  - test speed
---

# Requirements Document

## Introduction

The layered-refactor completion specification (draft, 2026-11-16, decision D5) required one test
project per product project, use cases tested over fakes, and a separate integration test project for
real file-system, process, git and `gh` work. That goal was not reached. On 2026-11-17 the full .NET
product test run took 251 seconds locally against a two-minute rule, and an inventory of every class
found the cause: the product reaches outside resources directly instead of through interfaces.

Measured on 2026-11-17 (the inventory is in [inventory/](inventory/README.md)):

- About 2,800 call sites in `src/` touch the file system, processes, the environment, the clock,
  random ids, the console or locks directly; about 1,200 of them sit in classes that also hold logic.
- 32 static classes in `KyberWeave.Data` hold logic and cannot receive an interface at all; 17 static
  service locators in `KyberWeave.Core` and `KyberWeave.Core.Docs` build production adapters with `new`.
- `KyberWeave.Core` references `KyberWeave.Data` and `KyberWeave.Application`.
- `KyberWeave.Domain` holds logic, not only data: of its 440 files, 210 are static classes holding logic
  (about 38,000 lines) and 131 hold only records, enums or interfaces.
- The interfaces that exist are thin and bypassed: `IProcessRunner` has one constructor consumer while
  a static `ProcessRunner.Run` is called 23 times; a static configuration loader is called 23 times
  while `IKyberWeaveConfigRepository` exists.
- Of 615 test classes, 117 (15 %) are true unit tests.

This specification completes the D5 goal. Every product behaviour becomes testable by an in-memory
unit test written against interfaces. Integration tests remain only as a safety net in CI. The one
allowed exception is a framework type that is sealed and offers no interface.

The owner's instruction (2026-11-17): "analyze every portion of our tests, every portion of our
application, and make sure that there's nothing that's using concrete classes, and that everything is
designed to be interface domain-driven so that all code can be tested by a true unit test and that no
integration tests are required. Only exception to that would be usage of sealed classes provided by
frameworks that don't support interface-driven design."

## Decisions

| ID | Question | Answer / decision | Status |
|---|---|---|---|
| RQ-1 | Where is this document's lasting record? | Requirements 1–10, as delivered. The behaviour it delivered is in the seams register, the test standard and the review guidance mapped below. | Closed |

## Design harvest

This document declares no harvest rows of its own: the specification's items H1–H6 are declared in
[the specification](README.md#design-harvest) and mapped there. These rows trace this document's own
content.

| ID | Design item | Target component | Target doc type | Harvested |
|---|---|---|---|---|
| RH-1 | Requirements 1–10, as delivered: seams, adapters, layers, tests and the written rules | CI Pipelines | coding-standard | yes |

## Closeout mapping

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| RQ-1 | [Seams register](../../../reference/seams.md) | extends-component | archived |
| RH-1 | [Test standard](../../../standards/test/test.standards.md) | extends-component | archived |
| RQ-1 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md) | extends-component | archived |
| RQ-1 | [Kyber Arbiter architecture](../../../kyber-arbiter/architecture.md) | extends-component | archived |
| RQ-1 | [Documentation ontology](../../../documentation-ontology.md) | extends-component | archived |
| RQ-1 | [Review council architecture](../../../code-review/architecture.md) | extends-component | archived |


## Requirements

### Requirement 1: Services depend on interfaces

**User Story:** As a developer, I want every service to depend only on interfaces, so that any
collaborator can be replaced by a fake in a unit test.

#### Acceptance Criteria

1.1. WHEN a class performs work for another class (a service) THEN its constructor SHALL take that
collaborator as an interface, never as a concrete service class.

1.2. WHEN a service needs another service THEN it SHALL receive it by injection, and SHALL NOT create
it with `new`, except in a composition root or a factory whose only job is creation.

1.3. IF a parameter is data (a record, a value object, an options class, an enum, a DTO) THEN it SHALL
be passed as its concrete type; data is not a dependency.

1.4. WHEN a static class holds logic that reads or changes any outside resource, or that another
service depends on THEN it SHALL become an instance class registered against an interface. Static
classes SHALL remain only for pure functions over their arguments and for constants.

1.5. WHEN a static service locator (a static member that returns a production service) exists THEN it
SHALL be removed and its callers SHALL receive the service by injection.

### Requirement 2: Outside resources sit behind seams

**User Story:** As a developer, I want every outside resource reached through an interface, so that a
unit test controls it completely.

#### Acceptance Criteria

2.1. WHEN product code reads or writes files or directories, resolves links, takes file locks, or
creates temporary locations THEN it SHALL do so through the file-system seam.

2.2. WHEN product code starts a program, checks whether a process is alive, or looks a program up on
the path THEN it SHALL do so through the process seam.

2.3. WHEN product code reads environment variables, the current directory, the home or temporary
directory, the user or machine name, or the operating system THEN it SHALL do so through the
environment and platform seam.

2.4. WHEN product code reads the wall clock, measures elapsed time, sets a deadline, or waits THEN it
SHALL do so through `TimeProvider`.

2.5. WHEN product code needs a new identifier or a random value whose value a test observes THEN it
SHALL obtain it through the identifier seam.

2.6. WHEN product code writes to or reads from the console THEN it SHALL do so through the console seam.

2.7. WHEN product code turns a relative path into a full path THEN it SHALL do so through the path seam,
which takes the base directory from the environment seam; it SHALL NOT read the process working
directory implicitly.

2.8. WHEN product code holds a cross-process lock or a named mutex THEN it SHALL do so through the lease
seam.

2.9. WHEN product code reads an embedded resource or a network resource THEN it SHALL do so through an
interface.

2.10. IF an interface for a resource already exists THEN no class that holds that interface SHALL
bypass it to reach the same resource directly.

### Requirement 3: Adapters are thin and are the only code that touches the resource

**User Story:** As a maintainer, I want the code that touches real resources to be small and free of
logic, so that almost nothing needs a real resource to be tested.

#### Acceptance Criteria

3.1. WHEN a class calls `System.IO`, `System.Diagnostics.Process`, `Environment`, `Console`, the system
clock, `Guid.NewGuid`, `Random`, `Mutex` or a network client directly THEN that class SHALL be an
adapter: it implements one seam, and contains no decisions beyond translating the call.

3.2. WHEN an adapter is added or changed THEN it SHALL be listed in the adapter register with the seam
it implements.

3.3. IF a framework type is sealed and offers no interface THEN it MAY be used directly, and SHALL be
listed in the exceptions register with the reason; WHEN a test needs to control its behaviour (time,
names, results) THEN it SHALL still be wrapped behind a seam.

### Requirement 4: Layers reference only what they may

**User Story:** As a maintainer, I want the project references to follow the layers, so that a seam
added to one layer cannot be bypassed from another.

#### Acceptance Criteria

4.1. WHEN the build runs THEN `KyberWeave.Core` and `KyberWeave.Core.Docs` SHALL NOT reference
`KyberWeave.Data` or `KyberWeave.Application`.

4.2. WHEN the build runs THEN `KyberWeave.Domain` SHALL hold only data: records, DTOs, property bags,
value objects, enums, options and constants. It SHALL hold no service, rule, validator, parser or
renderer.

4.3. WHEN logic sits in `KyberWeave.Domain` THEN it SHALL move to `KyberWeave.Application`, where the
services live: as an instance service behind an interface, or, for a pure function over its arguments,
as a static helper.

4.4. WHEN adapters are registered THEN they SHALL be registered only in the composition roots of the
programs (CLI, MCP server, rule-checker, hooks) and of the data layer.

### Requirement 5: Every behaviour has an in-memory unit test

**User Story:** As a developer, I want every behaviour proven by a unit test that runs in memory, so
that the tests are fast, isolated and run anywhere.

#### Acceptance Criteria

5.1. WHEN a unit test runs THEN it SHALL touch no disk, start no program, read no environment variable,
read no real clock, open no network connection and hold no cross-process lock.

5.2. WHEN a behaviour of a non-adapter class is tested THEN a unit test SHALL prove it through fakes of
the class's interfaces.

5.3. WHEN the file-system seam is faked THEN the fake SHALL reproduce the behaviours the product relies
on: atomic rename with overwrite, exclusive create, case-insensitive but case-preserving names where the
platform has them, links and their resolution, deletion without following links, enumeration, length
and last-write time.

5.4. WHEN a fake implements a seam THEN the same contract tests SHALL run against the fake in a unit
test project and against the adapter in the integration test project, so that the fake cannot drift
from the real resource.

5.5. WHEN a test needs a fake THEN it SHALL take it from the one shared fakes library; there SHALL be one
fake per interface.

### Requirement 6: Test projects follow the product projects

**User Story:** As a developer, I want each product project to have its own unit test project, so that
the tests of a change are easy to find and fast to run.

#### Acceptance Criteria

6.1. WHEN a product project contains logic THEN a unit test project named `<Project>.Tests` SHALL hold
its unit tests.

6.2. WHEN a test performs a real file-system, process, git, `gh` or network operation THEN it SHALL live
in `KyberWeave.Integration.Tests`.

6.3. WHEN a developer runs every unit test project on the reference developer machine THEN the run SHALL
finish within 120 seconds.

### Requirement 7: Integration tests are a safety net

**User Story:** As the owner, I want integration tests to be a small safety net run by CI, so that no
behaviour depends on them for its proof.

#### Acceptance Criteria

7.1. WHEN an integration test exists THEN it SHALL be one of: a contract test of an adapter against the
real resource; or an end-to-end smoke test of a whole flow (install, update, uninstall, recovery, a
review gate run, a delivery) that also has unit tests.

7.2. WHEN CI runs THEN it SHALL run the integration test project as its own job, required for merge.

7.3. WHEN a developer agent or a task gate runs tests THEN it SHALL run unit test projects only.

7.4. IF an integration test fails WHILE every unit test passes THEN the gap SHALL be closed by a unit
test or a contract test that would have failed, in the same change that fixes it.

### Requirement 8: Tests of the shipped content

**User Story:** As a maintainer, I want the tests that check the content we ship (agents, skills,
documents, workflows) to be honest about what they read, so that they stay fast and do not copy each
other's setup.

#### Acceptance Criteria

8.1. WHEN a test checks shipped content THEN it SHALL read that content through one read-only corpus
seam, and SHALL NOT walk the directory tree to find the repository root itself.

8.2. WHEN a product test checks how the product handles content THEN it SHALL use fixture content in
memory, not the shipped corpus.

8.3. WHEN a test checks shipped content THEN it SHALL be a content test in `KyberWeave.Content.Tests`,
neither a unit test nor an integration test; it SHALL write nothing and start no program, and it SHALL
run locally with the unit test projects, inside the 120 seconds of requirement 6.3.

### Requirement 9: The rules are written where agents read them

**User Story:** As the owner, I want the rules written in the documents agents read, so that new code
does not reintroduce direct resource access.

#### Acceptance Criteria

9.1. WHEN this work completes THEN `AGENTS.md` and the test standard SHALL state: the seams and where
they live; that only adapters touch resources; that new behaviour gets an in-memory unit test against an
interface; and that code testable only through a real resource is a design gap to raise, not a reason
to add a slow test.

9.2. WHEN a reviewer reviews a change THEN the review guidance SHALL name direct resource access outside
an adapter, and logic in `KyberWeave.Domain`, as findings.

9.3. The rules in 9.1 and 9.2 SHALL be enforced by written guidance and review, not by a build check or
an architecture test.

### Requirement 10: The product keeps working at every step

**User Story:** As a user, I want each step of this work to ship without changing behaviour, so that I
can keep using the product while it is done.

#### Acceptance Criteria

10.1. WHEN a class moves onto a seam THEN its observable behaviour SHALL NOT change, and the integration
safety net SHALL pass.

10.2. WHEN code that protects a user's files (install, update, uninstall, rollback, recovery) moves onto
a seam THEN the change SHALL carry the crash-safety contract tests for that code, run against the fake
and against the real file system, and SHALL be reviewed by a second reviewer before merge.
