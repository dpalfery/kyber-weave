---
schema: kyber-squad.migration/v1
agent: code-reviewer
source-commit: 677c3a876ba9c62f1083608596b238c9deaff167
selected-baseline: .github/agents/code-reviewer.agent.md
sources:
  .github/agents/code-reviewer.agent.md: 98440b62230139ae74e4f1a54d15482db338ab34bf912d98afbc629b249ffd14
final-body-sha256: 935382c74aa974db9ad0f1c6ebc2225fc11d14b34744585d248d44af8cf53509
---
# code-reviewer migration

## Hotshot golden baseline

The canonical agent description and instruction body were synchronized from `.github/agents/code-reviewer.agent.md` at
Hotshot commit `677c3a876ba9c62f1083608596b238c9deaff167`. The selected source file's SHA-256 is recorded in frontmatter,
while `final-body-sha256` is the SHA-256 of the UTF-8, LF-normalized instruction body after
YAML frontmatter is removed.

The body has since evolved: what to do when the harness gives the reviewer no tool for
invoking another agent, as on Devin (ADR 0025), the review-guidance step, which makes the reviewer
pass the host's `REVIEW.md` sections to each seat and record a missing seat attestation as a
coverage gap, the Arbiter review contract — routing headers on council and refutation dispatches,
the review-note statuses, and the audit citation (packet 7.3) — the fallback contract for a reviewer
that holds `arbiter_evaluate_agent_dispatch_or_return` instead of a hook (packet 17.4), and the status-provenance rule that
keeps an Arbiter note distinct from a `STATUS:` line a lens or a tool printed. It now also carries the single-gate re-check
through the runner's `--gate` option, and the seat handback: a harness that gives the reviewer no way to start its own
council stops it with `STATUS: COUNCIL_SEATS_NEEDED`, and the session above seats the lenses and re-invokes it.
It also indexes the tree under review with `kyber-weave doctor --ensure .` before any seat is dispatched, records that
result and any seat's `Index fallback:` line under Council coverage, and reports a tree it cannot index as a coverage gap.
The review loop changed it again: the reviewer runs after each fix round as well as at the end, every later cycle in
verifier mode; it saves each seat's output to the cycle folder and computes the verdict from those seat files; the
council's gates stop at the council deadline; and its seats report discovery above their attestation line.
`final-body-sha256` covers that evolved body.

## Canonical projection

Target-neutral `invocation`, `model-profile`, `capability-profile`, `delegates-to`, `fallback`,
and `aliases` remain canonical lifecycle fields. The `copilot-tools` field preserves exact golden
membership; Copilot rendering applies only the approved deterministic ordering.
