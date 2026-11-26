---
id: todo/arbiter-global-hook-dispatch-coverage
title: Confirm that sub-agent dispatch fires the global Arbiter before-hook
doc-type: todo
status: draft
component: KyberArbiter
owner: dpalfery
last-reviewed: 2026-11-26
---

# Confirm that sub-agent dispatch fires the global Arbiter before-hook

The [Arbiter global install plan](../archive/plans/2026-11-04-arbiter-global-install.md) installs the
Arbiter before-hook at the user's global hook location wherever a harness supports one. The vendor
documentation read on 2026-11-05 is generic on four points, so each is recorded as unconfirmed and
non-blocking, with the observed fallback:

- Whether the sub-agent dispatch tool (`task` in OpenCode and Kilo, `runSubagent` in VS Code Copilot)
  fires the before-tool hook. The documentation names only `subagentStart` and `subagentStop` for VS Code.
- Whether Cursor's global `~/.cursor/hooks.json` or the project file wins when both define a hook
  (precedence is undocumented).
- The minimum released version of each harness that supports the global location (mostly unstated).
- An OpenCode instance install writes no Arbiter plugin. `SquadInstanceService.EnsureAsync` builds `SquadInstallRequest` with no Arbiter wiring, and `SquadLifecycleService` renders that request as global scope against the instance root, so `AddArbiterPluginShim` writes no `plugins/kyber-arbiter.ts`. A global install on the same machine has that file under the user OpenCode root (`~/.config/opencode/plugins/kyber-arbiter.ts`). Giving `squad run opencode` the plugin would have to change decision 4 of [ADR 0044](../adr/0044-squad-lock-v2-and-instance-deployment-scope.md) and [ADR 0054](../adr/0054-squad-owned-blocks-in-shared-hook-files.md) (decision 4: instance scope never owns a block). Whether that user-global plugin is discovered when the config root is the instance is a separate spike.

Fallback: a harness whose block-on-dispatch cannot be shown when its renderer ships is reported
`degraded` in the install output with the recorded reason, and the Arbiter MCP `serve` fallback
remains the enforcement path. This is never a reason to withhold the install.

This todo is complete when each point is either confirmed against a live harness and recorded in the
Arbiter architecture matrix, or the matrix records the degraded reason as permanent.
