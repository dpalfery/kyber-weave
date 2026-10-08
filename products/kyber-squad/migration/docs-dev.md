---
schema: kyber-squad.migration/v1
agent: docs-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/docs-dev.agent.md
sources:
  .github/agents/docs-dev.agent.md: db63b4d3b09d0d352c5381feb6821e3fb982acdf49ad44f3b5d33861343410b0
final-body-sha256: c34b75d566a967b49ecc8c694c1d6e31742ee381ba8e83472a5a67ed9e026349
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
