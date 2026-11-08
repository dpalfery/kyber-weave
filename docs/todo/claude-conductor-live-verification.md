---
id: todo/claude-conductor-live-verification
title: Verify the Claude conductor entry point in a live session
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-07
---

# Verify the Claude conductor entry point in a live session

PR #136 adds a `/conductor` skill beside the existing Claude subagent. Renderer and CI checks
cover the generated files, but the [implementation plan's T6 checks](../archive/plans/2026-09-25-claude-conductor-entry-point-skill.md#t6-live-verification-run-by-the-parent-session)
were deferred during PR closeout. No live Claude Code result was recorded for slash-command
invocation, reference loading, or coexistence with `@agent-conductor`.

## The shared execution skill raises the stakes

The [conductor run-speed plan's](../plans/2026-11-07-conductor-run-speed-shared-execution.md)
slice 4 moved the execution contract out of
`products/kyber-squad/agents/conductor/references/execution-and-review.md` — deleted — into the
canonical `delivery-execution` skill, which both the `conductor` and the `bug-crusher` workflow
load. Every automated check reads the canonical Markdown and proves the skill exists, carries
the contract, and is named by both entry points. None of them can prove that a live session
**loads** it: a deployment that half-updated would leave a dangling link and the run would lose
its contract with no error. That is the observation this todo exists to capture now.

Slice 4's `bug-crusher` roster also widened to `architect`, `test-dev`, `code-reviewer` and the
twelve developer agents, so an escalation starts a specialist instead of stopping for a human.
Whether that dispatch actually works on a real run is likewise unobservable from the corpus.

## When a release carries it

`squad install` resolves a published Kyber-Squad release at the CLI's own version, so the
canonical source cannot be deployed on its own. Once a release carries this slice:

```
kyber-weave squad install --global --target claude --version <released-version>
```

Then run the checks and record the literal output of each — do not summarise:

1. **The conductor loads the shared skill.** `/skills` lists `delivery-execution`;
   `~/.claude/agents/conductor/references/` holds exactly `plan-path.md`, `spec-path.md`,
   `intake-path.md` and `autonomy.md`, with `execution-and-review.md` absent and unmentioned.
   Ask the session which file it follows for the developer exit gate: the answer must name the
   skill, which is the check that a registered skill was actually loaded. Then run `/conductor`
   over a Ready plan with one trivial task and confirm the worker's report carries the six-field
   completion digest and the conductor accepts it through that gate.
2. **Bug-crusher starts a specialist on a real run.** Trigger the escalation tripwire and confirm
   `architect` starts inside the session, writes the plan, and that the bug-crusher session does
   not edit the plan's status line or the plan index itself — behaviour that changed in this
   slice. Confirm no handback asks a human for a specialist that is installed.
3. **The supervised path is unchanged.** One ordinary defect with no tripwire still investigates,
   fixes directly and never round-trips through a plan.

Also run the original T6 checks above and record the deployed paths, receipt degradation codes,
`/skills` visibility, main-thread `/conductor` invocation, relative reference loading, and
retained subagent invocation, plus the automatic-loading observation and, if exercised, the two
optional deny settings checks. Update [onboarding](../kyber-squad/onboarding.md) only for
behaviour observed in the live session.

This todo is complete when the evidence and onboarding claims agree. Until then, the live
runtime behaviour remains unverified, and slice 4's child plan stays open in `docs/plans/`
because `KW-DOC-LIFECYCLE-003` fails an open plan by design — the intended shape, not a failure.
