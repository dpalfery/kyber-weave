---
schema: kyber-squad.migration/v1
agent: pulumi-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/pulumi-dev.agent.md
sources:
  .github/agents/pulumi-dev.agent.md: 968b2999eb8f998b8fc9863389ddca445661c8c444cecf079b3257dac9ae0040
final-body-sha256: ff1296c6065a64ec1a7bebd8d09f01761c88d5acebe9266aee3ca845ea23d443
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
