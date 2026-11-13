---
schema: kyber-squad.migration/v1
agent: python-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/python-dev.agent.md
sources:
  .github/agents/python-dev.agent.md: f058efe4c9f5bd07ff6d908f539265eb5219a59806ac7db8b1be42525eefa944
final-body-sha256: bee4b32072b0164347d0c0704abbd945a50e2c9cab5c80c1d0ce212d98919489
---
# python-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/python-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: its Hard rules now carry the input-site table rule and the defect-class rule, and `final-body-sha256` covers that evolved body.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
