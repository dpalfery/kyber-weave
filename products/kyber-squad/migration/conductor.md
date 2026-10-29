---
schema: kyber-squad.migration/v1
agent: conductor
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/conductor.agent.md
sources:
  .github/agents/conductor.agent.md: fe8fa326b83780cafb4a9b8aba06312ddb106eb732de1f7a64f3a88b8fe773c6
  .github/agents/conductor-v3.agent.md: 0392bd1e3dd09ab67f45801cb39aac7d016b5ed43733ee2ae65ac6c5668d7204
final-body-sha256: f9d6f41fdd310a3b61632ece297499250a23025e2369b10ff6ed546d94bb8e0f
---
# conductor migration

## Hotshot golden baseline

The canonical agent was synchronized from `.github/agents/conductor.agent.md` and later
consolidated with `.github/agents/conductor-v3.agent.md` from Hotshot commit
`677c3a876ba9c62f1083608596b238c9deaff167`. Both source hashes are retained in frontmatter;
`final-body-sha256` covers the evolved, LF-normalized instruction body after YAML frontmatter.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.

The canonical body evolved after that reconciliation — the `response-format` binding, the
`<intake-assessment>` resolution, and the routing and relay rules that follow them — so
`final-body-sha256` was recomputed over the evolved body rather than carried forward.
