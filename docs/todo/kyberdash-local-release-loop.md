---
id: todo/kyberdash-local-release-loop
title: The local release loop cannot exercise the KyberDash install or update path
doc-type: todo
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-10
status: draft
---

# The local release loop cannot exercise the KyberDash install or update path

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

[`distribution.md`](../distribution.md#verifying-a-release-locally) makes running
`./scripts/update-loop.sh` a requirement for changes to `install.sh` or the self-updater,
on the reasoning that a self-updater is always executed by the *old* binary and so cannot be
proven by the release containing the fix. That loop builds `kyber-weave`, `kyber-weave-mcp`,
and the Squad archives. It does not build `kyberdash`.

So the KyberDash install path shipped without ever running locally, and the failure that
followed was exactly the class the loop exists to catch: `install.sh` gained an unconditional
`kyberdash-<rid>` download, no published release carried that asset, and because every archive
is verified before any binary is placed, the documented one-line install placed **nothing** —
no CLI, no MCP — against every stable release. It was reachable only by running the real
script against a real release, which is what the loop simulates.

A second defect sat behind it in the same untested region: the macOS quarantine strip and
`--with-menubar` both read `os_part`, which is assigned inside `kyber_weave_resolve_rid` and
therefore only ever exists in that function's command-substitution subshell. Under `set -u`
both were an abort. Both are fixed now, and both would have been caught on first local run.

## What is known

- `scripts/release-local.sh` publishes one RID with `release.yml`'s exact flags. The KyberDash
  equivalent is the `build-kyberdash` job in `.github/workflows/release.yml`: download a
  prebuilt Node from `nodejs.org/dist`, `npx tsup --config tsup.sea.config.ts`, generate the
  SEA blob, `postject` it into the Node binary, and ad-hoc `codesign` on macOS.
- That job is self-contained and does not depend on the runner beyond Node and `postject`, so
  the same steps run on a developer machine for the **host** RID. Cross-RID builds are the part
  that does not: the workflow already skips its own smoke test for a cross-built RID.
- `scripts/local-release-server.py` serves whatever asset names are present in
  `.local-release/v<version>/`, so a `kyberdash-<rid>.tar.gz` needs no server change.
- The version floor is now the thing most worth exercising:
  `KYBERDASH_MIN_VERSION` in `install.sh`, `SelfUpdater.KyberDashMinVersion` in the updater.
  A local release can be published under any version, so both sides of the floor are testable
  without inventing a fixture.
- Host-RID-only coverage would still be a real gate. Every defect above was RID-independent.

## What needs deciding

- Whether the loop builds KyberDash by default or behind a flag. The SEA build downloads a
  ~50 MB Node archive and installs `postject`, which is a meaningful cost on a loop currently
  fast enough to run per-change. A `--with-kyberdash` flag keeps the default cheap but makes
  the coverage opt-in, which is how this gap arose in the first place.
- Whether a build failure is fatal to the loop or reported and skipped. The `dash/` subtree is
  vendored and its toolchain is not the .NET one; a broken `npm ci` there should probably not
  block verification of a Kyber-Weave self-updater change.
- Whether to assert the skip path as well as the install path — publishing a local release
  *below* the floor and asserting the CLI and MCP still install is the regression test for the
  exact failure this todo describes, and it needs no KyberDash build at all. That half is cheap
  and could land independently of the SEA build.
- How this interacts with [install-sh-local-origin.md](install-sh-local-origin.md): the loop
  drives `kyber-weave update`, not `install.sh`, because the script has no origin override. The
  install-side assertions here are blocked on that todo, while the update-side ones are not.

## The code seam

- `scripts/release-local.sh` — publishes one RID into `.local-release/v<version>/` and writes
  `SHA256SUMS.txt`; a KyberDash archive would join that directory and that manifest.
- `.github/workflows/release.yml`, job `build-kyberdash` — the steps to mirror, and the
  authority on the fuse string, the `--macho-segment-name`, and the post-inject codesign.
- `dash/tsup.sea.config.ts`, `dash/src/sea-shim.cjs` — the ESM-bundle-plus-CJS-shim arrangement
  the SEA build depends on; SEA-only files, so the vendored subtree stays clean.
- `scripts/update-loop.sh` — drives the pieces and asserts the outcome.

## How to verify

- A local release published at or above the floor installs `kyberdash` beside the CLI, and the
  binary answers `--version`.
- A local release published *below* the floor installs the CLI and MCP and logs the KyberDash
  skip — never the empty-install failure this todo records.
- `kyber-weave update` from an older local build replaces an installed `kyberdash`, leaves an
  absent one absent, and honours `--no-kyberdash`.
