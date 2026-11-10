---
schema: kyber-squad.migration/v1
agent: azure-reader
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/azure-reader.agent.md
sources:
  .github/agents/azure-reader.agent.md: f4f8bf0fe71adc3933808dccf4112a5fa49154069a2ed0977cc92b0d005382ca
final-body-sha256: ece5b9dec52cc2589311d6527b8103386808edc4f6fcfb555adf1efee7047ae0
---
# azure-reader migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/azure-reader.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
