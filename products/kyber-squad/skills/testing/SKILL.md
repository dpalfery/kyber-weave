---
name: testing
description: Use when writing or running the tests for a task, in any language — the one command that runs a task's tests and records its evidence, the test-first sequence, what a good test asserts, unit or integration, test data and parallel safety, and where this repository's own test conventions are. Do not use for reviewing a whole branch's or pull request's tests; that is the code-review skill.
license: MIT
---

# Testing

How to write and run tests in a Kyber-Squad task. Policy for this repository — runners, fixtures,
naming — is in its test standard, not here; this skill is the procedure every worker shares.

## First: this repository's test conventions, in one call

Before opening any test file, call `docs_get_coding_standard_for_technology` once with technology
`test`. The test standard is where the repository records how its tests work: the test projects
and the command that runs them, fixtures and collections, how integration tests get a database,
which seeded reference rows are safe to rely on, naming, and anything parallel runs must not
share. Read existing tests only for what that answer leaves open, and start from the test class
your task's Test contract names. Language-level rules come from your language's standard
(`csharp`, `typescript`, and so on). When the tool is missing, read the file declared as
**<test-coding-standard>** in the root `AGENTS.md`. A draft or missing standard is guidance,
never a stop: use a draft as the best available guidance, otherwise follow the existing tests'
pattern, and name the draft standard you relied on (`standard: draft`, or `missing`) in your report.

## Run your tests: one command

```bash
kyber-weave review gates . --agent <your agent name> --task <task id>
```

Your packet's **Test command** section prints it with both values filled in. It runs your task
gates: the build, then the tests your task covers (the classes the Test contract names, else the
classes that cover your files), and the format and lint checks. It prints each test gate in a few
lines — pass or fail, the totals, each failing test with the first line of its error, or each
compile error — and the path of the full log, and writes the report that is your evidence. Use it
for every run, including while you iterate: do not run the raw test command to see what failed,
and read the log only when the short form does not say enough.

Never run the whole test suite. The review council and CI run it on the integrated head.

## Test-first, one worker

1. Write the tests the task's Test contract names, and nothing else yet. No stub to make them compile.
2. Run the command once with `--red`. It passes when tests ran and failed, or when the build failed
   only on symbols your own files have not defined yet — a compile failure counts as the failing
   run. `RED_REJECTED: <reason>` means it is not one: fix what it names and run `--red` again.
3. Implement, then run the command without `--red` until it passes. The two reports, failing run
   first, are the proof the Arbiter reads; the passing run prints the digest to return.

In standard mode, write the tests with the change and run the command without `--red`. When a run
keeps failing, the `task-gate` skill has the cycle cap and the escalation.

## What a good test asserts

- **It fails without the change.** New behaviour ships with the test that would fail without it;
  a bug fix ships with a regression test named for the scenario that was broken.
- **Observable outcomes:** the returned value, the state left behind, the exact error raised. Not
  internal calls, private state or call counts, and never only "it did not throw".
- **A defect covers its class.** A fix for a reported defect adds cases for at least three other
  inputs of the same class, not only the one reported.
- **Never weaken an assertion to make it pass**, and never change production code to satisfy a test
  that contradicts the approved design — escalate the contradiction.
- No getter- or setter-only tests to move coverage, no sleeps or fixed delays (await, or poll with a
  timeout), no assertion that compares a value with itself.

## Unit or integration

| The code under test | Test kind | Infrastructure |
|---|---|---|
| Pure logic: domain rules, calculations, validation | Unit | None |
| Services that call other components | Unit | Fakes at the boundary they call |
| Persistence: repositories, queries, migrations | Integration | A real database, never a mocked one |
| An API or host end to end in-process | Integration | The in-process host plus a real database |
| A user flow across the whole stack | End-to-end | Only where the plan asks for it |

Mock only at an I/O boundary (network, clock, file system, external service), never inside the
logic under test. The test standard names the runner, mocking and fixture tools.

## Test data and parallel safety

Other workers run their tests at the same time as yours, against the same databases and folders.

- **Arrange your own data.** Never rely on rows another test creates. Use only the seeded reference
  rows the test standard lists as safe.
- **Make it unique.** Give every record a per-test prefix or a generated id, never a fixed id another
  test might claim.
- **Clean up.** Remove what the test created, or run it inside a transaction that is rolled back.
- **Scope anything shared to your task:** a database name, port, temporary directory or fixture
  file gets your task id or a generated name.
- **No order dependence** and no shared mutable static state between tests.

## What the test-adequacy review checks

The review council reads your tests as an outsider: would each fail with the change reverted; does
it assert behaviour rather than the implementation beside it; which edge and error cases the
change introduces are not exercised; does a bug fix have a test that was red first; does any test
exist only to move a coverage figure; can any assertion never fail. Write the tests to pass that
read.

## References — load only the one you need

| Need | Reference |
|---|---|
| .NET and Python unit tests: arrange/act/assert, fakes, builders | [Unit test patterns](./references/unit-test-patterns.md) |
| Real database, in-process API host, repository tests | [Integration test patterns](./references/integration-test-patterns.md) |
| Rust and Go: test modules, table-driven cases, fakes behind traits and interfaces | [Rust and Go test patterns](./references/rust-go-test-patterns.md) |
| Browser flows through the Playwright tools | [End-to-end test patterns](./references/e2e-test-patterns.md) |
| Mocks that are dead, unreachable, redundant or excessive | [Mock usage analysis](./references/mock-usage-analysis.md) |
| Magic values, duplicate setup, assertion overload, brittle names | [Test maintainability](./references/test-maintainability.md) |

TypeScript test conventions (runner, fakes, time, temporary files) are in the test standard;
language skills keep only their build and lint commands and point here for tests.
