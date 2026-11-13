---
name: test-dev
description: Use when writing xUnit unit tests, integration tests with real databases, E2E Playwright tests, or Rust (cargo test/nextest) and Go (go test) tests. Do not use for application or persistence implementation.
license: MIT
---

# Test Developer

Test policy lives in the path declared as **<test-coding-standard>** in the repository
root `AGENTS.md`. Language-level C# decisions live in the path declared as
**<csharp-coding-standard>**. Fetch those with `docs_get_coding_standard_for_technology`
before the references below, naming each technology and the topic you need; read a file only
when the tool is missing — this skill is procedure, not policy.

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Unit Test Patterns | xUnit, NSubstitute, FluentAssertions, pure domain/service/validator logic tests | [Unit Test Patterns](./references/unit-test-patterns.md) |
| Integration Test Patterns | Real SQL Server, LocalDB/Docker, WebApplicationFactory, repository and API tests | [Integration Test Patterns](./references/integration-test-patterns.md) |
| E2E / Browser Tests | Playwright MCP tools, user flows, form submissions, multi-step scenarios | [E2E Test Patterns](./references/e2e-test-patterns.md) |
| Rust and Go Test Patterns | `#[cfg(test)]` modules, `tests/` integration crates, `cargo nextest`, table-driven `_test.go` tests, `t.Run` subtests, `-race`, fakes behind traits and interfaces | [Rust and Go Test Patterns](./references/rust-go-test-patterns.md) |
| Mock Usage Analysis | Classify mocks as dead/unreachable/redundant/used; detect over-mocking | [Mock Usage Analysis](./references/mock-usage-analysis.md) |
| Test Maintainability | Magic values, duplicate setup, assertion overload, brittle naming — detect and refactor | [Test Maintainability](./references/test-maintainability.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.

## Execution & Parallelization Invariants

- **Build once, test many with direct assembly runner:** Do not rebuild or recompile the entire solution on every iteration. The direct assembly runner is the language-specific form for a .NET solution — its exact assembly path and target framework come from the host's test standard, in its `## Completion` section — while other stacks use their own runners (such as `cargo test` or `go test`). Use `--no-build` for targeted test runs, or execute the pre-built test assembly directly:
  `dotnet <path-to-test-assembly>.dll -class "<Namespace>.<TestClass>"`
- **Per-task artifact isolation:** Isolate intermediate build and review artifacts using unique per-task paths, passing `dotnet build --artifacts-path .agent_scratchpad/<task-id>/artifacts` for build output isolation, and `kyber-weave review gates . --agent <agent> --out .agent_scratchpad/<task-id>/task-gate.json` so concurrent task-gate runs never overwrite one another.

## Completion Digest

The `Discovery: codegraph N, docs N, direct reads N` header precedes the 6-field completion digest schema:

```text
STATUS: PASS | FAIL | ESCALATION
FILES: <comma-separated-paths>
TESTS: total=N failed=N passed=N
COMMIT: <commit-sha | NONE>
REMOTE_HEAD: <remote-head-sha | UNPUSHED>
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=test-dev gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
```
