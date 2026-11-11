---
schema: kyber-squad.migration/v1
agent: conductor
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/conductor.agent.md
sources:
  .github/agents/conductor.agent.md: fe8fa326b83780cafb4a9b8aba06312ddb106eb732de1f7a64f3a88b8fe773c6
  .github/agents/conductor-v3.agent.md: 0392bd1e3dd09ab67f45801cb39aac7d016b5ed43733ee2ae65ac6c5668d7204
final-body-sha256: f4c428e9fff599d6e7025fd747c835f0a44f438eb3f069329e0f141d9cc1179f
---
# conductor migration

## Hotshot golden baseline

The canonical agent was synchronized from `.github/agents/conductor.agent.md` and later
consolidated with `.github/agents/conductor-v3.agent.md` from Hotshot commit
`677c3a876ba9c62f1083608596b238c9deaff167`. Both source hashes are retained in frontmatter;
`final-body-sha256` covers the evolved, LF-normalized instruction body after YAML frontmatter,
including the Arbiter routing-marker list, the status-provenance rule, and the `COUNCIL_SEATS_NEEDED` handback that lets
the conductor seat the `review-lens` and `review-triage` lenses for a reviewer whose harness cannot start them.
The body names the `delivery-execution` skill rather than linking a local reference: the worker execution
contract that used to sit among the conductor's own reference files is that skill now, so both the
conductor and the `bug-crusher` workflow load one copy.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
