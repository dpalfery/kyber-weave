---
schema: kyber-squad.migration/v1
agent: test-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/test-dev.agent.md
sources:
  .github/agents/test-dev.agent.md: b461f218e0645748eb5beec7675c3d5908191d6facf2cb19ac0338fdc638e548
final-body-sha256: 5a591dbbd9e099f8a7b734dc720a7f02a8acdf70dda1bcd0511f78b7f6340884
---
# test-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/test-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: test execution with `--no-build` and `dotnet test`, the task-gate
exit gate, and `path.agent_scratchpad` worktree isolation. `final-body-sha256` covers that
evolved body.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
