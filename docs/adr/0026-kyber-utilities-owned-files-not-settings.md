---
id: adr/0026-kyber-utilities-owned-files-not-settings
title: Kyber Utilities Deploys Owned Files and Never Owns Harness Settings, with Manual Activation
doc-type: adr
status: current
component: KyberUtilities
owner: dpalfery
last-reviewed: 2026-09-28
---

# ADR 0026: Kyber Utilities Deploys Owned Files and Never Owns Harness Settings, with Manual Activation

## Status

Accepted, 2026-09-28. Records decisions A3–A5, D3–D5 and D7 of the
[Kyber Utilities status-line slice](../archive/plans/2026-09-28-kyber-utilities-status-line-slice.md),
approved for execution under A7 on 2026-09-28.

**Amendment, 2026-09-28 (D9).** The harness set in this ADR narrows to Claude Code, `agy`, and
Pi — three harnesses, not four. Under D9 of the
[status-line slice](../archive/plans/2026-09-28-kyber-utilities-status-line-slice.md), OpenCode is
dropped from the slice: T0 found no OpenCode implementation, so shipping it would need fresh
authoring, which the slice's rules forbid. OpenCode support is deferred to
[issue #165](https://github.com/dpalfery/kyber-weave/issues/165). The ownership decision itself
is unchanged; this note only narrows the harness set.

## Context

The developer runs Claude Code, the Antigravity CLI (`agy`), Pi, and OpenCode side by side, each
with a hand-maintained status line. Nothing in Kyber-Weave deploys, versions, or diagnoses those
files, and Kyber-Squad is not a fit: it owns agent and skill files only, and its architecture
explicitly rejects owning settings files ("Squad does not own settings files",
[Kyber-Squad architecture](../kyber-squad/architecture.md) §3, rejected alternatives).

Two facts about the harnesses shape the decision. First, **a status line is activated through a
settings key the user owns** — Claude `statusLine`, `agy` `statusLine`, Pi `settings.json`
`extensions`, OpenCode `tui.json` `plugin` — so the artifact and its activation live in different
places. Second, **some harness roots auto-load**: Pi discovers extensions in
`~/.pi/agent/extensions/`, and OpenCode auto-loads *server* plugins from
`~/.config/opencode/plugins/`. A file placed in an auto-load directory is active the moment it
lands, with no user action and no way to tell whether it was wanted.

A shared settings file is the user's state, not Kyber-Weave's. Writing it — even "helpfully",
create-if-missing, or by merge — means the tool takes ownership of a file it did not author, in a
format that changes between harness versions, and any wrong guess is hard for the user to undo.
That is the boundary the sibling component already drew, and it is worth drawing again before a
second component is tempted to cross it.

## Decision

1. **Kyber Utilities deploys only Kyber-owned files into a Kyber-owned staging location**, and
   records every file it owns, with its SHA-256 digest, in **its own receipt**. The receipt is
   separate from Kyber-Squad's; the two do not share state.
2. **It never creates, edits, merges, backs up, restores, or reads a shared harness settings
   file** — for any harness and any command, including `--dry-run`. It deploys nothing into a
   directory a harness auto-loads.
3. **Activation is manual.** The tool prints the exact snippet, with absolute paths, and the user
   applies it to their own settings. A deployed file is inert until then.
4. **`utilities statusline doctor` never reads settings.** It checks receipt checksums, the
   executable bit, and prerequisites (`jq`, `node`, the harness binary and its version, and
   `kyberdash` for the `agy` variant as a warning), then prints the activation snippet.
5. **Scope is per-user, on macOS and Linux.** Windows is not supported in this slice; the
   implementation specifics are recorded in
   [issue #163](https://github.com/dpalfery/kyber-weave/issues/163).
6. **It is not a wider utility platform.** No plugin registry, no generic utility schema, and no
   second utility type. The slice is one utility — status lines — for four harnesses.

## Alternatives Considered

- **Extend Kyber-Squad to own the settings keys.** Rejected: it contradicts Squad's own
  architecture and the ownership boundary that keeps a deployment tool out of the user's shared
  state. A second component with a narrower job is cheaper than widening the first.
- **Deploy straight into the auto-load directories, so activation is automatic.** Rejected: it
  breaches manual activation, changes a harness's behaviour the instant `deploy` runs, and makes
  "did the user want this?" unanswerable. Staging stays outside every auto-load path.
- **Write the settings key for the user — create-if-missing or a "helpful" merge.** Rejected: the
  file is the user's, its schema drifts across harness versions, and a merge that guesses wrong is
  hard to reverse. It also makes the tool's success depend on state it does not own.
- **Have `doctor` read the settings file to confirm activation.** Rejected: it would parse a file
  the tool does not own and make the health claim depend on the user's edits, which is exactly the
  read the boundary forbids.
- **A generic utility registry, schema, and loader.** Rejected: there is no second utility type,
  so the schema would be built for one example and would constrain the next one arbitrarily.

## Consequences

- **The user performs one manual step per harness**, and the tool cannot detect whether they did.
  `doctor` reports owned files and prerequisites, not activation. This is a real limit and it is
  the price of not reading settings.
- **`deploy` success is not render success.** Because a deployed file is inert until activated, a
  green `deploy` says the files landed, not that a status line draws. The live check is the
  user's, per harness.
- **Kyber Utilities carries its own receipt and lifecycle**, separate from Kyber-Squad's. The same
  file can be owned by one and not the other, and neither touches the other's state.
- **Windows has no implementation.** The specifics of how it would be built live in issue #163,
  not in a `docs/todo` entry, so the plan carries no open Windows work.
- **Snippet drift is undetectable by the tool.** If the printed snippet and the user's actual
  settings diverge, nothing in Kyber Utilities notices, because it does not read the settings.

## Related

- [Kyber Utilities status-line slice plan](../archive/plans/2026-09-28-kyber-utilities-status-line-slice.md)
- [Kyber Utilities overview](../kyber-utilities/README.md)
- [Kyber Utilities adoption and activation guide](../kyber-utilities/onboarding.md)
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — the sibling boundary that rejects
  owning settings files
- [Kyber Utilities: Windows support for status-line deployment](https://github.com/dpalfery/kyber-weave/issues/163)
