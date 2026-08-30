---
schema: kyber-squad.migration/v1
agent: python-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/python-dev.agent.md
sources:
  .github/agents/python-dev.agent.md: f058efe4c9f5bd07ff6d908f539265eb5219a59806ac7db8b1be42525eefa944
final-body-sha256: a844c92006903f71a7a2b3103828e82ea813be23040f74b5c531cfef90946c9d
---
# python-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/python-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
