---
schema: kyber-squad.migration/v1
agent: product-owner
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/product-owner.md
sources:
  .claude/agents/product-owner.md: 1648960d4ee258b16dcb2f7e2862dedd5f548e12bbafbff383e01847d3d7f56c
  .codex/agents/product-owner.toml: fe6ecca5dbf8d8c471a77ad4e8ac91040973627b1a9486032815693f382a5bd1
  .cursor/agents/product-owner.agent.md: e8bc63a89a1b078f4df4aee15eae89b159972ce20ecdc0507473d4f75b635513
  .github/agents/product-owner.agent.md: a3a01c8088cdbf4891b639a21b099be84ead7229b5b477b20b76b5f1d4df3729
  .opencode/agents/product-owner.md: cdfefd1fe9000e568e7d35972753364892cfd0f3c90c67fa7407a7a79a21fb9b
final-body-sha256: 30adef003b402432203836c33f5368572390bd9250206b6df337715c0f4a3e0c
---
# product-owner migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/product-owner.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The product-planning profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=ask, process.execute=deny, network.read=allow, network.publish=deny, delegate=allow. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
