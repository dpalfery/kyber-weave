---
id: adr/0034-squad-owned-blocks-in-shared-hook-files
title: Squad owns marked entries in shared hook files
doc-type: adr
status: current
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-09
---

# ADR 0034: Squad owns marked entries in shared hook files

## Status

Accepted, 2026-10-09. Records owner decisions D8 (2026-10-01) and D25 (2026-10-03):
Squad writes receipt-tracked blocks into hook files the user also owns, identified by
the hook command signature. This is the exception to the owned-files-not-settings
boundary that [ADR 0033](0033-kyber-arbiter-three-step-decision-gates.md) decision 6
announces and defers here, as Req 8.3 requires.

## Context

Squad does not own settings files. The architecture rejects writing them, and the
sibling component's [ADR 0026](0026-kyber-utilities-owned-files-not-settings.md) draws
the same boundary for Kyber Utilities: a shared settings file is the user's state, its
schema drifts across harness versions, and a merge that guesses wrong is hard to
reverse.

Three Phase 2 Arbiter harnesses leave no other place to put a hook. Cursor keeps hooks
in the shared `.cursor/hooks.json`; Codex in the shared `.codex/hooks.json`; Pi loads
a TypeScript extension file Squad owns outright (`.pi/extensions/kyber-arbiter.ts`),
so it needs no block. The shared files cannot carry marker comments in the style of
the Config Reg block — JSON has no comments — and the entries must coexist with the
user's own hooks in the same containers. (The splice already covers the Phase 3
shapes — `.factory/hooks.json`, `.devin/hooks.v1.json`, and the Antigravity
`.agents/hooks.json` group — but Phase 2 renders only the Cursor and Codex blocks.)

## Decision

1. **Squad owns marked entries, never the file.** The deployment plan splices Squad's
   rendered entries into the shared hook file, replacing Squad's previous entries and
   leaving the user's entries — order and values — in place. A missing file starts
   from the format's minimal document. An existing user file at a block path is not
   an unmanaged collision.
2. **Entries are identified by command signature (D25).** A Cursor entry is Squad's
   when its `command` starts with `kyber-weave-arbiter hook --harness cursor`; a
   Codex matcher group is Squad's when every `hooks[].command` in the group starts
   with `kyber-weave-arbiter hook --harness codex` — a group mixing Squad and user
   hooks stays user content. Shared-file hooks gate project-wide, so the command
   carries no `--caller`. Antigravity is owned by key instead: Squad owns the whole
   top-level `kyber-arbiter` group, since any unknown top-level key there would read
   as another group.
3. **Only documented fields are written (D25).** The splice projects each entry down
   to the fields the harness documents — `{command, matcher, timeout, failClosed}`
   on Cursor, `{matcher, hooks:[{type, command, timeout}]}` on Codex — and never a
   sentinel key. Serialized output is normalized (two-space indentation, trailing
   newline), so a splice rewrites the file's formatting even when its hooks are
   unchanged.
4. **The receipt tracks each entry's location and digest.** Receipts that own a block
   serialize as `kyber-squad.receipt/v3`: the v1 project field set plus `blocks`,
   each with `relativePath`, `target`, `createdFile`, and `entries` of
   `container` (an RFC 6901 JSON pointer to the entry, e.g. `/hooks/preToolUse/0`)
   and `sha256` (over the entry's compact JSON). Receipts without blocks stay
   byte-identical v1 or v2, and an older reader refuses v3 with exit code 1.
5. **The plan-side splice is the file splice.** `SquadDeploymentPlan` calls the
   content-level `SquadHookJsonBlock.SpliceContent` that the file-level splice also
   uses, so a dry run and an install cannot diverge. Planning stays side-effect free
   because it never calls the file-level `SpliceFile`, which is what writes.
6. **Drift is reported, not silently repaired (Req 8.4).** A hand-edited or missing
   owned entry is reported by `squad status` and `squad doctor` as drift, naming the
   file and the container. `squad update` rewrites the block but preserves a drifted
   entry, reporting it, unless `--replace-managed` is given. `squad uninstall`
   removes only the owned entries and deletes the file only when Squad created it
   and no hook remains.

## Alternatives considered

- **Own the whole hook file.** Rejected: it would claim the user's hooks, turning
  every user edit into either a collision or silent Squad state. Entry ownership is
  the smallest claim that still enforces the gate.
- **Manual activation in the style of Kyber Utilities.** Rejected: a hook file that
  is merely staged enforces nothing until the user copies a snippet, and unlike a
  status line there is no visible output that tells the user the gate is missing.
  The audit cannot see dispatches no hook observed, so an unwired hook fails open
  silently.
- **Marker comments delimiting Squad's block.** Rejected: JSON hook files carry no
  comments, so there is nowhere to put a marker that survives a parse. Ownership by
  command signature survives any formatting the user's editor applies.
- **Calling the file-level splice during planning.** Rejected: it writes to disk,
  which breaks `--dry-run` and makes the plan untestable without a filesystem. The
  shared content-level splice keeps one wire format and one digest function for
  both paths.

## Consequences

- **Splicing normalizes formatting.** The user's file is rewritten with normalized
  indentation on every install and update that touches the block, even when no hook
  changed. The hooks themselves are untouched, and the user's own text is not
  otherwise altered: the file is written with a relaxed JSON encoder, so characters
  such as `&&`, quotes, angle brackets and non-ASCII letters stay as typed and are never
  rewritten as `\uXXXX` escapes. Parsed content is identical before and after; only
  whitespace changes.
- **Drift is found by digest, not by position.** The receipt records each entry's index
  as a hint. A user who inserts a hook ahead of Squad's entry shifts it, so the check
  looks for the untouched entry anywhere in its container and reports drift only when
  no such entry exists.
- **v3 receipts need a current CLI.** A receipt with blocks is unreadable by older
  readers, which refuse it with exit code 1 rather than misreading ownership.
  Receipts without blocks are unaffected, so harnesses that need no shared file
  never force the upgrade.
- **Hand edits inside the block are drift, not adoption.** Editing a Squad entry by
  hand does not transfer ownership; it is reported until the next update rewrites
  it (or `--replace-managed` is passed). User entries beside the block are never
  drift.
- **Mixed matcher groups stay user content.** On Codex, appending a personal hook
  into Squad's matcher group converts the group to user content, so the next splice
  appends a fresh Squad group rather than editing the mixed one. The duplicate fires
  twice until the user moves their hook out.
- **The receipt schema id is permanent.** `kyber-squad.receipt/v3`, with its
  `blocks` field set, joins the permanent identifiers: renaming it silently
  un-suppresses ownership every host receipt records.

## Related

- [ADR 0026](0026-kyber-utilities-owned-files-not-settings.md) — the sibling
  owned-files-not-settings boundary this decision is the exception to
- [ADR 0033](0033-kyber-arbiter-three-step-decision-gates.md) — decision 6, which
  announces this exception and defers it here
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — receipt v3 and the
  block splice
- [Kyber-Squad requirements](../kyber-squad/requirements.md) — the owned-block
  contract
- [Kyber-Squad adoption guide](../kyber-squad/onboarding.md) — installing over
  user hook files
