---
id: standards/test
title: "Test coding standard"
doc-type: coding-standard
status: draft
technology: test
owner: unassigned
last-reviewed: 2026-08-16
---

# Test coding standard

How tests are written in this repository. Agents and skills resolve this document as
`<test-coding-standard>`, so it outranks the defaults a portable agent shipped with.
Language-level C# in test files follows `<csharp-coding-standard>`; Python and frontend
tests follow `<python-coding-standard>` and `<react-coding-standard>` the same way.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

### .NET (primary)

- **Runner:** xUnit. Pick one runner and stay on it.
- **Mocking:** NSubstitute. Prefer it over Moq — leaner syntax, no `Setup`/`Returns`
  duplication.
- **Assertions:** FluentAssertions (`result.Should().Be(...)`).
- **Integration:** `WebApplicationFactory<Program>` for API tests;
  `Microsoft.Data.SqlClient` against a real LocalDB, Docker, or dedicated test database
  for repository tests.
- **Naming:** `MethodName_StateUnderTest_ExpectedBehavior` (e.g.
  `CreateJob_WhenDuplicateArtifact_ThrowsConflictException`).
- **Structure:** Arrange / Act / Assert with blank-line separation; one logical
  assertion cluster per test; no test logic shared via inheritance — use fixtures and
  builders.

### Python

- **Runner:** pytest, with `parametrize` for table-driven cases.
- **Mocking:** `unittest.mock` or `pytest-mock`. Mock at the I/O boundary, never inside
  domain logic.
- **Naming:** `test_<unit>_<scenario>` snake_case.
- **Coverage:** meet the threshold declared as **Test Coverage Config**, or `>80%` line
  coverage with `pytest-cov` when that property is absent.

### Frontend

- **Unit / component:** Vitest + React Testing Library.
- **E2E:** Playwright. Coordinate scope with `react-dev` — do not duplicate their smoke
  tests.

## Isolation

Tests run in any order and do not depend on each other. Each test arranges its own data.
No shared mutable state between test methods.

## What to assert

A test name is an assertion, not a number. New behaviour ships with the test that would
fail without it. A bug fix ships with a regression test whose name encodes the scenario
that was broken (link to an issue id in a comment if one exists).

- **No test that only asserts it does not throw.** Assert the observable outcome.
- **No `Thread.Sleep` or arbitrary delays.** Use `await`, `WaitForAsync`, or a polling
  helper with a timeout.
- **Do not add getter/setter-only tests to pad coverage.** When a test would pin a
  type's shape, follow `<csharp-coding-standard>`. Do not treat a persistence row or a
  DTO as a Domain entity.

Prefer builder classes over inline object creation for complex domain objects. Builders
live next to the tests they serve.

## Layer boundaries

| Layer | Kind | Infrastructure |
|---|---|---|
| Domain | Unit | None — pure logic, no external deps |
| Application | Unit | Mocked repositories (NSubstitute) |
| Persistence (repository) | Integration | Real SQL Server — LocalDB or Docker |
| API (controller) | Integration | `WebApplicationFactory` + real DB |
| E2E | End-to-end | Full stack |

**Never mock the database in integration tests.** Mocked DB tests pass while production
migrations break. Use a real SQL Server instance.

Unit tests cover domain logic and service classes only — no infrastructure, no DB, no
HTTP.

## Commands

```bash
dotnet test --filter <TestClass>
dotnet test
pytest tests/<module>
```
