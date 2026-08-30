---
schema: kyber-squad.migration/v1
agent: task-reviewer-v3
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/task-reviewer-v3.agent.md
sources:
  .github/agents/task-reviewer-v3.agent.md: 27bc894fd3ed5b56172d4210d9f2ff3ac716fe7c8d79ee11a15d1d4c4800a1f8
final-body-sha256: 91b03ba1201bdd54dff15b2cd1291d889532df961231a07fc719f19bcced8ac6
---
# task-reviewer-v3 migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/task-reviewer-v3.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
