---
id: todo/agent-spec-broken-reference-rule
title: KW-AGENT-SPEC-004 is documented but never emitted
doc-type: todo
status: current
component: ContextHygiene
owner: dpalfery
last-reviewed: 2026-08-22
---

# KW-AGENT-SPEC-004 is documented but never emitted

Found 2026-08-22 while clearing the analyzer backlog in
[the duplication-lenses plan](../archive/plans/2026-08-21-duplication-and-prior-art-lenses.md).

[`rule-reference.md`](../ci-pipelines/rule-reference.md) documents `KW-AGENT-SPEC-004` as
"Broken file reference". `AgentSpecValidator` declared a `RuleBrokenReference` constant for it
and never used it: nothing in the validator checks references, and no diagnostic with that id
is ever raised. The rule reference therefore describes a validation the product does not
perform — the least detectable kind of documentation defect, because the doc is the only place
anyone would look to find out whether the check exists.

The unused constant has been deleted. The documented rule row has not, because deleting it and
implementing it are different decisions:

- **Implement it.** `AgentSpecValidator.Validate` gains a check that file references inside an
  agent's instruction body resolve. That is the behaviour the doc already promises.
- **Withdraw it.** Remove the row from the rule reference, and accept that agent specs are not
  checked for broken references.

Whoever picks this up should also check whether the other `KW-AGENT-SPEC-*` and
`KW-AGENT-SEC-*` ids in the rule reference are all actually emitted. `KW-AGENT-SEC-003` was in
the same shape — a constant in `AgentPromptScanner` that nothing used — though in that case the
rule *is* emitted, from `InstructionSurfaceRuleCodes`, so only the duplicate constant was dead.
