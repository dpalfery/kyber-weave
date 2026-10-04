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

## Iteration circuit-breaker and loop detection

The conductor enforces an iteration circuit-breaker to halt thrashing test-fix loops across rework cycles.

A **failure cluster** is keyed by the failing test ID first observed for that cluster, recorded on the execution artifact when the cluster is created (when no test IDs exist, the failing subsystem, job, or step label). A newly failing test joins the recorded cluster whose key it most recently co-failed with; if it co-fails with none, or with more than one, it forms a new cluster keyed by itself. A cold invocation reads the recorded key; it does not re-derive one from whatever is failing now. Distinct keys remain distinct clusters for A/B oscillation detection.

Every worker invocation is cold and self-contained. The per-cluster **dispatch tally** therefore lives on the run's persisted execution artifact (or the task artifact when the run has not yet written one). Each time the conductor dispatches a rework worker for a failure cluster, it reads that tally, increments it, and writes it back. Re-evaluating the queue reads the tally; it never increments it. A tally at or above the cluster limit trips the breaker before the dispatch, not after it. A cold worker does not keep a private across-run counter; it receives the current tally with the artifact. The worker's inner 3-iteration cap is per invocation and does not persist.

- **Cluster-level retry limit:** Maximum of 2 rework dispatches for the same failing fixture or subsystem failure cluster across the run. If a failure cluster persists across 2 rework attempts, the circuit-breaker trips on that cluster.
- **Oscillation detection:** If a fix for Failure Cluster A causes regression in Failure Cluster B, and a fix for B regresses A (or rework alternates between failure signatures), the loop detector trips immediately.
- **Circuit-breaker escalation:** When the circuit-breaker trips—or when a worker returns `STATUS: ESCALATION`—halt rework for that task immediately. Do not dispatch further workers for that failure cluster. Record the finding in the run's findings collection using the same `ESCALATION:` prefix as `ESCALATION: end-of-run`, with the key `ESCALATION: circuit-breaker`. Include `CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>` plus the affected failure cluster, contradictory invariants, blast radius, and the worker's `RECOMMENDED_ACTION`. Reject any other trigger token; do not invent a reason.
- **Architect mediation:** Tripped circuit-breakers must not be ignored or bypassed. When the queue drains to the findings collection, `architect` investigates the failure cluster, assesses whether coupled test fixtures assert conflicting invariants or legacy details, and authors an intake recommendation or Draft plan to resolve the architectural conflict.

## Findings and final council

When the ready queue is empty and every task has left the ladder, drain a non-empty findings collection through `architect`. Any resulting Draft plan goes through its normal user approval gate before execution.

With the collection empty, dispatch `code-reviewer` exactly once over the accumulated run. `REQUEST_CHANGES` follows the repository's bounded remediation loop; `NEEDS_HUMAN` is terminal. Do not commit, push, publish, or open a pull request until the council returns `APPROVE` and all required contract evidence is green.

## Closeout

After approval, assign `docs-dev` the closeout named by the execution artifact:

- Plan-backed work migrates durable facts, synchronizes the plan index, and archives the plan.
- Spec-backed work verifies requirements, migrates durable facts, synchronizes the specification index, and archives the specification.

Only a successful closeout completes the objective.
