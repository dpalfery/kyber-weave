---
schema: kyber-squad.migration/v1
agent: bug-crusher-investigator
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/bug-crusher-investigator.agent.md
sources:
  .github/agents/bug-crusher-investigator.agent.md: e0f68b7e28c9153de337dbb5daba0c756a192714c8906bc5aeb379d54553dbb2
final-body-sha256: 02c17ab003b7c3855652f6bb89020ed6d4db73c017be646851a3b271c5e56dfa
---
# bug-crusher-investigator migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/bug-crusher-investigator.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
