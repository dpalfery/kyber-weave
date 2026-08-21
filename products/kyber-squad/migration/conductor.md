---
schema: kyber-squad.migration/v1
agent: conductor
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .opencode/agents/conductor-v2.md
sources:
  .github/agents/conductor-v2.agent.md: e82cce130d47eba57ffb1aa1a9b53592e78879ff40f9743158359530375b7ef9
  .opencode/agents/conductor-v2.md: 681ecbd25ebf61ed3d0550bc1f4e3e50639d7c155db1003aaa5b31a64d600180
final-body-sha256: df7e74e513767f86b9cc522e07a0bdbab709ef16697168d9883b5ea3a2bdf44d
---
# conductor migration

## Baseline and reconciliation

The canonical body starts from .opencode/agents/conductor-v2.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. The canonical agent body is authoritative for both native-primary rendering and the existing same-name role-skill fallback identity.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The orchestrator profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.search=deny, filesystem.write=deny, process.execute=deny, network.read=deny, network.publish=deny, delegate=allow. Explicit denials remain deny.

The migration originally recorded filesystem.read=ask to stand for the baseline's scoped read. That proved unrepresentable: a target whose permission model is a binary tool allow-list narrows ask to deny, which left the orchestrator unable to open the plan it exists to sequence. The scoped grant is now carried by filesystem.read=allow paired with filesystem.search=deny — the role may open a document it is pointed at, but never sweep the tree for one — and the folder restriction is stated in the instruction body, since no target expresses path scoping in its permission model.

The instruction body was revised after migration. The Authority section granted delegation by exception ("unless explicitly authorized", sole exception architect). Delegation is now a per-role grant carried by each agent's capability profile and its declared delegates-to, so the body states that rule instead of enumerating exceptions, and names the two roles that hold the grant: architect for discovery, and code-reviewer for its review council. The orchestrator profile is unchanged, and the revision was applied byte-identically to the paired conductor skill body, which the shared-identity rule requires.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
