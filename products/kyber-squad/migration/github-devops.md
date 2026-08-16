---
schema: kyber-squad.migration/v1
agent: github-devops
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/github-devops.md
sources:
  .claude/agents/github-devops.md: 16d02c53b1aa2336ca0e841e5373f42d4edb9d7977f0ba0091f788a001052711
  .codex/agents/github-devops.toml: 861b99bcb75ec969c30d6c8ec1855b182fccdf9aafa6f5bcded35e0cda9e800a
  .cursor/agents/github-devops.agent.md: d04bef3702c88bb3fe601243ba142e01c4514179e30e2c582af8337c8da26107
  .github/agents/github-devops.agent.md: 9eb691c620287490893255a18bdeafbe0517e908886c300d2064e7dfecd64512
  .opencode/agents/github-devops.md: a66ba87bd4513493f3e8e98884a54a692645b02a272e597852a5f758aba2b2fc
final-body-sha256: 93b105d4f318bc7995155359f09019a9464aec201cc0bf8d5f76bdb312f22f97
---
# github-devops migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/github-devops.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The publishing-worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=allow, network.publish=ask, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
