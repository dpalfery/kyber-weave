---
schema: kyber-squad.migration/v1
agent: test-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/test-dev.agent.md
sources:
  .github/agents/test-dev.agent.md: b461f218e0645748eb5beec7675c3d5908191d6facf2cb19ac0338fdc638e548
final-body-sha256: ed1e4cffe3fce3a7b0270213279f0ddb5dd149c62da4d6c74411fba533287524
---
# test-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/test-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
