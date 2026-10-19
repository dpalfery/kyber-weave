# Autonomous Decisions

Applies only when the conductor's packet carries `autonomy: autonomous`. In `supervised` mode this reference does not apply and the headless phase contract is unchanged.

Persist the packet's `autonomy` in the task artifact next to `development-mode`. In autonomous mode, never ask a question just to ask: when there is a recommendation and the checks below do not force an escalation, decide, record it in the artifact, and keep `OPEN_QUESTIONS` for escalations only.

## Three checks per open decision

1. **Covered?** Look in the specification, repository documentation, ADRs, existing patterns, and the Config Reg; then authoritative external sources (Microsoft Learn through MCP, vendor documentation, RFCs) through `research-agent`. If any gives a clear answer, decide and cite the source.
2. **Cost of being wrong**, when nothing covers it:
   - cheap to reverse, or local to one task: decide, record, and mark it `autonomous: low-confidence, no authoritative source`;
   - expensive to reverse (data-model or persistence shape, a public interface or contract, a dependency choice that spreads, a choice between architectures, anything whose wrong answer wastes the run): escalate.
3. **Conflict.** A choice that conflicts with an already-accepted decision or a documented constraint, including an approved earlier phase: escalate.

## Escalation

Return `READY_FOR_REVIEW`, `REQUIREMENTS_GAP`, or `DESIGN_GAP` with `ESCALATION: impactful`, listing each escalated item in `OPEN_QUESTIONS`. Every field below is required; the conductor sends back an escalation missing any:

```text
STATUS: READY_FOR_REVIEW | REQUIREMENTS_GAP | DESIGN_GAP
PHASE: requirements | design | tasks
ARTIFACT: <path resolved from <specification-index>>
SUMMARY: <concise persisted outcome>
GAPS: <blocking gaps, or none>
ESCALATION: impactful
OPEN_QUESTIONS: [Qn] <decision> — OPTIONS: <a> / <b>
SOURCES_SEARCHED: <what was searched and what it said>
COST_OF_WRONG: <why it is expensive to reverse, or why the run is wasted>
RECOMMENDED: <one option> — <reason>
Discovery: codegraph N, docs N, direct reads N
DIRECT_READ_REASONS: <path and reason for each direct read of indexed code, or none>
```

Batch escalations in the usual group of up to four. A phase with no escalation returns its normal digest with `OPEN_QUESTIONS: none`.
