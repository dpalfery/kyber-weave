---
name: test-dev
description: Use when writing xUnit unit tests, integration tests with real databases, E2E Playwright tests, or Rust (cargo test/nextest) and Go (go test) tests. Do not use for application or persistence implementation.
license: MIT
---

# Test Developer

Test policy lives in the path declared as **<test-coding-standard>** in the repository
root `AGENTS.md`. Language-level C# decisions live in the path declared as
**<csharp-coding-standard>**. Read those before the references below — this skill is
procedure, not policy.

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

- **Adversarial RED/GREEN role boundary:** Maintain strict adversarial separation between RED phase test authoring (`test-dev`) and GREEN phase implementation (`csharp-dev`, etc.). Never collapse RED and GREEN into a single worker dispatch: test authors author tests and verify failures; implementers make them pass, while preserving independent verification at task completion.
- **Same-role task bundling:** Tasks belonging to the same specialist role are bundled in same-role micro-task dispatches to minimize context thrash.
- **Build once, test many with direct assembly runner:** Do not rebuild or recompile the entire solution on every iteration. Use `--no-build` for targeted test runs, or execute the pre-built test assembly directly with quiet reporting:
  `dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "<TestClassName>" -reporter quiet`
- **Zero-stash RED proof rule:** Forbid `git stash` for RED proofs; never run `git stash` to fake clean working state. Run the RED proof against a clean base checkout, throwaway copy, or isolated worktree before any implementation edits exist.
- **Per-task artifact isolation:** Isolate intermediate build and review artifacts using unique per-task paths, passing `dotnet build --artifacts-path .agent_scratchpad/<task-id>/artifacts` for build output isolation, and `kyber-weave review gates . --agent <agent> --out artifacts/task-gate-<task-id>.json` (or `.agent_scratchpad/<task-id>/task-gate.json`) so concurrent task-gate runs never overwrite one another.

## Completion Digest

Every task concludes by reporting the explicit 6-field worker completion digest:

```text
STATUS: PASS | FAIL | ESCALATION
FILES: <comma-separated-paths>
TESTS: total=N failed=N passed=N
COMMIT: <commit-sha | NONE>
REMOTE_HEAD: <remote-head-sha | UNPUSHED>
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=test-dev gates=<n> failed=<ids|none> skipped=<n> report=<path> runs=<k>
```
