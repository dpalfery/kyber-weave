---
schema: kyber-squad.migration/v1
agent: conductor
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .opencode/agents/conductor-v2.md
sources:
  .github/agents/conductor-v2.agent.md: e82cce130d47eba57ffb1aa1a9b53592e78879ff40f9743158359530375b7ef9
  .opencode/agents/conductor-v2.md: 681ecbd25ebf61ed3d0550bc1f4e3e50639d7c155db1003aaa5b31a64d600180
final-body-sha256: fa25ba977c6a1e56709d505e0834326292dad0cd7bb3110bc4373c6df359e9a9
---
# conductor migration

## Baseline and reconciliation

The canonical body starts from .opencode/agents/conductor-v2.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. The canonical agent body is authoritative for both native-primary rendering and the existing same-name role-skill fallback identity.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The orchestrator profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.search=deny, filesystem.write=deny, process.execute=deny, network.read=deny, network.publish=deny, delegate=allow. Explicit denials remain deny.

The migration originally recorded filesystem.read=ask to stand for the baseline's scoped read. That proved unrepresentable: a target whose permission model is a binary tool allow-list narrows ask to deny, which left the orchestrator unable to open the plan it exists to sequence. The scoped grant is now carried by filesystem.read=allow paired with filesystem.search=deny — the role may open a document it is pointed at, but never sweep the tree for one — and the folder restriction is stated in the instruction body, since no target expresses path scoping in its permission model.

The instruction body was revised after migration. The Authority section granted delegation by exception ("unless explicitly authorized", sole exception architect). Delegation is now a per-role grant carried by each agent's capability profile and its declared delegates-to, so the body states that rule instead of enumerating exceptions, and names the two roles that hold the grant: architect for discovery, and code-reviewer for its review council. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The instruction body was revised after migration, twice. First, the Authority section stopped granting delegation by exception and stated the rule that holds: delegation is a per-role grant carried by each agent's capability profile and its declared delegates-to. Then the architect profile gained that grant, so discovery is no longer mediated here — the conductor fulfils a discovery request only as a fallback where the harness does not let a subagent delegate. Added in the same pass: a mandatory-precedence block and hard stops that state the orchestration-only boundary as enforceable rules; a blocking approval gate that refuses to execute a plan still in Draft or otherwise unapproved; a fast path that begins orchestration immediately on an already-approved plan rather than routing it back to architect; and the rule that after approval the architect is an escalation path for a specific blocking conflict, not a routine stop. The orchestrator profile is unchanged, and every revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The instruction body was revised after migration again, to route per-task review to `task-reviewer` rather than `code-reviewer`. Section 4 now describes a three-pass ladder: two fast passes returning PASS or FAIL with a fix list, then one council pass, with findings surviving that pass tracked in a per-objective collection that routes through architect before the objective's council review. The orchestrator profile is unchanged — the collection is held in task state precisely because this role holds filesystem.write=deny — and delegates-to gained task-reviewer. The revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires. See ADR 0005.

The instruction body was revised after migration once more to close review-contract gaps: the ladder eligibility statement now names human-reserved and high-risk bypasses plus council-only escalations as council entries alongside a double fast-pass failure; the high-risk bypass list gained tenancy and revertibility; task-reviewer and rework payloads propagate the Test-contract row with RED/GREEN evidence verbatim; and "judgement" was normalized to "judgment" on the human-reserved path. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The instruction body was revised after migration again to gate every `task-reviewer` invocation on a Test-contract row with matching RED/GREEN evidence (missing evidence sequences `test-dev` or routes to `code-reviewer`), and to require both `APPROVE` and green contract tests before objective completion. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The instruction body was revised after migration again to align the objective code-reviewer loop with the three-cycle termination in `dp-code-reviewer`, and to treat `NEEDS_HUMAN` as a terminal human handoff that never enters the findings collection or architect drain. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The instruction body was revised after migration again to unify the task-level outcome contract: direct `code-reviewer` `REQUEST_CHANGES` findings join pass-3 residuals in the per-objective collection, completion is tracked through the ladder or a direct path, and every residual finding in the collection routes through architect before the objective review. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
