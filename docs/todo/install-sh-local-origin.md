---
id: todo/install-sh-local-origin
title: install.sh cannot be exercised by the local update loop
doc-type: todo
component: Distribution
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# install.sh cannot be exercised by the local update loop

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

`scripts/update-loop.sh` proves `kyber-weave update` and `kyber-weave squad install` against
locally published binaries served from loopback. It cannot prove `scripts/install.sh`, which
is the documented *first-install* channel and the one every new user hits first.

The CLI reaches the local server because `ReleaseOrigin` honours
`KYBER_WEAVE_RELEASE_ORIGIN` (`src/KyberWeave.Cli/Update/ReleaseOrigin.cs`). `install.sh` has
no equivalent: `RELEASE_BASE`, `LATEST_API`, and `RELEASES_API` are fixed at the top of the
script, and both `fetch` and `fetch_stdout` reject any URL that does not start with
`https://`. So the loop stages its "from" binaries with `cp` instead, and the installer's own
download, checksum-matching, and atomic-replace logic goes untested until a real release
exists.

That gap is not hypothetical. The installer has its own `awk`-based SHA256SUMS parser,
distinct from the C# one in `ChecksumVerifier`, and its own `cp`-then-`mv` replace sequence,
distinct from `BinaryInstaller.Replace`. Neither has a local test.

## What is known

- The C# side already settled the security shape of this: an override is accepted only for a
  loopback authority, and plain HTTP is tolerated only for loopback under an active override.
  `ReleaseOrigin.Resolve` and `ReleaseOrigin.EnsureAllowed` are the reference behaviour, and
  `ReleaseOriginTests` pins it.
- `install.sh` already reads five `KYBER_WEAVE_*` environment variables, so the naming and
  the flag/env pairing convention exist.
- `scripts/local-release-server.py` already serves the exact asset paths `install.sh` builds
  (`<origin>/<owner>/<repo>/releases/download/<tag>/<file>`), so no server work is needed.

## What needs deciding

- Whether the shell override should be the same variable name as the CLI's
  (`KYBER_WEAVE_RELEASE_ORIGIN`) or a distinct one. Same name is simpler for the harness to
  export once; distinct names make it harder to accidentally redirect both at once.
- How to express the loopback restriction in POSIX `sh` without a URL parser. A prefix match
  against `http://127.0.0.1:`, `http://localhost:`, and `http://[::1]:` is crude but has no
  false positives; a `case` on the authority is closer to the C# rule.
- Whether `curl --proto '=https'` should become `--proto '=http,https'` only when the
  override is active, or whether the loopback branch should bypass the `--proto` guard
  entirely.

## The code seam

- `scripts/install.sh` lines 22–26 (the endpoint constants) and the two `case "$1" in
  https://*)` guards in `fetch` / `fetch_stdout`.
- `scripts/update-loop.sh` `stage_from_directory` — replace the `cp` staging with a real
  `install.sh --install-dir "$BIN" --version <from>` invocation once the override lands.

## How to verify

- The loop stages its "from" side through `install.sh` against the loopback server and still
  passes every existing check.
- `install.sh` with the override unset, or set to a non-loopback host, still refuses any
  non-HTTPS URL — the existing `refusing non-HTTPS URL` path must stay reachable.
- A corrupted asset in the local release tree makes `install.sh` fail on the checksum
  comparison rather than installing it, which is the assertion the current loop cannot make.
