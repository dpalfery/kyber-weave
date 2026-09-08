---
schema: kyber-squad.migration/v1
agent: github-devops
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/github-devops.agent.md
sources:
  .github/agents/github-devops.agent.md: 52ed73aca2f79ad7f57ccaad94299fa96827f34c5e97133728b037aa3a6d2593
final-body-sha256: af12af51815171a44b021409f7f303dcda5f9de2cb64c48163132b69098cebad
---
# github-devops migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/github-devops.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
