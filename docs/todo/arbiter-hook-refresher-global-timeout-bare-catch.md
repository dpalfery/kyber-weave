---
id: todo/arbiter-hook-refresher-global-timeout-bare-catch
title: ArbiterHookRefresher.ResolveGlobalTimeout has a bare catch
doc-type: todo
component: KyberArbiter
status: draft
owner: dpalfery
last-reviewed: 2026-11-09
---

# ArbiterHookRefresher.ResolveGlobalTimeout has a bare catch

`ArbiterHookRefresher.ResolveGlobalTimeout` (`src/KyberWeave.Cli/Update/ArbiterHookRefresher.cs`) uses a
bare `catch`, which swallows every exception, including ones the repository's C# standard says must not be
swallowed. Found while closing out [the self-update hand-off plan](../archive/plans/2026-11-08-self-update-finishes-in-the-new-binary.md).
It was not changed there because it sits outside that change's scope.

**Decide:** catch only the exception types a timeout read can raise, and let anything else reach the hook
refresh's failure handler, where it is reported with its real cause.
