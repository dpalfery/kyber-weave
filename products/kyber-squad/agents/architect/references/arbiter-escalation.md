# Arbiter Escalation

The conductor dispatches you with `STATUS: ARBITER_ESCALATION` when the Arbiter
blocks a dispatch and `NEXT` names you as the escalation route. The envelope
carries the blocking rule, the plan digest, the task, and `REPEAT: 1` (or
`REPEAT: 2`, which means stop and record a run finding).

## Outcomes

Return exactly one of the three outcomes below. The choice is yours: neither
the Arbiter nor the conductor makes it.

1. `STATUS: ESCALATION_RESOLVED`, with corrected dispatch guidance inside the
   approved plan: a narrower scope, the right specialist, or a dependency to
   wait for. The user is not asked. An `ESCALATION_RESOLVED` cannot authorise
   a dispatch the rules block; it can only change the dispatch. Changing the
   policy is a Draft amendment to the plan or a host configuration change.
2. `STATUS: NEEDS_DECISION`, in the existing headless decision-protocol
   format, which the conductor relays to the user.
3. A Draft amendment to the plan (Req 15.3). Execution of the affected task
   then waits for the normal approval gate.

## Investigator dispatches

Every investigator dispatch you issue carries the marker `KYBER-ARBITER: true`,
so the hook can tell a planner investigation apart from other work. A blocked
planner dispatch is reported in your own result (`GAPS` or `STATUS: BLOCKED`)
and never retried unchanged; there is no escalation to yourself.

## Ledger loss and attestation (D31)

`READY-001` and `MODE-001` read completion and RED evidence from the ledger,
and `artifacts/` is not committed. A run resumed in a fresh clone, or after
the folder was cleaned, therefore escalates every task with a recorded
dependency. Recovery runs through this escalation path:

1. The escalation reaches you as usual.
2. Return `STATUS: NEEDS_DECISION`, asking the user to confirm which tasks
   are complete and which have RED evidence.
3. Once the user confirms, return `STATUS: ESCALATION_RESOLVED` carrying the
   line `ATTESTED: <task>=complete|red, …`.
4. The hook reads that line from the `delegate.planner.returned` output and
   records it under `returns.attested`. `READY-001` and `MODE-001` then
   count it.

Ask the user through `STATUS: NEEDS_DECISION` first; only then attest. No
agent writes the ledger.
