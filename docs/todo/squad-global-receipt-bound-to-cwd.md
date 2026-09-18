---
id: todo/squad-global-receipt-bound-to-cwd
title: A global Squad deployment is bound to the directory the command ran in
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-18
status: draft
---

# A global Squad deployment is bound to the directory the command ran in

This is **context for planning the work, not a plan**. It records what was observed, what
needs deciding, and where the seam is.

## Why this exists

A global receipt is stored under
`~/Library/Application Support/KyberWeave/squad/roots/<binding>/`, where `<binding>` is
derived from the command's `[path]` argument (`GlobalRootBinding(targetRoot)` in
`SquadStateStore`). That argument defaults to the current directory, just as it does in
project scope.

On 2026-09-18, `kyber-weave squad install -g -t cursor` was run from a Claude Code worktree.
The installed files went to `~/.cursor/` as intended, but the receipt was bound to the
worktree's path. Two consequences followed:

- **Deleting the worktree orphans the deployment.** Once the worktree is gone,
  `squad update -g` or `squad uninstall -g` from any other directory cannot find it, while
  its files stay in `~/.cursor`.
- **`squad status -g` answers for the directory, not the user.** Run from a different
  directory, it reports "No Kyber-Squad deployment found" even though the global files are
  installed.

The deployment was reinstalled bound to `~` (Cursor) and `~/.gemini` (Antigravity), the
latter because of
[squad-global-multi-target-receipt-collision](squad-global-multi-target-receipt-collision.md).
So this machine now has two global receipts, each tied to a path chosen only to keep them
apart.

## What is known

- A global deployment's files live under per-target home roots (`SquadGlobalRoots`), so
  nothing about them depends on the current directory. Only the receipt binding does.
- The success message prints the path argument ("Successfully installed Kyber-Squad to
  /Users/…/worktree"), not the roots actually written, which hides the binding.
- This is related to, but different from,
  [squad-path-argument-safety](squad-path-argument-safety.md). That todo is about which
  directory a *project* deployment writes to; this one is about which directory a *global*
  deployment's receipt is filed under.

## What needs deciding

- Whether global scope ignores `[path]` entirely and uses one binding per user, with or
  without one per target.
- How existing path-bound global receipts are migrated or adopted.

## The code seam

- `src/KyberWeave.Core/Squad/Deployment/SquadStateStore.cs`: `GlobalRootBinding`,
  `ListOtherGlobalReceipts`.
- `src/KyberWeave.Cli/Commands/Squad/SquadCommandComposition.cs`: how `[path]` is resolved
  for `-g`.

## How to verify

`squad install -g` from directory A followed by `squad status -g`, `update -g` and
`uninstall -g` from directory B all act on the same deployment.
