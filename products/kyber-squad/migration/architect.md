---
schema: kyber-squad.migration/v1
agent: architect
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/architect.agent.md
sources:
  .github/agents/architect.agent.md: 16afd840a7d528b941e86e4c9c3e050054a8655a423234c0ab484091373bc58c
  .github/agents/architect-v3.agent.md: 5eb0ce098c11a54c9f78f75daaf517d332fedda97f5d9431e6c968b2a9558fcb
final-body-sha256: cac39f48a8fb0c2571533ad9e00f50f2d8995581901d25a38049373b00713ad4
---
# architect migration

## Hotshot golden baseline

The canonical agent was synchronized from `.github/agents/architect.agent.md` and later
consolidated with `.github/agents/architect-v3.agent.md` from Hotshot commit
`677c3a876ba9c62f1083608596b238c9deaff167`. Both source hashes are retained in frontmatter;
`final-body-sha256` covers the evolved, LF-normalized instruction body after YAML frontmatter.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
