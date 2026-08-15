---
schema: kyber-squad.migration/v1
agent: architect
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/architect.md
sources:
  .claude/agents/architect.md: c003c4d9061d41c670676f4379bf96726c69f1cec243422370a66863360a5910
  .codex/agents/architect.toml: ea0fce1e9c51ffe9aefe4a0c051e40faa13a0ae0526e89b0ff1329ae17533520
  .cursor/agents/architect.agent.md: 21530e88dc55fc5b8abb4f64489c56f71b2946fdee126439d6405763df1f3529
  .github/agents/architect.agent.md: f4efb0dc22263c22469cdcfc7a77c172782748f874a69209c869b4ab09001534
  .opencode/agents/architect.md: 986174e95069d9693f98d6358850e6304973868467daca0129549f60cd0ccef5
final-body-sha256: 2764f709907f38ef2c03ec57920f269a68d583600e46fe7026846ae6059298b3
---
# architect migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/architect.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The architect profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
