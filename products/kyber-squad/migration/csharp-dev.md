---
schema: kyber-squad.migration/v1
agent: csharp-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/csharp-dev.agent.md
sources:
  .github/agents/csharp-dev.agent.md: 09cd65841fbb82e81060cbbccdc91c6aac7ee209a84b97e30799cc3281cbe146
final-body-sha256: 2fe9f41566f10639b97532b51b1bd8a01ae29b64a31b76d6ffb45c3654fff1a0
---
# csharp-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/csharp-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
