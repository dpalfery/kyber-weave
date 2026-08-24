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
final-body-sha256: fe0df2ec1b573a68411484bdbc5e196e17e5c3041c94a1b24f3676a9b56e431a
---
# code-reviewer migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/code-reviewer.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract. A harness-specific configuration path and filename were normalized to the target-neutral phrase "applicable repository instructions" without changing the review behavior.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The reviewer profile's historical conservative intersection after unsupported capabilities were marked unavailable was: filesystem.read=allow, filesystem.write=deny, process.execute=deny, network.read=allow, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The effective profile is filesystem.write=ask, process.execute=allow, delegate=allow (filesystem.read=allow, filesystem.search=allow, network.read=allow, network.publish=deny unchanged).

The intersection was not wrong about the sources; it was wrong about the role. The instruction body demands build output and test logs, and the code-review skill declares a blocking pre-merge test and coverage gate. With process.execute=deny those are unexecutable — on Copilot the rendered agent has no execute tool at all — so the one agent whose purpose is refusing unverified claims was structurally forced to make one. process.execute=allow makes the gate real, exercised through the single declared runner.

`kyber-weave review gates . --out artifacts/gates.json` and `kyber-weave review duplicates . --out artifacts/duplicates.json` are written by that executed CLI, not by the agent's write tool. Findings JSON is the one write that still uses filesystem.write=ask; targets that cannot express ask narrow it to deny and the reviewer returns findings in its response instead. Source edits stay denied. network.publish stays deny. The role that judges a change never ships it.

delegate=allow lets the role fan out a council of review-lens instances over the diff in parallel. It also gives delegate=deny on the other subagent profiles a meaning it did not have before: delegation is now a per-role grant rather than a property of being a subagent.

The instruction body was rewritten alongside that widening. The role is now an orchestrator and an adjudicator rather than a single serial pass: it runs the gate suite, fans out a council of review-lens instances, and decides what the combined evidence supports. Thirteen concern-specific blocks — dependency injection, model placement, the threat-modelling questions, analyzer triage — were not deleted but moved into the code-review skill's lens catalogue, where each is loaded only by the seat that owns it and only when the diff contains something for it. The skeptical contract is unchanged in substance and stronger in form: what was a demand for proof in prose is now a required evidence field that the verdict engine drops a finding for lacking.

The body was revised again the same day to name two lens runners rather than one. Lenses whose input is a machine artifact — analyzer diagnostics, a manifest diff — go to review-triage on the fast model profile, because attributing tool output to a change is bounded work; every lens that judges code stays on review-lens. The reviewer's own permissions and contract are unaffected.

The instruction body was revised after migration to state when this role runs. It reviews a whole run once, at the end, and a single task only when a human asks; it is no longer routed per completed task. A council spun up per task spends fifteen lenses and a full gate suite on a question about a couple of files and pays it again on the next task, which is why `task-reviewer` now owns single tasks. The body also instructs the role to say so rather than proceed if it is ever invoked on one task without a human having asked, since reviewing it anyway would hide the routing fault. No permission changed and the council's procedure, lenses, gates, and verdict engine are untouched. See ADR 0005.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
