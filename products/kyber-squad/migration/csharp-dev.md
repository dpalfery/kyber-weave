---
schema: kyber-squad.migration/v1
agent: csharp-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/csharp-dev.agent.md
sources:
  .github/agents/csharp-dev.agent.md: 09cd65841fbb82e81060cbbccdc91c6aac7ee209a84b97e30799cc3281cbe146
final-body-sha256: 150e753ce37e26139275183890d39d5781e3d5ec9a30d183b19ee52fd885eeca
---
# csharp-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/csharp-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: test execution with `--no-build` and `dotnet test`, the task-gate
exit gate, and `path.agent_scratchpad` worktree isolation. Its full-suite sentence now says the council runs the suite in the first full review, and a review-fix round re-runs only the gates the fix touched. `final-body-sha256` covers that
evolved body. Its Hard rules have since also gained the input-site table rule and the defect-class rule, which the Developer exit gate in the delivery-execution skill defines.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
