---
id: kyber-utilities-index
title: Kyber Utilities — Owned Per-Harness Utility Deployment
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-09-28
---

# Kyber Utilities — Owned Per-Harness Utility Deployment

> **Deploy Kyber-owned per-harness utility artifacts to per-user locations, record them in a receipt, and hand the user a snippet to activate — without ever touching their settings files.**

A developer runs several coding harnesses side by side — Claude Code, the Antigravity CLI
(`agy`), and Pi — and each keeps its own copy of the same small utilities: a status
line, a footer, a context readout. Every harness activates that utility through a **settings key
the user owns**, so the artifact and its activation live in two different places, and nothing
keeps the artifact versioned, diagnosed, or honest about what it renders.

**Kyber Utilities** is the deployment control plane for those artifacts. It stages Kyber-owned
files at per-user locations, records every file it owns with a SHA-256 digest in its own receipt,
and prints the exact activation snippet — with absolute paths — for the user to apply themselves.
The first slice ships the `statusline` utility for three harnesses.

It is deliberately **not** a settings manager. It never creates, edits, merges, or reads a shared
harness settings file, and it deploys nothing into a directory a harness auto-loads. That boundary
is what makes it safe to run: a deployed file is inert until the user activates it.

---

## Why Kyber Utilities?

Utility artifacts — status lines above all — break down in three ways once a developer runs more
than one harness.

### 1. Utility artifacts have no owner and no lifecycle
A status line lives as a loose script somewhere under `$HOME`, copied by hand into each harness,
versioned nowhere. When it breaks, there is no receipt to say which files were deployed, no digest
to say whether they changed, and no command to diagnose or remove them.

### 2. Activation is a settings edit the user owns
Each harness turns a utility on through a settings key: Claude and `agy` `statusLine`, and Pi
`settings.json` `extensions`. A deployment tool that writes that key
has taken ownership of the user's state, in a format that drifts across harness versions. Kyber
Utilities refuses to, and prints the snippet instead.

### 3. A deployed file must not be an active file
Pi auto-discovers extensions in `~/.pi/agent/extensions/`. A file placed there is active the
moment it lands. Kyber Utilities stages files where nothing auto-loads them, so `deploy` never
changes a harness's behaviour on its own.

---

## Core capabilities

| Capability | How it solves the problem | Command |
|---|---|---|
| **Owned-file deployment** | Stages Kyber-owned artifacts at per-user locations and records every file with its SHA-256 digest in a Kyber Utilities receipt — separate from Kyber-Squad's. | `kyber-weave utilities statusline deploy` |
| **Manual activation guidance** | Prints the exact per-harness snippet, with absolute paths, for the user to apply to their own settings. Never writes a settings file. | `kyber-weave utilities statusline deploy` |
| **Receipt-aware status** | Reports ok, missing, or drift for each owned file by comparing it against its recorded digest. | `kyber-weave utilities statusline status` |
| **Owned-file and prerequisite doctor** | Checks receipt checksums and the executable bit, plus runtime prerequisites, then prints the activation snippet. Never reads a settings file. | `kyber-weave utilities statusline doctor` |
| **Ownership-aware removal** | Deletes only receipt-owned, unmodified files. | `kyber-weave utilities statusline remove` |

---

## What Kyber Utilities never does

- It never creates, edits, merges, backs up, restores, or reads a shared harness settings file —
  for any harness, any command, including `--dry-run`.
- It deploys nothing into a directory a harness auto-loads.
- It is not a wider utility platform: there is no plugin registry, no generic utility schema, and
  no second utility type. The slice is one utility for three harnesses.

The reasoning behind that boundary is [ADR 0025](../adr/0025-kyber-utilities-owned-files-not-settings.md).

---

## Supported harnesses

| Harness | Utility | Activation surface (user-edited; never written by Kyber) |
|---|---|---|
| Claude Code | Status line (`type: command`) | `statusLine` in the user's Claude settings |
| Antigravity CLI (`agy`) | Status line (`type: command`) | `statusLine` in the `agy` settings — `agy` does not expand `~` |
| Pi | Footer extension | `settings.json` `extensions` |

Gemini CLI is out of scope; the Gemini-family target is `agy`.

**OpenCode: not supported in this slice; see [issue #165](https://github.com/dpalfery/kyber-weave/issues/165).**

**Windows: not supported; see [issue #163](https://github.com/dpalfery/kyber-weave/issues/163).**
This slice is per-user scope on macOS and Linux only.

---

## Jump In

Explore the Kyber Utilities documentation:

- **[Adoption & activation guide](onboarding.md)** — the four commands, per-harness activation
  snippets, the import and privacy-review process, and what `doctor` checks.
- **[ADR 0025](../adr/0025-kyber-utilities-owned-files-not-settings.md)** — why it deploys owned
  files and never owns harness settings, and why activation is manual.
- **[Status-line slice plan](../plans/2026-09-28-kyber-utilities-status-line-slice.md)** — the
  approved slice that ships this component.
- **[Windows support](https://github.com/dpalfery/kyber-weave/issues/163)** — how Windows support
  would be built (deferred).
