---
schema: kyber-squad.migration/v1
agent: dal-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/dal-dev.md
sources:
  .claude/agents/dal-dev.md: 09f9d6f1a5f4311a29934049bb0d4e5e91814a7ebf29eb864f68db3378894f4b
  .codex/agents/dal-dev.toml: 416ce76e948cd447bb3fb97c6585fa87f23d36b57da8d74429cada745fe71c9d
  .cursor/agents/dal-dev.agent.md: f55c14cc1dbca99edca8912e4094f0696837289764a577d56ffb0521a8cbb43f
  .github/agents/dal-dev.agent.md: 5a9e1b9ef11c9e62c4f7ce71776c7ea88009041fe79aca0fa9002e13fd427d51
  .opencode/agents/dal-dev.md: 9f3fd875d63276e02b0219fbed33e084f0fd3c95e7090cd7152b98ad120f9adf
final-body-sha256: 9b8da3d7693e2eb6d2551f2a0fc13aff58da782c115dea175b502066b8ccdad2
---
# dal-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/dal-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
