---
schema: kyber-squad.migration/v1
agent: research-agent
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/research-agent.md
sources:
  .claude/agents/research-agent.md: 7a263b79530bf959593b08cab1f06453b8b1d1c2c7b8e087240c4799e30bb314
  .codex/agents/research-agent.toml: e7191addc253a08b7cb1c9205c046f35cf3f2facc45d08e997eceb92abe4a2c6
  .cursor/agents/research-agent.agent.md: e431eba99ecdcdbb4daf648a3cfaf9c3ebf4189e056d9b0daf9e408963965dcd
  .github/agents/research-agent.agent.md: 1f9a585bd02881b9f502c54704dda05baa4cb9e9cde9b410f11c9caef4a1002b
  .opencode/agents/research-agent.md: 7991063e7b5ede195f7a9ac74526a0e773d435facbe57fe3b207b0f317aada39
final-body-sha256: 857c3fa1027977668cea8436703cd15e19d4fb77ddb8713535fd0ccc16168c4c
---
# research-agent migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/research-agent.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The read-only profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
