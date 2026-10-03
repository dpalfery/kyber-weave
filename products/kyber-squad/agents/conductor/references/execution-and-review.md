# Execution and Review

This contract is shared by Ready plans and Ready specification task artifacts.

## Ready queue

A task is ready only when its declared dependencies are complete, its file or symbol scope does not overlap work in flight, and its development-mode gate is satisfied. Launch every ready task immediately up to the artifact's concurrency bound. Re-evaluate the queue after every completion. Component labels and table order are not barriers.

Track one cold invocation per unit of work. Rework uses the same queue and the same dependency and scope rules.

## Test-first mode

`test-first` is the default. Every implementation task carries a Test contract naming the test surface, runner, and observable behavior.

1. **RED:** `test-dev` authors or identifies the contract test and records a failing run for the intended missing behavior. No implementation begins before valid RED evidence exists.
2. **GREEN:** the implementation specialist makes that same contract pass without weakening it and records current passing evidence.
3. **REFACTOR:** cleanup and task review preserve GREEN.

If changing requirements would require weakening or replacing the approved Test contract, return the artifact to its author and reopen approval for that contract.

## Standard mode

`standard` is an explicit opt-out from historical RED evidence, not an opt-out from tests. Each implementation task carries an approved verification contract naming the automated tests and other checks that prove its acceptance criteria.

1. **Implementation:** the specialist delivers the scoped change.
2. **Verification:** the specialist runs the approved automated tests and checks against the current tree.
3. **Review:** task audit and final council use that current evidence.

Changing the verification contract after approval reopens its approval gate.

## Three-pass task audit

Invoke `task-reviewer` after each worker completion with the mode, pass number, task contract, acceptance criteria, completion digest, current diff, and evidence. A worker may continue with other ready work while the audit runs.

- `PASS` completes the task's audit but does not authorize merge.
- `FAIL` on pass 1 or pass 2 creates a self-contained rework item for any available worker of the owning specialist type. Re-run the relevant contract before the next audit.
- `FAIL` on pass 3, or any `ESCALATION: end-of-run`, enters the run's findings collection. There is no pass 4.

The reviewer requires matching Test-contract and RED/GREEN evidence only in test-first mode. In standard mode it requires the approved verification contract and current evidence.

## Iteration circuit-breaker and thrash prevention

Prevent runaway test-fix thrash loops across workers and rework cycles:

- **Enforced retry threshold per failure cluster:** Track rework attempts per failing test fixture and subsystem failure cluster. A failure cluster is a group of failing tests sharing a fixture, namespace, or error signature. Limit retries to at most two attempts (three total runs) against the same failing fixture or failure cluster before tripping the circuit-breaker.
- **Oscillation detection:** If resolving failure cluster A introduces or re-activates failure cluster B, and subsequent work on B causes A to fail again, detect thrash oscillation and trip the circuit-breaker immediately on recurrence.
- **Conflicting invariants halt and escalation:** When an implementation change breaks a test fixture that asserts legacy internals, transient internal states, or contradictory requirements against the approved design, workers must not contort the implementation to satisfy mutually incompatible invariants. The worker must halt and emit `STATUS: CONFLICTING_INVARIANTS` or `ESCALATION: conflicting-test-fixture`. Conductor immediately pulls the task and enters it into the findings collection for architectural reconciliation.
- **JEV blast-radius checkpoints:** Evaluate deterministic Judgment/Execution Verification (JEV) blast-radius checkpoints at every worker completion and before task review:
  1. *File scope:* Edits must remain strictly within the task's declared file scope.
  2. *Architectural layers:* Changes must not cross into unapproved architectural layers or external subsystems.
  3. *Scope creep limit:* For bounded defects or single-task plans, touching more than three files or introducing unplanned cross-subsystem edits trips the circuit-breaker immediately.
- **Escalation path:** When the circuit-breaker trips (retry limit reached, oscillation detected, conflicting invariants reported, or blast radius exceeded), halt all further rework on the task, log the trip reason, failure cluster, diff, and evidence packet, and route directly to the findings collection for `architect` resolution.

## Findings and final council

When the ready queue is empty and every task has left the ladder, drain a non-empty findings collection through `architect`. Any resulting Draft plan goes through its normal user approval gate before execution.

With the collection empty, dispatch `code-reviewer` exactly once over the accumulated run. `REQUEST_CHANGES` follows the repository's bounded remediation loop; `NEEDS_HUMAN` is terminal. Do not commit, push, publish, or open a pull request until the council returns `APPROVE` and all required contract evidence is green.

## Closeout

After approval, assign `docs-dev` the closeout named by the execution artifact:

- Plan-backed work migrates durable facts, synchronizes the plan index, and archives the plan.
- Spec-backed work verifies requirements, migrates durable facts, synchronizes the specification index, and archives the specification.

Only a successful closeout completes the objective.
