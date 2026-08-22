---
schema: kyber-squad.migration/v1
agent: conductor-v3
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .opencode/agents/conductor-v3.md
sources:
  .opencode/agents/conductor-v3.md: 2696d45e25dcc3bbfc0f20445026d5f0213a59e783ba247bd59dc109b112e95c
final-body-sha256: 452b2726383450ddfd155ed6fdfce5586d3ca1bd3b8fdae30cf0cb5cc6bbee46
---
# conductor-v3 migration

## Baseline and reconciliation

The canonical body starts from .opencode/agents/conductor-v3.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. The canonical agent body is authoritative for both native-primary rendering and the existing same-name role-skill fallback identity; this role is the explicit test-first alternative to the general conductor.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The orchestrator profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.search=deny, filesystem.write=deny, process.execute=deny, network.read=deny, network.publish=deny, delegate=allow. Explicit denials remain deny.

The migration originally recorded filesystem.read=ask to stand for the baseline's scoped read. That proved unrepresentable: a target whose permission model is a binary tool allow-list narrows ask to deny, which left the orchestrator unable to open the plan it exists to sequence. The scoped grant is now carried by filesystem.read=allow paired with filesystem.search=deny — the role may open a document it is pointed at, but never sweep the tree for one — and the folder restriction is stated in the instruction body, since no target expresses path scoping in its permission model.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
