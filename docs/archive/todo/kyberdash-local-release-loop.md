---
id: archive/todo/kyberdash-local-release-loop
title: The local release loop cannot exercise the KyberDash install or update path
doc-type: todo
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-24
status: superseded
---

# The local release loop cannot exercise the KyberDash install or update path

> [!NOTE]
> **Closed 2026-09-24.** `scripts/release-local.sh` now builds the kyberdash
> single-executable for the host RID from a copy of the `build-kyberdash` steps.
> `ReleaseTests.LocalKyberDashBuildMatchesTheReleaseJob` pins that copy to the job.
> `scripts/update-loop.sh` runs the update-side cases listed under *How to verify*. The
> decisions:
>
> - **The build runs by default**, in CI as well as locally. `--no-kyberdash` opts out of
>   it for a local run. Opt-in coverage is how this gap opened, and the build adds about
>   fifteen seconds.
> - **A build failure is fatal.** `dash/` is first-party code under
>   [ADR 0020](../../adr/0020-kyberdash-one-time-fork.md), and CI's TypeScript gates already
>   fail on a broken `npm ci`. `--no-kyberdash` is the explicit way past a broken build.
> - **The below-floor case needs no build**, so it runs even under `--no-kyberdash`.
> - **The tray case runs the real kyberdash** against a `tray.json` fixture, not a stub.
>   Its first run found that `kyber-weave update` never updated the tray. The root
>   `--version` swallowed the updater's `menubar --update --version <v>`, so kyberdash
>   printed its version and exited 0. The same change fixed that with commander's
>   positional options.
> - **The `install.sh` half stays open.** It moved to
>   [install-sh-local-origin](../../todo/install-sh-local-origin.md), because it cannot
>   run until the script can reach the local server.

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

[`distribution.md`](../../distribution.md#verifying-a-release-locally) makes running
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
- How this interacts with [install-sh-local-origin.md](../../todo/install-sh-local-origin.md): the loop
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

## The tray widens the same gap

The KyberDash tray (`dash/tray/`) is now a third artifact the loop does not build, and it is
further out of reach than `kyberdash` is. The `build-kyberdash` job needs Node and `postject`;
`build-tray` needs a Rust toolchain, `tauri build`, and — on macOS — a Developer ID
certificate, an App Store Connect key and a notarization round trip. The last of those cannot
run on a developer machine at all without the team's signing secrets, and
[the signing todo](macos-developer-id-signing.md) is where that dependency lives.

So the loop can never verify the whole tray path locally. What it *can* reach is the part that
does not need a tray to exist:

- **The skip reasons are already exercised.** A loop run on 2026-09-19 against the
  self-updater's tray delegation logged `no tray install recorded in ~/.kyberdash/tray.json;
  install it with 'kyberdash menubar'` and exited 0, which is the behaviour Requirement 15.3
  asks for — an update must not install a surface the user never chose. `--no-menubar`,
  `--no-kyberdash` and the `TrayMinVersion` floor are the same shape and equally reachable.
- **The delegation itself is not.** Proving `kyberdash menubar --update` actually runs needs a
  `tray.json` and a `kyberdash` binary in the loop's throwaway prefix. Writing a `tray.json`
  fixture would exercise the spawn without building a tray at all, which is the cheap half.
- **`TrayMinVersion` is provisional.** No release has published tray assets yet, so the
  constant in `SelfUpdater` names the release the `build-tray` job is *expected* to ship in.
  It has to be confirmed against the first release whose job succeeds; until then the floor is
  an assumption the loop cannot check.

## How to verify

- A local release published at or above the floor installs `kyberdash` beside the CLI, and the
  binary answers `--version`.
- A local release published *below* the floor installs the CLI and MCP and logs the KyberDash
  skip — never the empty-install failure this todo records.
- `kyber-weave update` from an older local build replaces an installed `kyberdash`, leaves an
  absent one absent, and honours `--no-kyberdash`.
- With a `tray.json` fixture in the loop's prefix, `kyber-weave update` spawns
  `kyberdash menubar --update` and turns its non-zero exit into a named failure; without one,
  it logs the skip and still exits 0.
