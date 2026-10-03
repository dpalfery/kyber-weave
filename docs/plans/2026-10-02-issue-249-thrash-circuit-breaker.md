---
id: plans/2026-10-02-issue-249-thrash-circuit-breaker
title: "Iteration circuit-breaker for conductor and delegating harnesses"
doc-type: plan
status: ready
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
---

# Iteration circuit-breaker for conductor and delegating harnesses

## Status

Draft authored 2026-10-02. Decision-complete. Pre-approved for execution via intake directive ("Hal approves").

## Problem and goal

During defect resolution or refactoring tasks (such as migrating global Squad deployment state from caller repo-bound paths to user-scoped directories in issue #249), specialized developer subagents (`csharp-dev`, `test-dev`, `github-devops`) can enter runaway test-fix-verify thrash loops. Subagents execute repeated incremental iterations for dozens of turns without converging.

Per GitHub issue [#249](https://github.com/dpalfery/kyber-weave/issues/249), the root causes are:
1. **Lack of loop detection / iteration cap:** No enforced threshold exists on retry attempts for the same failing test fixture or subsystem failure cluster. When a fix in one area (e.g. lease contention) causes a regression in another (e.g. directory topology snapshots), agents bounce back and forth rather than halting to escalate.
2. **Scope creep & cascading test coupling:** Initial defects bounded to a specific component expose deeply coupled unit test fixtures asserting legacy internal implementation details (exact relative directory paths during rollbacks, duplicate lease keys, stale test helper assertions). Rather than recognizing conflicting invariants, subagents contort implementations trying to satisfy contradictory requirements simultaneously.
3. **Missing guardrails & JEV blast-radius checkpoints:** Agents lack deterministic Judgment/Execution Verification (JEV) checkpoints to assess whether the blast radius of incremental fixes has expanded beyond the intake scope.

The goal is to design and implement an iteration circuit-breaker across conductor, delegating harnesses (such as `bug-crusher`), task review, worker agents, and test standards:
- **Enforced retry threshold per fixture/failure cluster:** Cap retries on the same test fixture or failure cluster to 2 attempts (3 total runs), with immediate tripping on failure oscillation (A -> B -> A).
- **Halt and escalation path on conflicting invariants:** Explicitly forbid agents from contorting code to satisfy legacy internal test assertions that conflict with the intended design. Require immediate halt and structured escalation to `architect`.
- **Deterministic JEV blast-radius checkpoint:** Check at worker completion and task-reviewer audit whether touched files or architectural layers exceed the authorized scope or blast-radius limits (>3 files or cross-layer leakage for bounded tasks).
- **Standards and contract governance:** Update `<test-coding-standard>` in docs and Squad templates, canonical agent definitions, references, skills, and .NET verification tests.

## Intake assessment

This change is routed via the **PLAN** path. The work affects Kyber-Squad orchestration contracts, worker agent guidance, review invariants, test standards, and .NET content assertions. The blast radius is governed by the Squad canonical source and test suite.

## Development mode

`test-first` (default). Red tests in `SquadCanonicalContentTests` assert the circuit-breaker contract, failure cluster caps, conflicting invariants protocol, and blast-radius checkpoints before canonical source and standards are updated.

## Decisions

- **D1 (Retry threshold & oscillation detection):** Enforce a hard cap of 2 retry attempts (3 total attempts) per failing test fixture or failure cluster. If a fix alternates failures between two fixtures/clusters (oscillation / thrash pattern), the circuit breaker trips immediately on detecting recurrence.
  - *Status:* Approved (Hal approves).
- **D2 (Conflicting test invariants protocol):** When an agent discovers that a test failure is caused by an existing test fixture asserting legacy internals, transient internal state, or conflicting invariants with the intended design, the agent is strictly forbidden from contorting the implementation. The agent must halt immediately and emit `STATUS: CONFLICTING_INVARIANTS` / `ESCALATION: conflicting-test-fixture` naming the coupled fixture and contradictory invariants for architectural reconciliation.
  - *Status:* Approved (Hal approves).
- **D3 (JEV blast-radius checkpoints):** Introduce deterministic blast-radius verification at worker completion and task-reviewer audit. For bounded tasks or bug fixes, touching files outside the declared task scope or exceeding blast-radius thresholds (e.g. >3 files or unapproved cross-subsystem boundaries) trips the circuit breaker immediately.
  - *Status:* Approved (Hal approves).
- **D4 (Governed surfaces & .NET test coverage):**
  - Conductor: `products/kyber-squad/agents/conductor.md` and `products/kyber-squad/agents/conductor/references/execution-and-review.md`.
  - Task Reviewer: `products/kyber-squad/agents/task-reviewer.md`.
  - Delegating Harnesses: `products/kyber-squad/skills/bug-crusher/SKILL.md`.
  - Worker agents: `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/test-dev.md`, `products/kyber-squad/agents/github-devops.md`, and matching skills.
  - Standards: `products/kyber-squad/standards/test/README.md` and `docs/standards/test/README.md`.
  - Migration reports: `products/kyber-squad/migration/*.md` body digests synchronized.
  - Tests: `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` pins the new contracts and invariants.
  - *Status:* Approved (Hal approves).

## Implementation tasks

### T1: Test-first RED contract in .NET test suite
Add unit tests in `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` (or companion test class) asserting:
- Conductor contract defines the iteration circuit-breaker: failure cluster retry threshold, oscillation detection, conflicting invariant escalation, and JEV blast-radius checkpoints.
- Task-reviewer contract defines blast-radius checkpoints and detection of conflicting invariants / legacy assertions.
- Worker subagent contracts (`csharp-dev`, `test-dev`, `github-devops`) enforce halting on conflicting invariants rather than thrashing.
- Bug-crusher skill defines failure-cluster caps, oscillation tripwires, and blast-radius thresholds.
- Test coding standard forbids test coupling to legacy internals that creates contradictory invariants.
*Verification:* Run `dotnet test --filter SquadCanonicalContentTests` and observe failure (RED).

### T2: Conductor & execution-and-review contract updates
Update `products/kyber-squad/agents/conductor.md` and `products/kyber-squad/agents/conductor/references/execution-and-review.md`:
- Define failure clusters and the 2-retry cap (3 total runs).
- Define oscillation detection and immediate circuit-breaker tripping.
- Define the JEV blast-radius checkpoint prior to dispatching task review.
- Define the escalation path to `architect` when tripped.
- Update `products/kyber-squad/migration/conductor.md` `final-body-sha256`.

### T3: Task-reviewer and bug-crusher delegating harness updates
Update `products/kyber-squad/agents/task-reviewer.md` and `products/kyber-squad/skills/bug-crusher/SKILL.md`:
- Task-reviewer: Add JEV blast-radius verification to audit steps; audit whether diff expanded beyond task scope; check for conflicting invariant markers; emit `ESCALATION: circuit-breaker-tripped` or `ESCALATION: conflicting-test-fixture`.
- Bug-crusher: Add failure-cluster tracking, oscillation tripwire, and blast-radius limits to tripwires checklist and attempt budget.
- Update migration hashes where applicable.

### T4: Worker agents and test standards updates
Update `products/kyber-squad/agents/csharp-dev.md`, `test-dev.md`, `github-devops.md`, their skills, and `<test-coding-standard>` in `products/kyber-squad/standards/test/README.md` and `docs/standards/test/README.md`:
- Anti-thrash rule: halt on conflicting invariants / legacy test coupling; do not contort code to satisfy contradictory invariants.
- Blast-radius rule: reject unapproved file scope expansion.
- Update migration hashes in `products/kyber-squad/migration/`.

### T5: Green verification, documentation validation, and archival
- Verify `dotnet build KyberWeave.sln -c Release`.
- Verify `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build` (filtered by test classes).
- Verify `docs validate .` and `docs drift .`.
- Finalize and archive the plan to `docs/archive/plans/2026-10-02-issue-249-thrash-circuit-breaker.md` and update `docs/plans/README.md` (KW-DOC-LIFECYCLE-003).
- Verify `docs validate . --merge-ready`.
