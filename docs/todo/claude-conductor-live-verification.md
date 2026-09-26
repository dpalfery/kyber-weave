---
id: todo/claude-conductor-live-verification
title: Verify the Claude conductor entry point in a live session
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-25
---

# Verify the Claude conductor entry point in a live session

PR #136 adds a `/conductor` skill beside the existing Claude subagent. Renderer and CI checks
cover the generated files, but the [implementation plan's T6 checks](../archive/plans/2026-09-25-claude-conductor-entry-point-skill.md#t6-live-verification-run-by-the-parent-session)
were deferred during PR closeout. No live Claude Code result was recorded for slash-command
invocation, reference loading, or coexistence with `@agent-conductor`.

When a release containing #136 is available in a safely managed Claude environment, run the
T6 checks and record the deployed paths, receipt degradation codes, `/skills` visibility,
main-thread `/conductor` invocation, relative reference loading, and retained subagent
invocation. Record the automatic loading observation and, if exercised, the two optional deny
settings checks. Update [onboarding](../kyber-squad/onboarding.md) only for behaviour observed
in the live session.

This todo is complete when the evidence and onboarding claims agree. Until then, the live
runtime behaviour remains unverified.
