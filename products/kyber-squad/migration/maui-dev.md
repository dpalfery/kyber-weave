---
schema: kyber-squad.migration/v1
agent: maui-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/maui-dev.md
sources:
  .claude/agents/maui-dev.md: db44cd66076d87ddd44cb357978b2dc0de7e1ef05eadf1148fb454996d6191da
  .codex/agents/maui-dev.toml: beafe3cdb9841b8dfeae192f97c2108132576144534722fffdd184de3109c960
  .cursor/agents/maui-dev.agent.md: f8f404b37f11eee6882f2a9394cb665056314df1b7be89978122c1cf1eaeae69
  .github/agents/maui-dev.agent.md: 1cb5984dfbf6142e88c464c4f8edd040fa7332ca0956ec299443cf822bb9843b
  .opencode/agents/maui-dev.md: 8c4133b41cd6439d078b1735482a5bdb10602078aa79f19a79b7a3dfe3951261
final-body-sha256: 90d2078bef615a21c52b34904c04e358297070836707e7229235c286364031ff
---
# maui-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/maui-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
