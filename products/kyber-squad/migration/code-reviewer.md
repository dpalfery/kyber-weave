---
schema: kyber-squad.migration/v1
agent: code-reviewer
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/code-reviewer.agent.md
sources:
  .github/agents/code-reviewer.agent.md: 98440b62230139ae74e4f1a54d15482db338ab34bf912d98afbc629b249ffd14
final-body-sha256: f38e564a96dd415c3ef3b6b8314eca925062cfd282813382aa13425bbb5046d1
---
# code-reviewer migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/code-reviewer.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
