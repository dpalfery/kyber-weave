---
schema: kyber-squad.migration/v1
agent: test-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/test-dev.agent.md
sources:
  .github/agents/test-dev.agent.md: b461f218e0645748eb5beec7675c3d5908191d6facf2cb19ac0338fdc638e548
final-body-sha256: 40720f62a34cd227fa41fe08b95f52e910c9765d4a0a68253ceb81703b35ba52
---
# test-dev migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/test-dev.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: test execution with `--no-build` and `dotnet test`, the task-gate
exit gate, `path.agent_scratchpad` worktree isolation, and the completion digest carrying a
machine-readable `RED_EVIDENCE:` line the in-flight ledger parses for the `KW-ARB-MODE-001`
fact `ledger.red-evidence` (packet 7.4). `final-body-sha256` covers that evolved body.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
