# Autonomous Decisions

Applies only when the packet carries `autonomy: autonomous`. In `supervised` mode this reference does not apply and the headless decision protocol is unchanged.

Persist the packet's `autonomy` in the plan next to `development-mode`. In autonomous mode, never ask a question just to ask: when there is a recommendation and the checks below do not force an escalation, decide.

## Three checks per open decision

1. **Covered?** Look in repository documentation, ADRs, existing patterns, and the Config Reg; then authoritative external sources (Microsoft Learn through MCP, vendor documentation, RFCs) through `research-agent`. If any gives a clear answer, decide and record the source in the row.
2. **Cost of being wrong**, when nothing covers it:
   - cheap to reverse, or local to one task: decide, record, and set status `autonomous: low-confidence, no authoritative source`;
   - expensive to reverse (data-model or persistence shape, a public interface or contract, a dependency choice that spreads, a choice between architectures, anything whose wrong answer wastes the run): escalate.
3. **Conflict.** A choice that conflicts with an already-accepted decision or a documented constraint: escalate.

Record the source in `Mitigating and supporting information`; the conductor sets the row's approval provenance.

## Escalation

Return `STATUS: NEEDS_DECISION` with `ESCALATION: impactful`. Every field below is required; the conductor sends back an escalation missing any:

```text
STATUS: NEEDS_DECISION
ESCALATION: impactful
PLAN_FILE: <saved path>
QUESTION: [Qn] <decision>
OPTIONS: <a> / <b> / ...
SOURCES_SEARCHED: <what was searched and what it said>
COST_OF_WRONG: <why it is expensive to reverse, or why the run is wasted>
RECOMMENDED: <one option> — <reason>
```

Batch escalations in the usual group of up to four. Non-escalated open decisions are decided and recorded, never returned as `NEEDS_DECISION`.
