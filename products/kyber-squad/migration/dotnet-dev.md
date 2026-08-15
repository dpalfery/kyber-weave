---
schema: kyber-squad.migration/v1
agent: dotnet-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/dotnet-dev.md
sources:
  .claude/agents/dotnet-dev.md: 36de7a9673c75b7d9ee35c47a9df44972b98d49e9944e701e7103f31b94352b1
  .codex/agents/dotnet-dev.toml: aa7612e3bd3ffa6ec76ca907aeb58bc6aa60c54cecd424efe3abe048bf87620e
  .cursor/agents/dotnet-dev.agent.md: 908e5f0813a3a4a0ff6b1d983645744c24b7bce6574cb97dff34dd52b6f0b616
  .github/agents/dotnet-dev.agent.md: ea402133727e2b32d7987871a4aa7edb09fe5240340b2fe1dff8d61affdec72c
  .opencode/agents/dotnet-dev.md: 2190ab5b76e48203bf3250468e35741ec90625402bcf5ab7e2051cd3df3d1bb0
final-body-sha256: 337c32d9022d03007fc6b55bcfdd471b6e9c829ff62b1586ee8663c3642ef89e
---
# dotnet-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/dotnet-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
