---
schema: kyber-squad.migration/v1
agent: research-agent
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/research-agent.agent.md
sources:
  .github/agents/research-agent.agent.md: e0f94800f1c177fec00ff70f977263a61e14b3af1820225b7f7d99f60f66a548
final-body-sha256: d30c702257419ca6645b7254ff6def84d32a24e18377f9c73cd36d5d090c79e9
---
# research-agent migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/research-agent.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
