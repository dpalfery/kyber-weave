---
schema: kyber-squad.migration/v1
agent: code-reviewer
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/code-reviewer.md
sources:
  .claude/agents/code-reviewer.md: 301e887cd283bd3fd3eb090557ad2df3b1cce170930c5accdeaa2d131d93bab8
  .codex/agents/code-reviewer.toml: 4f919b491271d65ce7b10108bb3715308bc9037c0de1916c483ed23e566ce8c8
  .cursor/agents/code-reviewer.agent.md: abbc10e4d5758d50d032d60598f48da0f590f3f4aef61fe68dd6a499d6a2626f
  .github/agents/code-reviewer.agent.md: c4b31d87762a8814cd0e69772be4e1b43c481337117debb1acbb658addde7976
  .opencode/agents/code-reviewer.md: 4e40f1a3130f9618f741d93b897c00a9606c6d7de970b583c7f8ad47d3a10de3
final-body-sha256: d97d527b3388c434c9f18530a5db4b27972212d92effb12e2fbcff019b3c38e0
---
# code-reviewer migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/code-reviewer.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. A harness-specific configuration path and filename were normalized to the target-neutral phrase "applicable repository instructions" without changing the review behavior.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The reviewer profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
