---
id: todo/kyberdash-engine-failure-skips-hook-refresh
title: A failed KyberDash engine step ends the update before the hook refresh runs
doc-type: todo
component: Distribution
status: draft
owner: dpalfery
last-reviewed: 2026-11-09
---

# A failed KyberDash engine step ends the update before the hook refresh runs

In the self-updater, the KyberDash engine step runs before the arbiter hook refresh. When the engine step
fails, the update ends there, so the hook refresh never runs. The binaries are already installed at that
point, so the arbiter hooks stay on the previous release's source until the next successful update.

The ordering was kept by [the self-update hand-off plan](../archive/plans/2026-11-08-self-update-finishes-in-the-new-binary.md),
which moved both steps into the new binary without changing their order. It was listed there as out of
scope.

**Decide:** either run the hook refresh even when the engine step fails and report both outcomes, or keep
the order and state in the update output that the hook refresh was skipped and why.
