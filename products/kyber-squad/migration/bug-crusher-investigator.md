---
schema: kyber-squad.migration/v1
agent: bug-crusher-investigator
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/bug-crusher-investigator.md
sources:
  .claude/agents/bug-crusher-investigator.md: ffae92b2f88a13524dc422e9607d2e76eed23993e032b7374a5e6a0666ba96a1
  .codex/agents/bug-crusher-investigator.toml: dea8626c601d48fa5407ce1257da8c2ff8a27c3dfd0720f692bac5afe543c0b9
  .cursor/agents/bug-crusher-investigator.agent.md: c60bbeb8c849d58db3bca7eb7f9bc5027cbb35b61e23c0fa7f22bf4d1258d606
  .github/agents/bug-crusher-investigator.agent.md: d1d2eccd8cf6b08c1d53b05a7c7bd339f6ca50005f796142ae75b2ee54fad6e2
  .opencode/agents/bug-crusher-investigator.md: 534c307e6fa95588e63185b43f4b60e15b33552ca3e5a220c234268a3e9a5b81
final-body-sha256: 02c17ab003b7c3855652f6bb89020ed6d4db73c017be646851a3b271c5e56dfa
---
# bug-crusher-investigator migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/bug-crusher-investigator.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The investigator profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=allow, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
