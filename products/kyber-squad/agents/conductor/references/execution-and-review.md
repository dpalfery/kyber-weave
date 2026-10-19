# Execution and Review

This contract is shared by Ready plans and Ready specification task artifacts.

## Ready queue

A task is ready only when its declared dependencies are complete, its file or symbol scope does not overlap work in flight, and its development-mode gate is satisfied. Launch every ready task immediately with no concurrency cap; when file scope is uncertain, favor parallelism and accept the conflict risk. Re-evaluate the queue after every completion. Component labels and table order are not barriers.

Track one implementation owner per task. Rework uses the same queue and the same dependency and scope rules. Each dispatch carries a self-contained task packet that defines authorized scope; a plan or spec path in the packet is routing metadata marked not for reading.

## Task session continuity

Record the task id, owning specialist type, `worker-session-id`, and separate `reviewer-session-id` on the execution artifact. Store actual handles returned by the harness; when none is available, record that fact and the reason rather than inventing one. Keep a recovery brief containing the task packet, completion evidence, reviewer findings, decisions, failed approaches, and persisted failure-cluster tallies.

Reuse the original worker session for implementation, review fixes, verification, and any final-council findings in its scope. Use the harness's continuation or follow-up operation with the recorded handle. Append the findings and current scope/evidence updates without rewriting earlier conversation history. A fresh invocation of the same specialist type is a replacement, not continuation. Keep the task session available until its audits and any run-level findings are settled; do not repurpose it for an unrelated task while it awaits review.

Start a replacement only when continuation is unavailable, the original session cannot be recovered, or its context is unreliable (for example, it repeatedly acts on superseded requirements after correction). Record the replacement reason, pass the recovery brief, and update the handle. A busy session is queued, not replaced merely to avoid waiting. Separate unrelated tasks use separate sessions.

Discover continuation support from the current harness's exposed tools; do not assume it from the model, role name, or renderer. VS Code's stateless `runSubagent` path requires recovery briefs; resumable harnesses use their native session handles. Retaining a conversation supports context reuse but does not guarantee a provider cache hit. Avoid keeping a polluted context merely to preserve cached tokens; correctness takes priority.

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

Invoke `task-reviewer` after each worker completion with the mode, pass number, task contract, acceptance criteria, completion digest, current diff, and evidence. Keep the reviewer independent: it receives review evidence, not the implementation conversation. Reuse its separate reviewer session for passes 2 and 3 when supported, supplying prior findings and the current diff and evidence on every pass. A replacement reviewer receives the same review history. The implementation owner waits for its task's audit; other task sessions may continue with non-overlapping ready work.

- `PASS` completes the task's audit but does not authorize merge.
- `FAIL` on pass 1 or pass 2 creates a self-contained rework item for the original worker session. Use the replacement policy above only when needed. Re-run the relevant contract against the current tree before the next audit; remembered results are not current evidence.
- `FAIL` on pass 3, or any `ESCALATION: end-of-run`, enters the run's findings collection. There is no pass 4.

The reviewer requires matching Test-contract and RED/GREEN evidence only in test-first mode. In standard mode it requires the approved verification contract and current evidence.

## Iteration circuit-breaker and loop detection

The conductor enforces an iteration circuit-breaker to halt thrashing test-fix loops across rework cycles.

A **failure cluster** is keyed by the failing test ID first observed for that cluster, recorded on the execution artifact when the cluster is created (when no test IDs exist, the failing subsystem, job, or step label). A newly failing test joins the recorded cluster whose key it most recently co-failed with; if it co-fails with none, or with more than one, it forms a new cluster keyed by itself. Every resumed or replacement worker receives the recorded key; it does not re-derive one from whatever is failing now. Distinct keys remain distinct clusters for A/B oscillation detection.

The per-cluster **dispatch tally** lives on the run's persisted execution artifact (or the task artifact when the run has not yet written one), independently of session lifetime. Each time the conductor dispatches rework for a failure cluster, it reads that tally, increments it, and writes it back. Resuming a worker counts as a rework dispatch just as starting a replacement does. Re-evaluating the queue reads the tally; it never increments it. A tally at or above the cluster limit trips the breaker before the dispatch, not after it. Resumption, replacement, and compaction never reset this tally or oscillation history. The worker's inner 3-iteration cap applies to each implementation or rework dispatch, not the lifetime of the retained session.

- **Cluster-level retry limit:** Maximum of 2 rework dispatches for the same failing fixture or subsystem failure cluster across the run. If a failure cluster persists across 2 rework attempts, the circuit-breaker trips on that cluster.
- **Oscillation detection:** If a fix for Failure Cluster A causes regression in Failure Cluster B, and a fix for B regresses A (or rework alternates between failure signatures), the loop detector trips immediately.
- **Circuit-breaker escalation:** When the circuit-breaker trips—or when a worker returns `STATUS: ESCALATION`—halt rework for that task immediately. Do not dispatch further workers for that failure cluster. Record the finding in the run's findings collection using the same `ESCALATION:` prefix as `ESCALATION: end-of-run`, with the key `ESCALATION: circuit-breaker`. Include `CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>` plus the affected failure cluster, contradictory invariants, blast radius, and the worker's `RECOMMENDED_ACTION`. Reject any other trigger token; do not invent a reason.
- **Architect mediation:** Tripped circuit-breakers must not be ignored or bypassed. When the queue drains to the findings collection, `architect` investigates the failure cluster, assesses whether coupled test fixtures assert conflicting invariants or legacy details, and authors an intake recommendation or Draft plan to resolve the architectural conflict.

## Findings and final council

When the ready queue is empty and every task has left the ladder, drain a non-empty findings collection through `architect`. Any resulting Draft plan goes through its normal user approval gate before execution.

With the collection empty, dispatch `code-reviewer` exactly once over the accumulated run. `REQUEST_CHANGES` follows the repository's bounded remediation loop; `NEEDS_HUMAN` is terminal in supervised mode; in autonomous mode follow the [autonomy contract](autonomy.md). Do not commit, push, publish, or open a pull request until the council returns `APPROVE` and all required contract evidence is green.

## Closeout

After approval, assign `docs-dev` the closeout named by the execution artifact:

- Plan-backed work migrates durable facts, synchronizes the plan index, and archives the plan.
- Spec-backed work verifies requirements, migrates durable facts, synchronizes the specification index, and archives the specification.

Only a successful closeout completes the objective.
