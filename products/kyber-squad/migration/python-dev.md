---
schema: kyber-squad.migration/v1
agent: python-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/python-dev.md
sources:
  .claude/agents/python-dev.md: 79e21f9c6dd947434ed1d79137bd4e60917f660e066416680edd048d35a7a4bd
  .codex/agents/python-dev.toml: e4f35c89f89bdf5f88ad3465e78a22f3d9cccff8ba99f086325f66c4d317fce4
  .cursor/agents/python-dev.agent.md: f54df2691e7a9eefe39ebc7594c76ad1219b849231db175e7be231d4863bb5c6
  .github/agents/python-dev.agent.md: 99f132c8bcfc21649a115a4d87c000c76b410be147d7c32b1d70d4fb8d8c34ee
  .opencode/agents/python-dev.md: 4992401ae92c611b480b9733d4678fceeadd0d04825a6b3b77b08234a80566eb
final-body-sha256: a844c92006903f71a7a2b3103828e82ea813be23040f74b5c531cfef90946c9d
---
# python-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/python-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
