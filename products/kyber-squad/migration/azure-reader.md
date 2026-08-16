---
schema: kyber-squad.migration/v1
agent: azure-reader
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/azure-reader.md
sources:
  .claude/agents/azure-reader.md: cfe68f279be0c86223d7b3d8e18688c68a0297626ebfcf4822fd35b53af6c012
  .codex/agents/azure-reader.toml: 3b8d75165fb36380643a4077020a884973c1ad44cc28171603baa270bda9ab0d
  .cursor/agents/azure-reader.agent.md: c1d20516aafa70b6b7a039b5a3e123f2409a0433ab7a5109cd4316e93cc39896
  .github/agents/azure-reader.agent.md: b7f4848ffb5f056cd22296820ed65a468a68c8e7dd09a991d49d8846a17df1df
  .opencode/agents/azure-reader.md: aa014571d5e760bcf88508a4c8f61c09987ecbad4733c08d58ee319f78a032a0
final-body-sha256: 0e53ac073b459ab1137546d6f9b791588bf16ff86109c94f8632c43036c372f3
---
# azure-reader migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/azure-reader.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The read-only profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
