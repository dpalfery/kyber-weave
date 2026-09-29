---
id: kyber-utilities/onboarding
title: Kyber Utilities adoption and activation guide
doc-type: onboarding
component: KyberUtilities
source-root: src/KyberWeave.Core/Utilities
owner: dpalfery
last-reviewed: 2026-09-28
status: draft
---

# Kyber Utilities adoption and activation guide

`kyber-weave utilities statusline` deploys Kyber-owned status-line artifacts to per-user
locations, records them in its own receipt, and prints the exact snippet you apply to your own
harness settings. It is **per-user scope, macOS and Linux only**, and it never touches a shared
settings file.

This guide covers the four commands, the per-harness activation snippets, the import and
privacy-review process, and what `doctor` checks. Read
[ADR 0026](../adr/0026-kyber-utilities-owned-files-not-settings.md) for why the boundary is where
it is.

---

## Command reference

All operations are grouped under the `utilities statusline` branch:

```bash
# Stage the Kyber-owned artifacts and print per-harness activation guidance
kyber-weave utilities statusline deploy [--target <harness>] [--dry-run]

# Report ok, missing, or drift for each owned file
kyber-weave utilities statusline status

# Check owned files, the executable bit, and runtime prerequisites
kyber-weave utilities statusline doctor

# Delete receipt-owned, unmodified files
kyber-weave utilities statusline remove
```

`<harness>` is one of `claude`, `agy`, `pi`.

- **`deploy`** writes only Kyber-owned files, beneath a Kyber-owned staging root, and records each
  one with its SHA-256 digest in the Kyber Utilities receipt. `--dry-run` prints the same guidance
  and writes nothing. A file already present that Kyber does not own is refused, never
  overwritten. `deploy` prints the activation snippet for each harness, with absolute paths, and
  states that **you** apply it.
- **`status`** compares each owned file against its recorded digest and reports ok, missing, or
  drift.
- **`doctor`** checks receipt checksums and the executable bit for each owned file, and checks
  runtime prerequisites. It exits `0` when healthy, `1` on issues, and `2` on a client-input
  error — the same convention as `squad doctor`.
- **`remove`** deletes only files the receipt owns and that still match their digest.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Healthy |
| 1 | Issues found |
| 2 | Client-input error |

---

## Activation is manual

A deployed file does nothing until you activate it. Every harness turns a utility on through a
**settings key you own**, so `deploy` prints the snippet and you edit your own settings. Kyber
Utilities never creates, edits, merges, or reads those files — not for any harness, not for any
command, including `--dry-run`.

The snippets below use **placeholder absolute paths**. `deploy` prints each snippet with the real
path filled in. Never use `~`: `agy` does not expand it, and a snippet that depends on shell
expansion is wrong for a settings file.

### Claude Code

`statusLine`, a command that receives JSON on stdin and whose stdout is rendered:

```json
{
  "statusLine": {
    "type": "command",
    "command": "/absolute/path/to/kyber-statusline-claude"
  }
}
```

### Antigravity CLI (`agy`)

`statusLine`, a command that receives JSON on stdin and whose stdout is rendered with ANSI:

```json
{
  "statusLine": {
    "type": "command",
    "command": "/absolute/path/to/kyber-statusline-antigravity"
  }
}
```

`agy` does **not** expand `~`, so the command must be an absolute path. `agy` also supports a
`stack_with_default` option; the Kyber variant renders its own footer.

### Pi

`extensions` in `settings.json`, pointing at the deployed TypeScript extension:

```json
{
  "extensions": ["/absolute/path/to/kyber-statusline-pi.ts"]
}
```

Pi's extension **replaces the whole footer** — there is no stacking — so the variant reproduces
cwd, model, and context itself. Pi auto-discovers extensions in `~/.pi/agent/extensions/`; Kyber
Utilities never deploys there, because a file in that directory is active without your consent.

