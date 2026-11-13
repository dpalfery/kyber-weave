---
schema: kyber-squad.migration/v1
agent: dal-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/dal-dev.agent.md
sources:
  .github/agents/dal-dev.agent.md: efbd8ed5a6f2f8342a184cf57cba08c3649dba0e736e980257101a1056f3079a
final-body-sha256: a95e4913ff83f85f30ccacde8551fb1ce00861710ef746ad9bad42fb0d71a7ac
---
# dal-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/dal-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: its Hard rules now carry the input-site table rule and the defect-class rule, and `final-body-sha256` covers that evolved body.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
