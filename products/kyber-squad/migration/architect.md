---
schema: kyber-squad.migration/v1
agent: architect
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/architect.agent.md
sources:
  .github/agents/architect.agent.md: 16afd840a7d528b941e86e4c9c3e050054a8655a423234c0ab484091373bc58c
final-body-sha256: 1e81c386a95a253ef984094d8a0a5a2df203a08500cb5665997b1aa3d2862dc0
---
# architect migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/architect.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
