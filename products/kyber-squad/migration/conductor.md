---
schema: kyber-squad.migration/v1
agent: conductor
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/conductor.agent.md
sources:
  .github/agents/conductor.agent.md: fe8fa326b83780cafb4a9b8aba06312ddb106eb732de1f7a64f3a88b8fe773c6
final-body-sha256: 72ef45f3453e641857dd1c4f9d4830fe35aef77d063cb60b0e472fe41a7a4ebd
---
# conductor migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/conductor.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
