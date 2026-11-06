---
schema: kyber-squad.migration/v1
agent: tauri-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/tauri-dev.agent.md
sources:
  .github/agents/tauri-dev.agent.md: 30f08182e6c42869088f2df5cc36aa6162182a97fa6576342b9ca2fff47051ad
final-body-sha256: 19ea4498bbfa2cef39f4dea1671327b36cd03dd76c06837b20fe2ba4f336b2e6
---
# tauri-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/tauri-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
