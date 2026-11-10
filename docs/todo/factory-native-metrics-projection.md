---
id: todo/factory-native-metrics-projection
title: Factory's native OpenTelemetry metrics are stored for Droid sessions but never projected or shown
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-09
---

# Factory's native OpenTelemetry metrics are stored for Droid sessions but never projected or shown

This is the second of three follow-ups from the Droid data-completeness work. The first slice
(2026-11-09) gave Droid sessions a real duration, a cost for MiniMax M3.1 Flash, the tool calls
from the transcript and Droid's own session titles. It did not use Factory's native OpenTelemetry
metrics, which record things the transcript does not.

## What is deferred

- Factory's native metric points reach the store, but nothing reads them back.
  `CanonStore.getMetricPoints` has no production caller, so the session view never shows them.
- Candidate metrics to project into the session:
  - `droid.code.lines_modified`
  - `droid.code.files_modified`
  - `droid.git.commits`
  - `droid.git.pull_requests`
  - `droid.mcp.tool_invocations`
  - `droid.skill.invocations`
- Render the projected metrics in the session view.

## Why it is separate

Projecting them adds a new field to the session payload and a new panel. That is a contract change of
its own, and the four figures the first slice fixed do not depend on it.

The general gap, metric points stored but never projected, is also filed as
[native-metric-points-stored-but-not-projected.md](native-metric-points-stored-but-not-projected.md).
This todo is the Factory instance of it, with its candidate list.

## Also to re-check

- Droid's cache-creation tokens are non-zero in only 26 of 365 owner sessions. The first slice passed
  them through from Factory's companion settings file unchanged, and did not check the zeros against
  Factory's documentation. Check them against the native data here.
