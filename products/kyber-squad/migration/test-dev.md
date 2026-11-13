---
schema: kyber-squad.migration/v1
agent: test-dev
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/test-dev.agent.md
sources:
  .github/agents/test-dev.agent.md: b461f218e0645748eb5beec7675c3d5908191d6facf2cb19ac0338fdc638e548
final-body-sha256: a49dbff93e5ba5992ea6688605eef562b8ffce0b25c28aa466d12200b46f3c9d
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
fact `ledger.red-evidence` (packet 7.4). Its full-suite sentence now says the council runs the suite in the first full review, and a review-fix round re-runs only the gates the fix touched. `final-body-sha256` covers that evolved body. Its Hard rules also now carry the RED input-case rule and the defect-class rule, which the Developer exit gate in the delivery-execution skill defines.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
