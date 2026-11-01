# Execution and Review

This contract is shared by Ready plans and Ready specification task artifacts.

## Ready queue

A task is ready only when its declared dependencies are complete, its file or symbol scope does not overlap work in flight, and its development-mode gate is satisfied. Dispatch ready tasks up to `review.max-concurrent-workers` (default 4, host-configurable in `.kyber-weave/kyber-weave.yml`); when file scope is uncertain, favor parallelism within the cap and accept the conflict risk. (Q4 reversal, issue #386: this supersedes the 2026-10-14 "no max concurrency" directive, which predated per-task artifacts-path isolation.) Re-evaluate the queue after every completion. Component labels and table order are not barriers.

Track one implementation owner per task. Rework uses the same queue and the same dependency and scope rules. Each dispatch carries a self-contained task packet that defines authorized scope: inlined entry-file table, acceptance criteria, dependencies, selected development mode, required contract, required skills, and pre-computed test commands. Keep plan and spec files out of the worker context: a plan or spec path in the packet is routing metadata marked not for reading; never instruct a worker to open the plan or spec to learn its task.

When dispatching concurrent workers up to `review.max-concurrent-workers`, isolate parallel workers using separate git worktree checkouts or isolated working contexts with dedicated per-task artifact paths (`dotnet build --artifacts-path .agent_scratchpad/<task-id>/artifacts` for compiler intermediates and `kyber-weave review gates` `--out artifacts/task-gate-<task-id>.json` for gate reports). Serialize final branch-head integration (cherry-pick or fast-forward merge onto the shared feature branch) on the main working tree before git push. If an integration conflict occurs, immediately abort the operation on the main working tree (`git cherry-pick --abort` or `git merge --abort`), record conflict details in the execution record, and dispatch a conflict-reconciliation rework dispatch. Guarantee worktree cleanup across all outcomes (`git worktree remove --force <path>` and `git worktree prune` across all exit paths: successful integration, failure, escalation, or cancellation).

Bundle same-role micro-tasks within a single dispatch where appropriate to minimize dispatch latency, while strictly preserving adversarial separation between RED test authorship and GREEN implementation. Never collapse RED and GREEN into a single worker dispatch: `test-dev` authors the failing test contract and proves it RED; the implementation specialist delivers GREEN and automated verification without weakening that contract, preserving the independent verification pass at task completion.

## Task session continuity

Record the task id, owning specialist type, and `worker-session-id` on the execution artifact. Store actual handles returned by the harness; when none is available, record that fact and the reason rather than inventing one. Keep a recovery brief containing the task packet, completion evidence, rejected gate results, decisions, failed approaches, and persisted failure-cluster tallies.

Reuse the original worker session for implementation, task-gate rework, verification, and any final-council findings in its scope. Use the harness's continuation or follow-up operation with the recorded handle. Append the findings and current scope/evidence updates without rewriting earlier conversation history. A fresh invocation of the same specialist type is a replacement, not continuation. Keep the task session available until its exit gate and any run-level findings are settled; do not repurpose it for an unrelated task while it awaits rework.

Start a replacement only when continuation is unavailable, the original session cannot be recovered, or its context is unreliable (for example, it repeatedly acts on superseded requirements after correction). Record the replacement reason, pass the recovery brief, and update the handle. A busy session is queued, not replaced merely to avoid waiting. Separate unrelated tasks use separate sessions.

Discover continuation support from the current harness's exposed tools; do not assume it from the model, role name, or renderer. VS Code's stateless `runSubagent` path requires recovery briefs; resumable harnesses use their native session handles. Retaining a conversation supports context reuse but does not guarantee a provider cache hit. Avoid keeping a polluted context merely to preserve cached tokens; correctness takes priority.

## Test-first mode

`test-first` is the default. Every implementation task carries a Test contract naming the test surface, runner, and observable behavior.

1. **RED:** `test-dev` authors or identifies the contract test and records a failing run for the intended missing behavior. No implementation begins before valid RED evidence exists. Never use `git stash` to record RED proof runs: zero-stash RED proofs are strictly mandated (forbid git stash). Proofs must run against a clean base checkout, throwaway copy, or isolated worktree.
2. **GREEN:** the implementation specialist makes that same contract pass without weakening it and records current passing evidence.
3. **REFACTOR:** cleanup and the developer exit gate preserve GREEN.

Follow the build once, test many pattern for rapid iteration: build or compile once, then execute targeted tests using the pre-computed direct assembly test runner:
`dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class "<TestClass>" -reporter quiet`
or `dotnet test --no-build` for fast loops. Reserve full test suite execution for the final review council, which runs it once on the integrated head. A worker's task end runs only the test classes its task touched, because workers finish together and concurrent full runs overload the machine. Name those classes in the task packet's runner command.

If changing requirements would require weakening or replacing the approved Test contract, return the artifact to its author and reopen approval for that contract.

## Standard mode

`standard` is an explicit opt-out from historical RED evidence, not an opt-out from tests. Each implementation task carries an approved verification contract naming the automated tests and other checks that prove its acceptance criteria.

1. **Implementation:** the specialist delivers the scoped change.
2. **Verification:** the specialist runs the approved automated tests and checks against the current tree.
3. **Gate:** the worker's exit gate and the final council use that current evidence.

Changing the verification contract after approval reopens its approval gate.

## Developer exit gate

Every developer worker ends its task by running the deterministic `task-gate` and reports the result in its completion digest. No agent audits a single task, and the conductor does not judge technical correctness.

Workers precede the six-field completion digest block with the `Discovery: codegraph N, docs N, direct reads N` header; this header is valid and is not rejected as a 7th digest field, and neither is any other line in the list below. The completion digest itself must follow this exact six-field schema, subject to the phased-rollout scope in the bullets below:

```text
STATUS: PASS | FAIL | ESCALATION
FILES: <comma-separated-paths>
TESTS: total=N failed=N passed=N
COMMIT: <commit-sha | NONE>
REMOTE_HEAD: <remote-head-sha | UNPUSHED>
TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE — agent=… gates=… failed=… skipped=… report=… runs=…
```

Worktree workers committing locally report their `<commit-sha>`, while uncommitted/unpushed task runs report `NONE` and `UNPUSHED`.

Lines of the explanatory message body that are not digest fields and are never counted or rejected as a seventh field are exactly these shapes: the `Discovery: codegraph N, docs N, direct reads N` header; `COVERAGE_GAPS:` followed by free text; and `Artifact:` or `Baseline:` followed by one repository-relative or absolute path. A `report=<path>` inside the `TASK_GATE:` line is part of that field, not a separate line.

- **Missing line or `FAIL`:** reject the digest, except a valid escalation under the circuit-breaker section below. Create a self-contained rework item for the original worker session (use the replacement policy above only when needed) naming the failed gate ids and the report path. The rework counts in the per-cluster tally below, keyed `task-gate/<gate-id>` for each failed gate, or `task-gate/missing` when the line is absent. After 2 rework dispatches for a key, the circuit-breaker trips on it. During the phased rollout only `csharp-dev` and `test-dev` must return the full six-field block; every other developer worker may return its standard completion, meaning the completion report its own agent body specifies, plus a `TASK_GATE:` line; the conductor's requirement for that line overrides the agent body, so a standard completion without it is rejected as `task-gate/missing`, and the `FILES`, `TESTS`, `COMMIT` and `REMOTE_HEAD` fields are not required of it. The missing-or-`FAIL` `TASK_GATE` rejection and its `task-gate/<gate-id>` / `task-gate/missing` tally apply to every developer worker.
- **`PASS`:** the task's gate is complete. It does not authorize merge.
- **`NONE_DECLARED`:** valid; the repository declares no gates for that agent.
- **`NONE_APPLICABLE`:** valid; gates are declared for that agent, but every one was skipped for this host or change, so none ran. Accept it as `NONE_DECLARED` is accepted. It is not a pass, and it never reads as `PASS`.
- **`UNAVAILABLE`:** the gate could not run. It is never a pass: record it in the findings collection with the worker's reason and continue.
- **Test-first ordering:** before dispatching GREEN, mechanically check that the RED digest for the task exists and records a failing run. This is a presence check on the digest, not an LLM audit; without it, return the task to `test-dev`.
- Re-run nothing from memory: rework ends with a fresh `TASK_GATE:` line against the current tree.

Other task sessions may continue with non-overlapping ready work while a task awaits rework.

## Iteration circuit-breaker and loop detection

The conductor enforces an iteration circuit-breaker to halt thrashing test-fix loops across rework cycles.

A **failure cluster** is keyed by the failing test ID first observed for that cluster, recorded on the execution artifact when the cluster is created (when no test IDs exist, the failing subsystem, job, or step label). A newly failing test joins the recorded cluster whose key it most recently co-failed with; if it co-fails with none, or with more than one, it forms a new cluster keyed by itself. Every resumed or replacement worker receives the recorded key; it does not re-derive one from whatever is failing now. Distinct keys remain distinct clusters for A/B oscillation detection.

The per-cluster **dispatch tally** lives on the run's persisted execution artifact (or the task artifact when the run has not yet written one), independently of session lifetime. Each time the conductor dispatches rework for a failure cluster, it reads that tally, increments it, and writes it back. Resuming a worker counts as a rework dispatch just as starting a replacement does. Re-evaluating the queue reads the tally; it never increments it. A tally at or above the cluster limit trips the breaker before the dispatch, not after it. Resumption, replacement, and compaction never reset this tally or oscillation history. The worker's inner 3-iteration cap applies to each implementation or rework dispatch, not the lifetime of the retained session.

- **Cluster-level retry limit:** Maximum of 2 rework dispatches for the same failing fixture or subsystem failure cluster across the run. If a failure cluster persists across 2 rework attempts, the circuit-breaker trips on that cluster.
- **Oscillation detection:** If a fix for Failure Cluster A causes regression in Failure Cluster B, and a fix for B regresses A (or rework alternates between failure signatures), the loop detector trips immediately.
- **Circuit-breaker escalation:** When the circuit-breaker trips—or when a worker returns a valid `STATUS: ESCALATION`—halt rework for that task immediately. Do not dispatch further workers for that failure cluster. A valid escalation is `STATUS: ESCALATION` with exactly one `CIRCUIT_BREAKER_TRIGGER:` token from the closed set (`ITERATION_CAP_EXCEEDED`, `THRASH_OSCILLATION_DETECTED`, `INVARIANT_CONTRADICTION`, `BLAST_RADIUS_EXCEEDED`) and its required payload lines (`FAILURE_CLUSTER` and `RECOMMENDED_ACTION` always, plus `CONTRADICTORY_INVARIANTS` for `INVARIANT_CONTRADICTION` and `BLAST_RADIUS` for `BLAST_RADIUS_EXCEEDED`); only a valid escalation is exempt from the `FILES`, `TESTS`, `COMMIT`, `REMOTE_HEAD` and `TASK_GATE` rules. A `STATUS: ESCALATION` without a valid `CIRCUIT_BREAKER_TRIGGER` is not an escalation: it is checked and rejected like any completion digest. A valid escalation wins the routing (halt rework, record `ESCALATION: circuit-breaker`, architect mediation) but never completes the task or clears its exit gate; a `TASK_GATE: FAIL` it carries is recorded in the finding with its gate ids, and the task stays open until the conductor, once architect mediation has resolved the cluster, dispatches a follow-up worker that returns a fresh passing `TASK_GATE:` line against the current tree; the halt bars dispatch only until that mediation completes. The conductor accepts a valid escalation's payload without rejection for missing completion fields or `TASK_GATE`. Record the finding in the run's findings collection with the key `ESCALATION: circuit-breaker`. Include `CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>` plus the affected failure cluster, the contradictory invariants and blast radius where the trigger carries them, and the worker's `RECOMMENDED_ACTION`. Reject any other trigger token; do not invent a reason.
- **Architect mediation:** Tripped circuit-breakers must not be ignored or bypassed. When the queue drains to the findings collection, `architect` investigates the failure cluster, assesses whether coupled test fixtures assert conflicting invariants or legacy details, and authors an intake recommendation or Draft plan to resolve the architectural conflict.

## Findings and final council

When the ready queue is empty and every task has cleared its exit gate, drain a non-empty findings collection through `architect`. Any resulting Draft plan goes through its normal user approval gate before execution.

With the collection empty, dispatch `code-reviewer` exactly once over the accumulated run. The draft pull request is pushed before the review council runs, so the council reviews a pull request that already exists. `REQUEST_CHANGES` follows the bounded auto-remediation loop below; `NEEDS_HUMAN` is terminal in supervised mode; in autonomous mode follow the [autonomy contract](autonomy.md). The verdict goes in the PR description and is not a conductor gate. The pull request is marked ready once required CI passes, and review happens after that (the autonomy contract sets the 60-minute CI wait).

### Bounded council remediation loop (Critical/Major only, per Q3)

When the council returns actionable Critical or Major findings fixable within the approved scope, the conductor routes each finding to its owning worker session for one bounded remediation pass before park: a single same session continuation in the same file scope, ending with a fresh task-gate re-run against the current tree so evidence is current instead of remembered. Remediation rework uses the same queue and scope rules as any rework; the rework dispatch counts in the per-cluster tally and the worker's inner iteration cap applies to the remediation pass.

Unactionable, out-of-scope, or second-failure findings park immediately with their finding ids in the parked list instead of earning another worker dispatch: Unactionable findings (no deterministic fix within scope), findings outside the authorized file scope, and any finding that survives the one bounded remediation pass and fails a second council review park immediately — stop reworking each one and list its finding ids in the PR. Park means exactly that; it never holds back delivery of unaffected work (see the autonomy contract).

Minor/Info findings never trigger remediation and never park: a Minor-only council reports its findings and moves on without a remediation loop and without parking the change or task.

The following verdict paths are not findings for this loop and bypass remediation entirely: KW-REVIEW-008 reserved-path escalation (always-human paths), size escalation over max-reviewable-lines (KW-REVIEW-009), and any blocking-gate failure (blocking-gate verdict rule 3, including docs validate, docs drift, build, test, and analyzers). Each keeps its escalation and REQUEST_CHANGES path as written; a size escalation is reported as pending human review and never cancels an in-flight remediation pass already dispatched.

## Closeout

After approval, assign `docs-dev` the closeout named by the execution artifact:

- Plan-backed work migrates durable facts, synchronizes the plan index, and archives the plan.
- Spec-backed work verifies requirements, migrates durable facts, synchronizes the specification index, and archives the specification.

### Documentation-impact review

Before closeout completes, run the documentation-impact review step:

- Keep a run record of asked/decided+why/files changed; no transcripts.
- Dispatch docs-dev with that run record.
- Require DOCS_REVIEW rows in the closeout evidence before proceeding.

### Closeout completion

Only a successful closeout completes the objective. Record Q2 CLOSEOUT_COMPLETE in the execution artifact and carry the outcome in the PR description.
