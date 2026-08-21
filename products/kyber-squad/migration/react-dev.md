---
schema: kyber-squad.migration/v1
agent: react-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/react-dev.md
sources:
  .claude/agents/react-dev.md: 5bdb08955c9981583f4371597d01770cc91ee53a8d1fc3b34be868799e709fdc
  .codex/agents/react-dev.toml: 29aee883289f0630b1467d84e0c7121da1dbc47d8dfd78a3d6b4c19deb730fe3
  .cursor/agents/react-dev.agent.md: c3b779757917432ec8403622d7920578a1b9c47f07e968ff161912eab25cd68b
  .github/agents/react-dev.agent.md: 0b089376574faafc5b3399032705b64e62482efdea6095e3e162e57aaa6b16a5
  .opencode/agents/react-dev.md: e4ce5052baf16b1c3b9aa13b97d5078927c1dc901d959f8643f80bcf7046499f
final-body-sha256: b736e75bb0f2ca5cb60c2496f305df0a65d63af37550cce6a183d74f4820f7bc
---
# react-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/react-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The instruction body was revised after migration. A blocking completion gate on language diagnostics was added, paired with the project lint command over the same paths: a baseline sweep before the first edit, a full-file and workspace sweep after the last one, a rule that every diagnostic class counts, and a `DIAGNOSTICS` line in the completion digest. The gate exists because a green build measures a different thing than a clean Problems list, and "pre-existing" was being asserted rather than proven. Capability profile and delegation are unchanged.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
