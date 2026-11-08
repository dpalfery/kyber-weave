---
name: task-gate/rework
description: The failure loop a developer worker runs after a fix fails — the iteration cap, the three JEV checkpoints, and the escalation block to return when the circuit breaker trips.
---

# Rework: the failure loop

A fix that did not work is the normal case, not the exceptional one. What makes it safe is the
bound on it: how many times a worker may try, what ends the attempt, and what it returns when
the attempt is over. Those three rules are here, in the one reference the task gate loads after
a failed fix, so no agent body has to carry them.

Load this reference when the task gate or a test run reports a failure you have just tried to
fix, and again on every later attempt in the same dispatch.

## The iteration cap

**Maximum of 3 incremental test-fix iterations** against the same failing test fixture or
subsystem failure cluster within the current implementation or rework dispatch, whether it
resumes a session or starts a replacement. If the work is not green after 3 iterations, halt
immediately and trip the circuit breaker. Each new attempt is one iteration — a build error and a
test failure in the same attempt are one iteration, not two.

The conductor keeps a separate tally of rework dispatches per failure cluster, and that tally
survives a new session. This cap governs the iterations inside one dispatch; the conductor's cap
governs how many dispatches the cluster gets. Either can end the loop.

## The three JEV checkpoints

Check every iteration against these three. Any one of them ends the attempt on the spot; none of
them is a reason to try once more.

- **JEV Checkpoint 1 (Blast Radius Guardrail):** Confirm that every modified and proposed file is
  strictly within the authorized task scope. If resolving the failure requires expanding the
  blast radius into out-of-scope files or unrelated components, halt and trip
  `CIRCUIT_BREAKER_TRIGGER: BLAST_RADIUS_EXCEEDED`.
- **JEV Checkpoint 2 (Oscillation Tripwire):** A first one-way regression — fixing failure
  cluster A makes failure cluster B fail — consumes one of the 3 iterations. Trip
  `CIRCUIT_BREAKER_TRIGGER: THRASH_OSCILLATION_DETECTED` only when a later fix for B regresses A
  (A→B→A) or a failure signature repeats. One regression is a cost, not yet an oscillation.
- **JEV Checkpoint 3 (Invariant Contradiction):** If a failing test fixture asserts legacy
  internal implementation details, or requirements that contradict the task's approved design
  invariant, do not twist production code to satisfy the invalid invariant. Halt and trip
  `CIRCUIT_BREAKER_TRIGGER: INVARIANT_CONTRADICTION`.

## Never enter an unconstrained test-fix loop

Three iterations that do not converge, an oscillation, or a contradiction means escalate rather
than keep trying. A fourth attempt is the loop this section exists to stop: each one re-sends
the same context, spends the same time, and is no likelier to converge than the three before it.
Halting is the correct outcome, and the escalation below is how the work continues.

## The escalation

Halt the task and return this block in place of the completion digest:

```text
STATUS: ESCALATION
CIRCUIT_BREAKER_TRIGGER: <ITERATION_CAP_EXCEEDED | THRASH_OSCILLATION_DETECTED | INVARIANT_CONTRADICTION | BLAST_RADIUS_EXCEEDED>
FAILURE_CLUSTER: <failing fixture names or subsystem cluster>
CONTRADICTORY_INVARIANTS: <invariant A vs invariant B, or none>
BLAST_RADIUS: <files touched / attempted vs authorized scope>
RECOMMENDED_ACTION: <test fixture modernization | scope renegotiation | architect re-planning>
```

- **One trigger, from the closed set.** The token above names the reason the loop stopped, and
  no other token exists: `ITERATION_CAP_EXCEEDED` when the 3 iterations are spent,
  `THRASH_OSCILLATION_DETECTED` for A→B→A or a repeating signature,
  `INVARIANT_CONTRADICTION` for checkpoint 3, `BLAST_RADIUS_EXCEEDED` for checkpoint 1. Do not
  invent a reason, and do not send two.
- **Payload lines are required.** `FAILURE_CLUSTER` and `RECOMMENDED_ACTION` always.
  `CONTRADICTORY_INVARIANTS` when the trigger is `INVARIANT_CONTRADICTION`, and `BLAST_RADIUS`
  when it is `BLAST_RADIUS_EXCEEDED`. Write `none` in a payload line that does not apply; never
  omit the line.
- **The cluster is the key.** Name the failing fixtures, or the subsystem cluster when no test
  id exists, so the conductor can match the escalation to the tally and halt the same cluster
  rather than opening a new one.

The conductor halts rework for that failure cluster, records the escalation, and hands it to
`architect` for mediation. An escalation never completes the task and never clears its exit
gate: the task stays open until a later worker returns a passing `TASK_GATE:` line against the
current tree.
