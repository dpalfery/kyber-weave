---
id: todo/squad-install-version-flag
title: Add a --version flag to squad install and squad update
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# Add a --version flag to squad install and squad update

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

`squad install` and `squad update` can only ever install whatever Squad release version
happens to match the *running CLI binary's own* assembly version — there is no way to pin a
version from the command line. `squad pack` has `-v|--version <VERSION>`
(`SquadPackSettings`, `src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`); `squad install`
and `squad update` do not.

This was found while verifying the Copilot renderer end-to-end: `dotnet run --project
src/KyberWeave.Cli -- squad install --target copilot` against a locally built dev binary
failed with `404 Not Found`, because the dev build's assembly-informational version (e.g.
`0.1.0+<commit-sha>`) does not correspond to any real GitHub release tag. The only way to
complete the proof was to publish a throwaway binary stamped with `-p:Version=1.0.6-rc.6` —
a real released version — since there was no flag to just say so.

## What is known

- The domain model already carries the field: `SquadInstallRequest.Version` and
  `SquadUpdateRequest.Version` (`src/KyberWeave.Core/Squad/Deployment/SquadLifecycleService.cs`)
  are both `string? Version = null`, defaulting to
  `ResolveDefaultVersion()` — the running assembly's own version — when unset. The lifecycle
  layer is ready to accept a pinned version; nothing downstream needs to change.
- `SquadInstallCommand.Execute` and `SquadUpdateCommand.Execute`
  (`src/KyberWeave.Cli/Commands/Squad/SquadInstallCommand.cs`,
  `SquadUpdateCommand.cs`) build their `SquadInstallRequest`/`SquadUpdateRequest` without
  ever populating `Version` — it is simply never wired from settings to request.
- `kyber-weave update` (the *CLI's own* self-update, `UpdateCommand`,
  `src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs`) already has exactly this pattern for
  a different subsystem: a `--version` option normalized through `ReleaseVersion.Normalize`
  (`src/KyberWeave.Cli/Update/ReleaseVersion.cs`) before use. That normalization (stripping a
  leading `v`, rejecting path-fragment-shaped input) is the right thing to reuse rather than
  re-derive for Squad versions too.

## What needs deciding

- Whether `squad update` pinning a version *forward or backward* relative to the existing
  receipt's recorded version needs its own guard, or whether that is already handled by
  existing version-lockstep behavior elsewhere in the lifecycle service.
- Whether an invalid/nonexistent version should surface the underlying GitHub 404 verbatim
  (as it does today) or a clearer "no Squad release exists at version X" message — the 404
  above was correct but not especially actionable for a user unfamiliar with the release
  flow.

## The code seam

- Add `[CommandOption("-v|--version <VERSION>")] public string? Version { get; set; }` to
  `SquadInstallSettings` and `SquadUpdateSettings`
  (`src/KyberWeave.Cli/Commands/Squad/SquadSettings.cs`), mirroring `SquadPackSettings`.
  Normalize through `ReleaseVersion.Normalize` before passing to the request, matching
  `UpdateCommand`'s existing pattern.
- Thread `Version: settings.Version` into the `SquadInstallRequest`/`SquadUpdateRequest`
  construction in `SquadInstallCommand.Execute` / `SquadUpdateCommand.Execute`.

## How to verify

- `squad install --target copilot --version 1.0.6-rc.6` installs from that exact release
  without needing the running binary's own version to match it.
- Omitting `--version` preserves current behavior (defaults to the running assembly's
  version) — this must not become a breaking change for existing callers.
- An invalid version string is rejected before any network call, consistent with
  `ReleaseVersion.Normalize`'s existing behavior in the self-update path.
