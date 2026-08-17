---
schema: kyber-squad.migration/v1
agent: docs-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/docs-dev.md
sources:
  .claude/agents/docs-dev.md: 6fef4156bd54ab4cb111aeac519daf4481555d204884bd9844051f5a98a7f4b9
  .codex/agents/docs-dev.toml: 93be095ce082aca68241ffb2161812b8212ce873414725a117c65c29b89fd6e2
  .cursor/agents/docs-dev.agent.md: d21c3c06d105b517234d2eb5e9faf4cce02ec677e922cb81edb08ea8bd1fbe8e
  .github/agents/docs-dev.agent.md: 6d064d541f50a38f471083352cb1dcc9de8cc745fd96b75972da61b3ce4a44ee
  .opencode/agents/docs-dev.md: e95d461ea01b696d5c45e519f71fad135042bf683c04b9e348daaf2d1dd60ae1
final-body-sha256: 46a0ae7b5e53abb7124663206338cec1d5b48e1c1f5787730cf7c154499fb92e
---
# docs-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/docs-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The documentation profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=deny, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
