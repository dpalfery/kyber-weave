---
schema: kyber-squad.migration/v1
agent: test-dev
source-commit: d7547f46ab6bb8e447096345abbe5d4c7840bfc0
selected-baseline: .claude/agents/test-dev.md
sources:
  .claude/agents/test-dev.md: f5cbeddcca72d6786607d0b07f6a3f3a5ce2c336a927fb1d70f4d19f7f6333ac
  .codex/agents/test-dev.toml: 7f3669af6145e0d39f50f67e4c6070e06114ec98569a7871aa98334b145e6a78
  .cursor/agents/test-dev.agent.md: 77ac564aa1a783fab81e6fa702f16496c99c8aebf6dc7cf5f7bfe1b02452e6c7
  .github/agents/test-dev.agent.md: bb2700d7d1fa57405a8de345de6ab08e17d66b3727b0a987883345f761225f22
  .opencode/agents/test-dev.md: 0c6262120157c680f5d3403973b8dfe773209f93605891cc939b1f1584144025
final-body-sha256: 2e8d2cb540aa692e7f6b84be0e9aee924b1e09a80536e425f4f64e6b6da1d4ed
---
# test-dev migration

## Baseline and reconciliation

The canonical body starts from .claude/agents/test-dev.md at the locked source commit. No alternate harness body is retained. Other live variants were compared for harness-independent behavior; none added behavior that could be merged without changing the selected role contract.

Source frontmatter, provider model identifiers, tool allowlists, and command-shaped invocation syntax are excluded from the instruction body. Equivalent intent is represented by canonical invocation, model, capability, delegation, fallback, and alias metadata.

## Permission resolution

The worker profile is the conservative intersection of effective live permissions after unsupported capabilities are marked unavailable: filesystem.read=allow, filesystem.write=allow, process.execute=allow, network.read=deny, network.publish=deny, delegate=deny. Scoped source grants resolve to ask when a broad allow would widen access; explicit denials remain deny.

The instruction body was revised after migration. A blocking completion gate on language diagnostics was added: a baseline sweep before the first edit, a full-file and workspace sweep after the last one, a rule that every diagnostic class counts, and a `DIAGNOSTICS` line in the completion digest. The gate exists because a green build measures a different thing than a clean Problems list, and "pre-existing" was being asserted rather than proven. Capability profile and delegation are unchanged.

The body now routes to the `resharper-clt` skill and folds a ReSharper InspectCode run into the completion gate, at the baseline and again before `READY_FOR_REVIEW`. ReSharper's inspection set and the compiler's overlap only partially — a suggestion-severity inspection never reaches `dotnet build` — so a green build was clearing a gate it does not actually measure.

The final digest is calculated from the UTF-8, LF-normalized body loaded from the canonical agent file.
