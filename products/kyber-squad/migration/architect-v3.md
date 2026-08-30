---
schema: kyber-squad.migration/v1
agent: architect-v3
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/architect-v3.agent.md
sources:
  .github/agents/architect-v3.agent.md: 5eb0ce098c11a54c9f78f75daaf517d332fedda97f5d9431e6c968b2a9558fcb
final-body-sha256: 084e5e27e612f752b16e5ded160d45cfef91157eca3bd3b71994cbe4cb908d67
---
# architect-v3 migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/architect-v3.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
