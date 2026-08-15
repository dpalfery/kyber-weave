---
schema: kyber-squad.migration/v1
agent: architect-v3
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/architect-v3.md
sources:
  .claude/agents/architect-v3.md: 6b312b32b4d7f423e3d9fc0aaefffd389e8864e6f60aba1339b6e40976525c43
  .codex/agents/architect-v3.toml: 8ce47ea00e09b96aa145802fcb578da96eb96d181ce968f7942f3e9ff8311e70
  .cursor/agents/architect-v3.agent.md: 6fbb40364b86fb88043145e3b9f9c895803b1bc0eabbe4e0cc57aaa5c328da7f
  .github/agents/architect-v3.agent.md: c973df5e8d283f7b5d9b36b95c583d9fb29e3d3633bf318603d2c4ba4ddc8208
  .opencode/agents/architect-v3.md: 5b35cc86188a35bb2a1d4edd8ed1fd8788ca7b5df772bc197defdfe3f53b93e6
final-body-sha256: 69acc4d2b5019a211692e0421880fc59bdc778ad153405484f4cd7efb6ebe828
---
# architect-v3 migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/architect-v3.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The architect profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
