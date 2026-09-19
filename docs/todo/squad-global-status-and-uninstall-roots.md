---
id: todo/squad-global-status-and-uninstall-roots
title: Global squad status and uninstall dry-run report against the wrong root
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-18
status: draft
---

# Global squad status and uninstall dry-run report against the wrong root

This is **context for planning the work, not a plan**. It records what was observed, what
needs verifying, and where the seam is.

## Why this exists

Three observations from 2026-09-18, all on global deployments made with `kyber-weave`
0.1.7-rc.11:

1. **`squad status -g` reports installed files as missing.** Run as
   `kyber-weave squad status -g "$HOME"` against a Cursor deployment whose files were
   present in `~/.cursor/agents/`, it reported entries such as `missing agents/architect.md`.
   `kyber-weave squad status -g "$HOME/.gemini"` did the same for the Antigravity
   deployment's `skills/…` files under `~/.gemini/config/`. The paths it printed are
   relative to each target's global root, but it appears to check them against the `[path]`
   argument instead.
2. **The uninstall dry run found nothing to remove.** `kyber-weave squad uninstall -g
   --dry-run` reported "would uninstall 0 files". The real `uninstall -g` that followed then
   removed the deployment's agent files from `~/.cursor/agents/`.
3. **Directories were left behind.** After that uninstall, three entries remained in
   `~/.cursor/agents/`: the `architect`, `conductor` and `task-reviewer` directories, which
   hold agent reference files. Whether they still contained files was not checked before a
   reinstall overwrote them.

The first two misreport what a global deployment holds, and both are the checks an operator
relies on before and after a change.

## What needs verifying

- Whether observations 1 and 2 share one cause: resolving receipt paths against the
  command's root rather than through `SquadGlobalRoots` per target.
- Whether observation 3 is directory cleanup declining to remove non-empty directories
  because the files inside were not in the receipt, or a real leak.

## The code seam

- `src/KyberWeave.Cli/Commands/Squad/SquadStatusCommand.cs` and `SquadUninstallCommand.cs`:
  how each resolves file paths under `-g`.
- `src/KyberWeave.Core/Squad/Deployment/SquadTransaction.cs`: directory cleanup in global
  scope.
- `src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs`: the per-target roots that
  status and dry-run should resolve through.

## How to verify

After a global install, `status -g` reports every file `ok`, `uninstall -g --dry-run`
lists every file the real uninstall removes, and the real uninstall leaves no Squad-created
directory behind.
