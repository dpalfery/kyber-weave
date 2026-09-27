---
id: adr/0024-squad-global-receipt-layout-marker
title: An Explicit Layout Marker on Global Squad Receipts, with Legacy Single-Root Recovery
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-26
component: KyberSquad
---

# ADR 0024: An Explicit Layout Marker on Global Squad Receipts, with Legacy Single-Root Recovery

## Status

Accepted, 2026-09-26. Records decisions A3–A5 of the archived
[receipt layout marker plan](../archive/plans/2026-09-26-squad-receipt-layout-marker.md),
delivered for [issue 98](https://github.com/dpalfery/kyber-weave/issues/98).

## Context

A Global Squad receipt records the files a deployment owns, as paths relative to the recorded
deployment root. Two layouts have shipped under the same `kyber-squad.receipt/v1` schema:
- **rc.9 and rc.10** wrote every owned file beneath one deployment root, with its target's
  project prefix kept (`.codex/agents/x.toml`).
- **Since #91 (rc.11)** each target's files live beneath that target's own global root, with
  bare paths (`agents/x.toml` beneath `~/.codex`).

The receipt carried nothing to tell the two apart, so the current CLI resolved a legacy entry to
`~/.codex/.codex/...`. As a result:
- `status` reported every file missing.
- `update` and same-target `install` dropped the entries and orphaned the originals.
- `uninstall` reported success with 0 files, then deleted the receipt and lock. This cannot be
  undone.

The legacy and per-target physical paths coincide only for some targets at a `$HOME` root with no
environment override: Copilot (`~/.github` vs `~/.copilot`) and Antigravity (`~/.agents` vs
`~/.gemini/config`) never do. So rewriting the receipt alone cannot migrate a legacy install.

## Decision

1. **Global receipts are written as `kyber-squad.receipt/v2` with a required `layout`**
   of `single-root` or `per-target-roots`. v2 is the v1 field set plus `layout`, with `scope`
   optional because a v2 receipt is always Global. Project receipts stay on v1, byte-identical
   to every receipt written before, with no `layout` field. `SquadStateStore` holds the one
   schema source for both versions. It derives the write schema from the receipt's scope,
   never from the schema string it was handed.

2. **A v1 Global receipt is classified from its own paths, never from the disk.**
   `SquadDeploymentPlan.ClassifyGlobalLayout` checks each owned file against a fixed table of
   per-target legacy prefixes (`.github/`, `.cursor/`, `.claude/`, `.codex/`, `.opencode/`,
   `.kilo/`, `.factory/`, `.agents/`, `.warp/`, `.pi/`, `.zcode/`):
   - every entry prefixed → `single-root`;
   - none prefixed → `per-target-roots`;
   - a mix → rejected as invalid data, with guidance.

   The same receipt always classifies the same way. A guard test pins that no registered
   renderer's Global output starts with its target's legacy prefix, since the classifier
   depends on that. `update` and `uninstall` stamp the layout they classify from the files
   they keep, and a v2 receipt whose declared `layout` contradicts its own paths is rejected
   on read, so a mislabeled receipt can neither be written by them nor trusted.

3. **Legacy receipts are read-and-remove only.** `status` and `uninstall` resolve a
   `single-root` receipt beneath its recorded deployment root:
   - uninstall deletes only owned files whose digests match;
   - it keeps an edited file and rewrites the receipt as v2 `single-root` with only that
     entry;
   - it deletes the lock and receipt only after the files are gone.

   The uninstall confirmation names that recorded root. `update` and same-target `install`
   refuse a `single-root` receipt with `SquadDeploymentConflictException` before any download
   or change, and name `squad uninstall --global` followed by `squad install --global` as the
   way out. A v1 receipt with bare paths updates normally and is rewritten as v2
   `per-target-roots`.

## Alternatives Considered

- **Keep v1 and let path spelling be the permanent layout rule (Q1b).** Rejected: it leaves no
  explicit marker, which is what the issue asks for, and every future reader would have to
  re-derive the layout.
- **v2 for every scope (Q1c).** Rejected: project receipts are committed and shared across
  teammates' CLI versions, and an older CLI would refuse them.
- **Record resolved absolute roots in the receipt (Q1d).** Rejected: breaks the portable
  `targetRoot: "."` rule.
- **Migrate in place on `update` (Q2b).** Rejected: an edited legacy file would need a
  per-entry layout, and paths that overlap at `$HOME` would need special handling.
- **A new `squad migrate --global` command (Q2c).** Rejected as the largest scope: a new
  command, docs and tests, for a population limited to two release candidates.
- **Refuse on all four commands (Q2d).** Rejected: `install` routes through `update`, so the
  only way out would be deleting state by hand.
- **Classify by whether the per-target roots lack the recorded bytes (Q3b).** Rejected: it
  depends on the filesystem, so the same receipt could be classified differently over time.
- **Classify by the lock's `cli-version` (Q3c).** Rejected: fails for development builds.

## Consequences

- A CLI from before this decision refuses a v2 Global receipt (exit 1, no changes) until it is
  upgraded. That is by design: misreading a layout is what deleted state in the first place.
- A legacy rc.9/rc.10 Global install cannot be updated in place. It is recovered by uninstalling
  and reinstalling with the current CLI, and edited files survive the uninstall.
- Classification relies on renderers never emitting a prefixed Global path. A renderer that
  starts to fails the guard test (`RenderReceiptLayout_GlobalRender_ContainsNoPrefixedPaths`)
  rather than silently turning new receipts into legacy ones.
- A receipt that mixes prefixed and bare paths has no automatic recovery. The error tells the
  owner to verify or remove each file by hand, then delete the receipt and lock.
- `squad doctor` does not read receipts and is unaffected. Sibling-global ownership across
  bindings is also unchanged.

## Related

- [Kyber-Squad architecture](../kyber-squad/architecture.md#receipt-version-and-layout-contract)
  holds the receipt version and layout contract.
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) covers rc.9/rc.10 Global recovery.