**OpenCode: not supported in this slice; see [issue #165](https://github.com/dpalfery/kyber-weave/issues/165).**

---

## What `doctor` checks — and never reads

`doctor` checks:

- receipt checksum health and the executable bit for each owned file;
- runtime prerequisites where the import inventory declares them: `jq` and `node`;
- each harness binary and its version;
- for `agy`, whether `kyberdash` is present — a **warning**, not a failure, because the hand-off
  is optional;
- harnesses that are not installed are skipped, not failed.

`doctor` **never reads a harness settings file.** It cannot confirm that you activated a status
line, because activation lives in settings it does not touch. It prints the activation snippet
instead.

---

## The `agy` hand-off to KyberDash (optional)

The `agy` variant replaces any telemetry or KyberDash call in the imported script with a call to
`kyberdash kyber antigravity-statusline`. It hands its stdin payload to that command in the
background, with stdout and stderr discarded, so the status line is never delayed or corrupted. It
is skipped silently when `kyberdash` is not on `PATH`.

`kyberdash kyber antigravity-statusline` writes nothing to stdout and always exits 0, whether it
records, ignores, or rejects a payload — a status-line host renders stdout and may disable a
command that keeps failing. It appends to KyberDash's own local cache file and attributes the data
as harness `antigravity-cli`. See the [KyberDash runbook](../dash/runbook.md) for the command's
stream contract.

---

## Import and privacy review

Kyber Utilities ships a **cleaned-up import** of the developer's existing status-line
implementations. Nothing enters the repository until it has been reviewed and scanned.

1. **Review outside the working tree.** Copy each original into a scratch directory **outside**
   this repository, and review and redact it there. No agent opens the original file where it
   lives, or any settings file beside it.
2. **Scan with gitleaks.** Run `gitleaks dir <scratch-dir> --redact --no-banner` until it exits 0
   with zero findings. A missing gitleaks binary is a **hard stop**, never a skip.
3. **Import only what is clean.** A file enters `products/kyber-utilities/` only after the scan is
   clean. Imported artifacts are **de-personalised**: no absolute home paths, usernames, hostnames,
   account ids, or private endpoints. Paths are derived at run time (`$HOME`, harness environment
   overrides) or come from the deploy step.
4. **Fixtures are synthetic.** Status-line test fixtures are hand-written from published payload
   schemas. They contain no real paths, conversation ids, or account data, and are never captured
   from live sessions.

### Privacy statement

- No secret, token, credential, or private endpoint exists anywhere in this repository or in any
  artifact Kyber Utilities deploys.
- Imported scripts open no network sockets and write no files. The one sanctioned side effect is
  the `agy` variant's background hand-off to `kyberdash kyber antigravity-statusline`, which
  appends to KyberDash's local cache file. It is the only permitted exception.
- A status-line payload is read from stdin and rendered. The tool does not print environment
  values, and a missing field renders as absent, never as `0` — the repository's
  [honest unobservability rule](../rules/honest-unobservability.md).

---

## Guarantees and boundaries

| Constraint | What it means |
|---|---|
| **No settings ownership** | Kyber Utilities never creates, edits, merges, backs up, restores, or reads a shared harness settings file, for any harness or command, including `--dry-run`. |
| **No auto-load deployment** | Deployed files go to a Kyber-owned staging location. Nothing is written into a directory a harness auto-loads (Pi `~/.pi/agent/extensions/`). |
| **Manual activation** | The tool prints guidance; you edit your own settings. A deployed file is inert until you do. |
| **Per-user scope** | macOS and Linux only. Project-scope deployment is out of scope. |
| **Own receipt** | Kyber Utilities records its owned files in its own receipt, separate from Kyber-Squad's. |
| **No wider platform** | No plugin registry, no generic utility schema, no second utility type. |
| **Recorder is the only side effect** | The imported scripts open no sockets and write no files, except the background `kyberdash` hand-off. |
| **Recorder is invisible** | `kyberdash kyber antigravity-statusline` writes nothing to stdout and always exits 0. |
| **No silent fresh authoring** | If no file-based implementation exists for a harness, the slice stops for a decision rather than writing a replacement from scratch. |

---

## Windows

**Windows: not supported; see [issue #163](https://github.com/dpalfery/kyber-weave/issues/163).**
This slice is per-user scope on macOS and Linux only. Issue #163 records how Windows support would
be built: PowerShell variants for the command-type status lines, Windows roots for each harness,
forward-slash or escaped JSON paths in activation guidance, `pwsh -File` instead of the executable
bit, line-ending handling, a `pwsh` prerequisite check, and a Windows CI leg for the artifact
tests.

---

## Related

- [Kyber Utilities overview](README.md)
- [ADR 0026](../adr/0026-kyber-utilities-owned-files-not-settings.md) — the ownership boundary
- [Status-line slice plan](../archive/plans/2026-09-28-kyber-utilities-status-line-slice.md)
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — the sibling boundary that rejects owning settings files
- [KyberDash runbook](../dash/runbook.md) — the `kyberdash kyber antigravity-statusline` stream contract
- [Honest unobservability](../rules/honest-unobservability.md) — missing values render as absent, never zero
- [Windows support](https://github.com/dpalfery/kyber-weave/issues/163)
