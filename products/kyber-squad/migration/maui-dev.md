---
schema: kyber-squad.migration/v1
agent: maui-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/maui-dev.agent.md
sources:
  .github/agents/maui-dev.agent.md: d275ca8508a007832e6c00bb77f38735f1f849ac4bd8eb7a554ee1b05e371a2f
final-body-sha256: 676ed30e147b4bde586be852a6768849b2a6f6128c3bcd57c831162af664721f
---
# maui-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/maui-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
