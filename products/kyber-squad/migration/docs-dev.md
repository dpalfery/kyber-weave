---
schema: kyber-squad.migration/v1
agent: docs-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/docs-dev.agent.md
sources:
  .github/agents/docs-dev.agent.md: db63b4d3b09d0d352c5381feb6821e3fb982acdf49ad44f3b5d33861343410b0
final-body-sha256: 5b1fa8be9dca8418c2b5470bf8d153f87de9d04e70ec7628e2949ce99feeee75
---
# docs-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/docs-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
