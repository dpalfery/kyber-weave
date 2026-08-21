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
final-body-sha256: ff4133f748c3ec7b38c085a400e51cc11a29360ebec48fdfce727c1bcec6d698
---
# code-reviewer migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/code-reviewer.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. A harness-specific configuration path and filename were normalized to the target-neutral phrase "applicable repository instructions" without changing the review behavior.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The reviewer profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The reviewer profile was deliberately widened after migration, away from the conservative intersection recorded above: filesystem.write deny to ask, process.execute deny to allow, delegate deny to allow.

The intersection was not wrong about the sources; it was wrong about the role. The instruction body demands build output and test logs, and the code-review skill declares a blocking pre-merge test and coverage gate. With process.execute=deny those are unexecutable — on Copilot the rendered agent has no execute tool at all — so the one agent whose purpose is refusing unverified claims was structurally forced to make one. process.execute=allow makes the gate real, exercised through the single declared runner.

delegate=allow lets the role fan out a council of review-lens instances over the diff in parallel. It also gives delegate=deny on the other subagent profiles a meaning it did not have before: delegation is now a per-role grant rather than a property of being a subagent.

filesystem.write stops at ask rather than following execute to allow. The reviewer writes one artifact, its findings; the lattice has no path scoping, so the scope is instruction-only and the permission is the weakest one that permits the write. Targets that cannot express ask narrow it to deny, and the reviewer returns findings in its response instead. network.publish stays deny. The role that judges a change never ships it.

The instruction body was rewritten alongside that widening. The role is now an orchestrator and an adjudicator rather than a single serial pass: it runs the gate suite, fans out a council of review-lens instances, and decides what the combined evidence supports. Thirteen concern-specific blocks — dependency injection, model placement, the threat-modelling questions, analyzer triage — were not deleted but moved into the code-review skill's lens catalogue, where each is loaded only by the seat that owns it and only when the diff contains something for it. The skeptical contract is unchanged in substance and stronger in form: what was a demand for proof in prose is now a required evidence field that the verdict engine drops a finding for lacking.

The body was revised again the same day to name two lens runners rather than one. Lenses whose input is a machine artifact — analyzer diagnostics, a manifest diff — go to review-triage on the fast model profile, because attributing tool output to a change is bounded work; every lens that judges code stays on review-lens. The reviewer's own permissions and contract are unaffected.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
