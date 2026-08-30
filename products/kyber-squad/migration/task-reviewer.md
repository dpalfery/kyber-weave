---
schema: kyber-squad.migration/v1
agent: task-reviewer
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/task-reviewer.agent.md
sources:
  .github/agents/task-reviewer.agent.md: e096cb7d1507f7d6f1bc635dbc893d8f41a0289d105bfa5648bb2db5ba74b61c
final-body-sha256: 881accf653a1ba0fac3bab6e663f5519564971fc247fc435c8fb8bd8da70d208
---
# task-reviewer migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/task-reviewer.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
