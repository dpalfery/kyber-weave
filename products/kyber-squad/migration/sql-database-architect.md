---
schema: kyber-squad.migration/v1
agent: sql-database-architect
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/sql-database-architect.md
sources:
  .claude/agents/sql-database-architect.md: fa601470d69f439c185c2578eac5418cd6c95fb21f3b518559d67e9be2d5acc4
  .codex/agents/sql-database-architect.toml: 3dd58d14028ff8aafaac8c868b824747f44fc5504d09211470efdc7cad686cc6
  .cursor/agents/sql-database-architect.agent.md: 57119991dbea2d3a2e7ac9e1d52ea9a612402036a66428807af70dfa86c1a8a2
  .github/agents/sql-database-architect.agent.md: 91a27a358c43ce3719afbf9899d3f4f9942b57ed32e2768b36943475b327b8f8
  .opencode/agents/sql-database-architect.md: da70672b7a0a9eda28e25de01fdc85646e03f4544e0895f0b8ad3be897830b37
final-body-sha256: da480f811d7b23a8f8943b321918b5d77cd32593745683ce0ca7f54463c9081e
---
# sql-database-architect migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/sql-database-architect.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
