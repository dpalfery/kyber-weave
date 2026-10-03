---
id: plans/2026-10-02-issue-249-subagent-iteration-circuit-breaker
title: "Kyber-Squad: Subagent Iteration Circuit-Breaker and JEV Checkpoints (#249)"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-02
development-mode: test-first
---

# Kyber-Squad: Subagent Iteration Circuit-Breaker and JEV Checkpoints (#249)

**Status: Complete, archived 2026-10-02.**  
**Date:** 2026-10-02  
**Development mode:** `test-first`  
**Goal:** Address GitHub issue [#249](https://github.com/dpalfery/kyber-weave/issues/249): establish an iteration circuit-breaker and Judgment/Execution Verification (JEV) checkpoints for the conductor and developer subagents (`csharp-dev`, `test-dev`, `github-devops`), preventing runaway test-fix loops, bounded retries per failure cluster, oscillation detection, and deterministic escalation.  
**Harvest:** No ADR is needed; decisions Q1–Q5 are recorded in this plan. Contracts live in canonical agent specifications (`products/kyber-squad/agents/conductor.md`, `csharp-dev.md`, `test-dev.md`, `github-devops.md`) and agent reference documentation (`products/kyber-squad/agents/conductor/references/execution-and-review.md`).

---

## 1. Problem and Scope

### 1.1 Context and Evidence

During defect resolution in Squad deployment state migration, developer subagents (`csharp-dev`, `test-dev`, `github-devops`) entered an unconstrained execution loop. Agents ran repeated incremental test-fix-verify iterations across dozens of turns without converging.

Root causes identified in issue [#249](https://github.com/dpalfery/kyber-weave/issues/249):
1. **Lack of Loop Detection / Iteration Cap:** The conductor and delegating harnesses lacked an enforced threshold on retry attempts for the same failing test fixture or subsystem failure cluster. When a fix in one area caused a slight regression in another, subagents repeatedly bounced back and forth rather than halting to escalate.
2. **Scope Creep & Cascading Test Coupling:** Resolving the initial defect exposed coupled unit test fixtures asserting legacy internal implementation details. Rather than recognizing contradictory invariants between intended design and legacy fixtures, subagents attempted to satisfy contradictory invariants simultaneously.
3. **Guardrails & Judgment/Execution Verification (JEV):** Subagents lacked deterministic guardrails or JEV checkpoints to assess whether the blast radius of incremental fixes expanded beyond intake scope. Without an explicit circuit breaker, execution cycles were consumed endlessly.

### 1.2 Scope Boundaries

- **In Scope:**
  - Conductor orchestration invariants and execution contract (`products/kyber-squad/agents/conductor.md`, `products/kyber-squad/agents/conductor/references/execution-and-review.md`): iteration circuit-breaker, failure-cluster retry threshold (max 2 rework dispatches per failure cluster), loop/oscillation detection, and escalation into conductor findings.
  - Specialized developer subagents (`csharp-dev`, `test-dev`, `github-devops`): inner loop iteration cap (max 3 incremental test-fix-verify iterations per failing fixture/cluster within an invocation), JEV checkpoint protocol (blast radius guardrail, oscillation tripwire, invariant contradiction detection), and structured `STATUS: ESCALATION` completion digest.
  - Golden contract and migration tracking: evolution registration in `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` and `products/kyber-squad/migration/` reports.
  - Automated tests verifying circuit-breaker contracts.
- **Out of Scope:**
  - Redesigning the conductor architecture or changing intake routing.
  - Adding new unapproved external packages or runtime dependencies.

---

## 2. Decisions & Resolution

### Decision Q1: Iteration Cap Thresholds (Inner Worker vs. Conductor Queue)
- **Question:** What are the exact iteration limits for inner subagent execution and conductor rework dispatch?
- **Resolution (Conservative / Repo-grounded):**
  - **Worker Inner Loop Cap:** Maximum of **3 incremental test-fix-verify attempts** against the same failing fixture or subsystem failure cluster within a single worker turn. If unresolved after 3 attempts, the worker must halt immediately and trip the circuit breaker.
  - **Conductor Cluster Rework Cap:** Maximum of **2 rework dispatches** for the same failing fixture or subsystem failure cluster across the entire delivery run. A 3rd failure of that cluster trips the run-level circuit breaker and halts dispatch for that task.

### Decision Q2: Thrash / Oscillation Detection
- **Question:** How is thrashing/oscillation between regressions detected?
- **Resolution (Deterministic):**
  - An oscillation is triggered when an attempt to fix Failure Cluster A causes previously passing Failure Cluster B to fail, or when fixes alternate between two sets of failures (A → B → A).
  - Both worker subagents (during inner loops) and the conductor (during queue re-evaluation) must track failure signatures. If an oscillation is observed, the circuit breaker trips immediately without exhausting remaining attempts.

### Decision Q3: Scope Creep & Invariant Contradictions
- **Question:** How should subagents handle deeply coupled test fixtures asserting legacy details that conflict with the approved task design?
- **Resolution:**
  - Subagents must not hack heuristics to satisfy contradictory invariants.
  - When a test failure is caused by a fixture asserting obsolete implementation details conflicting with the task objective, the worker must identify the contradiction, halt production code churn, and escalate with `CIRCUIT_BREAKER_TRIGGER: INVARIANT_CONTRADICTION`.

### Decision Q4: Judgment/Execution Verification (JEV) Checkpoints
- **Question:** What is the formal JEV checkpoint protocol?
- **Resolution:**
  - Every developer subagent executes a mandatory JEV check before and after each fix attempt:
    1. **Blast Radius Guardrail:** Are touched files strictly within authorized task scope? (Trips `BLAST_RADIUS_EXCEEDED` if out-of-scope files are required).
    2. **Oscillation Tripwire:** Did this change regress previously green fixtures? (Trips `THRASH_OSCILLATION_DETECTED`).
    3. **Invariant Consistency Check:** Does the failing fixture contradict the approved design or another fixture? (Trips `INVARIANT_CONTRADICTION`).
    4. **Iteration Cap:** Have 3 attempts on this failure cluster been reached? (Trips `ITERATION_CAP_EXCEEDED`).
  - Tripping any tripwire outputs `STATUS: ESCALATION` with failure cluster, contradictory invariants, blast radius, and recommended action.

### Decision Q5: Conductor Escalation Handling
- **Question:** How does the conductor handle a tripped circuit breaker?
- **Resolution:**
  - Conductor immediately halts rework for that task and records the escalation in the run's findings collection with `ESCALATION: circuit-breaker`.
  - Non-dependent queue tasks may continue, but the run cannot complete while an unresolved circuit-breaker finding exists.
  - At queue drain, findings are channeled to `architect` for re-planning or human escalation.

---

## 3. Technical Architecture & Component Changes

1. **`products/kyber-squad/agents/conductor.md`**:
   - In `## Shared lifecycle invariants`, add the iteration circuit-breaker invariant: bounded retries per failing fixture/cluster, oscillation detection, and mandatory escalation over thrashing.
2. **`products/kyber-squad/agents/conductor/references/execution-and-review.md`**:
   - Add `## Iteration circuit-breaker and loop detection` specifying the 2-rework cluster cap, oscillation detection, JEV escalation handling into the findings collection, and architect re-planning seam.
   - Retains exact 4-resource count for conductor.
3. **`products/kyber-squad/agents/csharp-dev.md`**:
   - Add JEV Checkpoints and Iteration Circuit-Breaker section in Workflow.
   - Update Hard Rules to forbid unconstrained test-fix thrashing and contradictory invariant hacking.
   - Update Completion Digest with `STATUS: ESCALATION` format.
4. **`products/kyber-squad/agents/test-dev.md`**:
   - Add JEV Checkpoints, iteration cap (max 3), and oscillation detection when updating test suites and running tests.
   - Forbid modifying test invariants to mask contradictory requirements.
   - Update Completion Digest with `STATUS: ESCALATION` format.
5. **`products/kyber-squad/agents/github-devops.md`**:
   - Add JEV Checkpoints and iteration circuit-breaker for CI workflow debugging and pipeline verification loops.
   - Update Completion Digest with `STATUS: ESCALATION` format.
6. **Golden Manifest & Migration Tracking**:
   - In `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs`: add `csharp-dev`, `test-dev`, and `github-devops` to `EvolvedAgentIdentities`.
   - Update `products/kyber-squad/migration/{csharp-dev,test-dev,github-devops}.md` `final-body-sha256` to match evolved LF-normalized instruction bodies.
7. **Automated Tests**:
   - In `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` (or a dedicated test class): add assertions verifying the circuit-breaker, JEV checkpoint, and escalation contracts in conductor and developer subagents.

---

## 4. Test-First Implementation Tasks

- [x] **T1 (RED): Author contract tests for circuit breaker and JEV contracts**
  - Add test methods in `tests/KyberWeave.Tests/` asserting that conductor and developer agents (`csharp-dev`, `test-dev`, `github-devops`) specify the iteration circuit-breaker, iteration cap, oscillation detection, JEV checkpoints, and `STATUS: ESCALATION` digest format.
  - Verify tests fail (RED) against current un-updated agent contracts.
- [x] **T2 (GREEN - Conductor): Implement circuit-breaker rules in conductor**
  - Update `products/kyber-squad/agents/conductor.md` and `products/kyber-squad/agents/conductor/references/execution-and-review.md` with cluster rework caps, oscillation detection, and circuit-breaker findings escalation.
- [x] **T3 (GREEN - Developer Subagents): Implement JEV checkpoints in developer subagents**
  - Update `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/test-dev.md`, and `products/kyber-squad/agents/github-devops.md` with JEV checkpoints, 3-iteration cap, oscillation tripwires, and escalation digests.
- [x] **T4 (GREEN - Golden and Migration Alignment): Update golden contract and migration records**
  - Update `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` (`EvolvedAgentIdentities`).
  - Update `products/kyber-squad/migration/{csharp-dev,test-dev,github-devops}.md` `final-body-sha256`.
  - Verify all unit and golden tests pass (GREEN).
- [x] **T5 (Verification & Quality Gates): Run solution build, tests, and documentation gates**
  - `/Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release`
  - `/Users/hal/.dotnet/dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build`
  - `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
  - `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`

---

## 5. Review and Archival Closeout

- Upon green verification of all tasks, move this plan to `docs/archive/plans/2026-10-02-issue-249-subagent-iteration-circuit-breaker.md`.
- Update `docs/plans/README.md` to reflect `Complete` status and archival date under Archived Plans.
- Verify `docs validate . --merge-ready` exits 0 with zero findings.

### Closeout & Harvest

- **Tasks:** T1–T5 verified and complete.
- **Harvest:** No ADR is required. Decisions Q1–Q5 are durably recorded in this plan. The operational rules and invariants are harvested into:
  - `products/kyber-squad/agents/conductor.md` (shared lifecycle invariant for iteration circuit-breaker)
  - `products/kyber-squad/agents/conductor/references/execution-and-review.md` (cluster rework retry cap of 2, oscillation detection, `STATUS: ESCALATION` intake into findings, architect mediation)
  - `products/kyber-squad/agents/csharp-dev.md`, `products/kyber-squad/agents/test-dev.md`, `products/kyber-squad/agents/github-devops.md` (JEV checkpoints 1–4, 3-iteration inner loop cap, oscillation tripwires, contradictory invariant escalation, and `STATUS: ESCALATION` completion digest)
  - `tests/KyberWeave.Tests/SquadCanonicalContentTests.cs` and `tests/KyberWeave.Tests/HotshotGoldenContractTests.cs` (automated regression contract tests and golden manifest tracking)
