---
schema: kyber-squad.migration/v1
agent: code-reviewer
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/code-reviewer.agent.md
sources:
  .github/agents/code-reviewer.agent.md: 98440b62230139ae74e4f1a54d15482db338ab34bf912d98afbc629b249ffd14
final-body-sha256: b7b7f3dde819df3c0f4f1db62e667b2fd90a1426e9300df4a0039b0b0c8e5c4f
---
# code-reviewer migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/code-reviewer.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: what to do when the harness gives the reviewer no tool for
invoking another agent, as on Devin (ADR 0025), and the Arbiter review contract — routing
headers on council and refutation dispatches, the review-note statuses, and the audit
citation (packet 7.3), and the fallback contract for a reviewer that holds `arbiter_evaluate` instead of
a hook (packet 17.4), and the status-provenance rule that keeps an Arbiter note distinct from a
`STATUS:` line a lens or a tool printed. `final-body-sha256` covers that evolved body.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
