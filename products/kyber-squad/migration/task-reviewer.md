---
schema: kyber-squad.migration/v1
agent: task-reviewer
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/task-reviewer.agent.md
sources:
  .github/agents/task-reviewer.agent.md: e096cb7d1507f7d6f1bc635dbc893d8f41a0289d105bfa5648bb2db5ba74b61c
  .github/agents/task-reviewer-v3.agent.md: 27bc894fd3ed5b56172d4210d9f2ff3ac716fe7c8d79ee11a15d1d4c4800a1f8
final-body-sha256: c2e7fbf50604282f1d039ea114c8431415455dd9b14a5d5850e5a3740c96f86b
---
# task-reviewer migration

## Hotshot golden baseline

The canonical agent was synchronized from `.github/agents/task-reviewer.agent.md` and later
consolidated with `.github/agents/task-reviewer-v3.agent.md` from Hotshot commit
`677c3a876ba9c62f1083608596b238c9deaff167`. Both source hashes are retained in frontmatter;
`final-body-sha256` covers the evolved, LF-normalized instruction body after YAML frontmatter.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
