---
schema: kyber-squad.migration/v1
agent: pulumi-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/pulumi-dev.agent.md
sources:
  .github/agents/pulumi-dev.agent.md: 968b2999eb8f998b8fc9863389ddca445661c8c444cecf079b3257dac9ae0040
final-body-sha256: 59a44060f3a0ccdd426b4f22de7c6eb33f0be4dfc6bc50a0cd487b4fa656cbdc
---
# pulumi-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/pulumi-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
