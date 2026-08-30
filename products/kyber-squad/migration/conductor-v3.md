---
schema: kyber-squad.migration/v1
agent: conductor-v3
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/conductor-v3.agent.md
sources:
  .github/agents/conductor-v3.agent.md: 0392bd1e3dd09ab67f45801cb39aac7d016b5ed43733ee2ae65ac6c5668d7204
final-body-sha256: 2491db44c9a3712757f3caa577dd1b0c7fda2b1e217aacc9cd9f3603c1a01418
---
# conductor-v3 migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/conductor-v3.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
