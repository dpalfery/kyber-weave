---
id: todo/squad-global-multi-target-receipt-collision
title: A global Squad install of two targets that share relative paths fails at receipt write
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-18
status: draft
---

# A global Squad install of two targets that share relative paths fails at receipt write

This is **context for planning the work, not a plan**. It records what was observed, what
needs deciding, and where the seam is.

## Why this exists

On 2026-09-18, with `kyber-weave` 0.1.7-rc.11,
`kyber-weave squad install -g -t cursor,antigravity` failed with:

```
Squad receipt file path 'skills/app-docs-standard/SKILL.md' is not a unique portable path.
```

A `--dry-run` of the same command succeeded and planned 226 files, so the dry run does not
predict the failure. The workaround was one global install per target, each bound to a
different path argument. That workaround creates the second receipt described in
[squad-global-receipt-bound-to-cwd](squad-global-receipt-bound-to-cwd.md).

## What is known

- **The two files do not actually collide.** In global scope each target resolves its own
  root (`SquadGlobalRoots`): Cursor under `~/.cursor`, Antigravity under `~/.gemini/config`.
  Both render skills to the relative path `skills/<name>/SKILL.md`, so the absolute paths
  differ.
- **The receipt check only looks at the relative path.** `SquadStateStore`'s receipt
  validation (the loop ending in the "is not a unique portable path" exception) builds its
  uniqueness set from `RelativePath` alone, not from target plus path. In project scope that
  is correct, because all targets share one root. In global scope it rejects files that do
  not conflict.
- **The failure happens late.** It fires when the receipt is written, after planning, which
  is why the dry run passes. No files were left behind; the transaction rolled back.

## What needs deciding

- Whether a global receipt's file identity becomes target plus relative path, or the
  resolved absolute path. Either choice needs a receipt schema bump or a compatible reader,
  because existing global receipts key on relative paths.
- Whether the dry run should run receipt validation, so that it predicts this class of
  failure.

## The code seam

- `src/KyberWeave.Core/Squad/Deployment/SquadStateStore.cs`: receipt validation.
- `src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs`: per-target global roots.
- `src/KyberWeave.Core/Squad/Deployment/SquadTransaction.cs`: in global scope, each file's
  target root is already resolved per file, so the transaction itself handles distinct
  roots.

## How to verify

A global install of `cursor,antigravity` succeeds, and so does its dry run. Reinstalling
either target alone does not orphan or duplicate the other's files.
