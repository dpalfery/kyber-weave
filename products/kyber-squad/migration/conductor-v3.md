---
schema: kyber-squad.migration/v1
agent: conductor-v3
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .opencode/agents/conductor-v3.md
sources:
  .opencode/agents/conductor-v3.md: 2696d45e25dcc3bbfc0f20445026d5f0213a59e783ba247bd59dc109b112e95c
final-body-sha256: 65fda2424cfe75c0979ece740ace69d2fc154cc4342ce50b38dc92a1fdb24bfd
---
# conductor-v3 migration

## Baseline and reconciliation

The canonical body starts from .opencode/agents/conductor-v3.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. The canonical agent body is authoritative for both native-primary rendering and the existing same-name role-skill fallback identity; this role is the explicit test-first alternative to the general conductor.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The orchestrator profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.search=deny, filesystem.write=deny, process.execute=deny, network.read=deny, network.publish=deny, delegate=allow. Explicit denials remain deny.

The migration originally recorded filesystem.read=ask to stand for the baseline's scoped read. That proved unrepresentable: a target whose permission model is a binary tool allow-list narrows ask to deny, which left the orchestrator unable to open the plan it exists to sequence. The scoped grant is now carried by filesystem.read=allow paired with filesystem.search=deny — the role may open a document it is pointed at, but never sweep the tree for one — and the folder restriction is stated in the instruction body, since no target expresses path scoping in its permission model.

The instruction body was revised after migration. It asserted that subagents may not spawn other agents — "none of them can, under the current design". That ceased to be true when the reviewer profile gained delegate=allow, and a false invariant in an instruction body is worse than no invariant. The body now states the rule that actually holds: an agent may invoke only the roles named in its own delegates-to, and only where its capability profile grants delegate.

The instruction body was revised after migration, twice. First, the Authority section stopped granting delegation by exception and stated the rule that holds: delegation is a per-role grant carried by each agent's capability profile and its declared delegates-to. Then the architect profile gained that grant, so the request/fulfill discovery loop this body mediated is now a fallback rather than the normal path — architect reaches azure-reader and research-agent itself and folds the findings into its own plan (direct-delegation model), and the conductor fulfils a labeled discovery request only where the harness does not let a subagent delegate or an Azure call fails after one retry, capped at three outer cycles. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor-v3 skill body, which the shared-identity rule requires.

The instruction body was revised after migration again, to route per-task review to `task-reviewer` rather than `code-reviewer`. Section 4 now describes a three-pass ladder: two fast passes returning PASS or FAIL with a fix list — checking the test-first discipline as part of the acceptance criteria — then one council pass, with findings surviving that pass tracked in a per-objective collection that routes through architect-v3 before the objective's council review. The rule that a test is never weakened to reach green is unchanged. The orchestrator profile is unchanged, and delegates-to gained task-reviewer. The revision was applied byte-identically to the paired conductor-v3 skill body, which the shared-identity rule requires. See ADR 0005.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
