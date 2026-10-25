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
`<test-coding-standard>`. Language-level C# in test files follows
`<csharp-coding-standard>`; Python, Rust, Go, and frontend tests follow
`<python-coding-standard>`, `<rust-coding-standard>`, `<go-coding-standard>`, and
`<react-coding-standard>` the same way.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

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
- **Coverage:** meet the coverage floor the host declares under `review.coverage` in its Kyber-Weave configuration and `kyber-weave review gates` reports, or `>80%` line
  coverage with `pytest-cov` when that property is absent.

### Rust

- **Runner:** `cargo nextest run` when the repository has adopted it, otherwise `cargo test`.
  Doctests still need `cargo test --doc`; nextest does not run them.
- **Placement:** unit tests in a `#[cfg(test)] mod tests` beside the code; integration tests
  that use only the public API in the crate's `tests/` directory.
- **Table-driven cases:** a slice of `(name, input, expected)` cases looped with the case name
  in the assertion message, or `rstest` when the repository already takes it.
- **Fakes:** substitute dependencies through traits. Prefer a hand-written fake to a mocking
  crate; adopt `mockall` only where the standard says so.
- **Naming:** `<unit>_<scenario>_<expected>` snake_case.
- **`unwrap` is allowed in tests** — a panic is the failure report. Tests that exercise an
  error path assert on the error variant with `matches!`, not on the message string.
- **Async:** `#[tokio::test]`; time-dependent tests use `tokio::time::pause` rather than real
  sleeps.

### Go

- **Runner:** `go test ./...`, always with `-race` where cgo is available.
- **Table-driven tests:** a slice of named cases, each run as `t.Run(tc.name, ...)` so a
  failure names its case. Mark independent subtests `t.Parallel()`.
- **Fakes:** substitute dependencies through small interfaces with hand-written fakes. No
  mocking framework unless the standard names one.
- **Assertions:** the standard library (`if got != want { t.Errorf("…: got %v, want %v", …) }`)
  and `go-cmp` for structural diffs, unless the repository already uses `testify`.
- **Helpers** call `t.Helper()`; cleanup uses `t.Cleanup`, temp files use `t.TempDir()`.
- **Package:** black-box tests in `package foo_test` by default; internal `package foo` tests
  only for unexported logic that cannot be reached otherwise.

### TypeScript (Node)

- **Runner:** Vitest (`vitest run`), unless the repository already uses `node:test` or Jest.
- **Placement:** `<module>.test.ts` beside the module, or under `tests/` when the package keeps
  them there. Match the package.
- **Fakes:** pass substitutes through the parameters the code already takes (database handle,
  clock, file-system root). `vi.mock` of a module is a last resort; a test that needs it is a
  sign the code constructs its own dependencies.
- **Data access:** run queries against a real in-memory or temporary-file database built from
  the production schema, not a mocked driver.
- **Time:** `vi.useFakeTimers()` and `vi.setSystemTime()` instead of real sleeps; restore in
  `afterEach`.
- **Files:** a fresh `mkdtemp` directory per test, removed in `afterEach`.
- **Absent values:** where a value can be unmeasured, at least one case asserts it stays
  `null`/`undefined` through to the output, and is not reported as `0`.

### Frontend

- **Unit / component:** Vitest + React Testing Library.
- **E2E:** Playwright. Coordinate scope with `react-dev` — do not duplicate their smoke
  tests.

## Isolation

Tests run in any order and do not depend on each other. Each test arranges its own data.
No shared mutable state between test methods.

Clean up test data in DisposeAsync or use a transaction-per-test pattern, so one test's rows
never leak into the next. Use unique identifiers for test records to avoid conflicts with
parallel runs — a GUID or a per-test prefix, not a fixed id another test might also claim.

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
cargo nextest run --workspace --locked   # or: cargo test --workspace --locked
go test -race ./...
npx vitest run <path>
```
