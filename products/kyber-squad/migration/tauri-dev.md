---
schema: kyber-squad.migration/v1
agent: tauri-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/tauri-dev.md
sources:
  .claude/agents/tauri-dev.md: 930ba43922d1608a9ad80342f170d3b1a121483e63361ab813bf25f396a123bc
  .codex/agents/tauri-dev.toml: b941336e8fd75c6961b57be25b7b5a2af9243c31502793e9adaf4bd54fbf69e9
  .cursor/agents/tauri-dev.agent.md: 956b9e594dd5de07d600240de3987fb396a6c038c65d8a54e326a7471cff543d
  .github/agents/tauri-dev.agent.md: 9c633575014701180012aaa839c4d0e7a79540627b398be81c9844d76bac8535
  .opencode/agents/tauri-dev.md: dfc067c2aa69313d06b30bcba7c0605266b64dfe5b251a811ab64a4253c8f3a1
final-body-sha256: a78492389b3a5d7f6561ab21b38deb300914318331396afb8d7a33b919624da1
---
# tauri-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/tauri-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
