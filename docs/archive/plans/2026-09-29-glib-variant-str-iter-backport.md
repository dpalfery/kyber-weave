---
id: plans/2026-09-29-glib-variant-str-iter-backport
title: Patch GLib VariantStrIter in the KyberDash tray dependency graph
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-29
---

# Patch GLib VariantStrIter in the KyberDash tray dependency graph

## Goal and evidence

Fix [Dependabot alert #68](https://github.com/dpalfery/kyber-weave/security/dependabot/68),
[GHSA-wrw7-89jp-8q8g](https://github.com/advisories/GHSA-wrw7-89jp-8q8g), in the source
used by the Linux tray build. `dash/tray/src-tauri/Cargo.lock` resolves Tauri 2.11.5 through
GTK 0.18.2 to vulnerable `glib 0.18.5`. The advisory marks `glib >= 0.20.0` patched, but
GTK 0.18 requires the 0.18 GLib family. Adding a direct `glib 0.20` dependency would leave
the vulnerable transitive copy in the graph.

The published `glib-0.18.5.crate` archive has SHA-256
`233daaf6e83ae6a12a52055f568f9d7cf4671dabb78ff9560ab6da230ce00ee5`, matching the
lockfile. Its `VariantStrIter::impl_get` still passes `&p` to a C out-parameter. The
[upstream fix](https://github.com/gtk-rs/gtk-rs-core/pull/1343) changes only `let p` to
`let mut p` and `&p` to `&mut p`; that patch applies to this exact source. The existing
`test_variant_str_iter_*` tests exercise iteration, reverse iteration, and `last`.

## Implementation decision

Use a repository-owned backport of the existing `glib 0.18.5` crate so the Tauri GTK 0.18
dependency remains compatible. Keep [alert #68](https://github.com/dpalfery/kyber-weave/security/dependabot/68)
open until the patched source is merged and verified. Tauri's
[GTK 0.19 migration](https://github.com/tauri-apps/tauri/pull/16170) is still a draft with
several upstream dependencies; the linked [Wry draft](https://github.com/tauri-apps/wry/pull/1843)
resolves a newer, patched GLib line and raises the Rust version floor to 1.92. It is the
route for removing the backport after a stable
Tauri release, not a dependency for this fix.

## Work

1. Download `glib-0.18.5.crate` from `static.crates.io`, verify the SHA-256 above before
   extraction, and copy the complete crate into `dash/tray/vendor/glib-0.18.5`. Preserve its
   `LICENSE` and `COPYRIGHT`. Apply exactly the two-line upstream fix in
   `src/variant_iter.rs`; put the source URL, archive hash, upstream PR, and removal trigger
   in `dash/tray/vendor/README.md`. Review the extracted source diff to confirm no other
   crate changes.
2. Add `[patch.crates-io] glib = { path = "../vendor/glib-0.18.5" }` to
   `dash/tray/src-tauri/Cargo.toml`, with a comment naming the advisory and the reason for
   the backport. Regenerate `Cargo.lock` with Cargo; retain the crate's `0.18.5` package
   version so GTK's existing version constraints resolve. Confirm the Linux dependency
   graph uses the local path and contains no registry copy of affected `glib`. Add the
   gtk-rs MIT attribution to `dash/THIRD_PARTY_NOTICES.md`.
3. Extend the tray Rust CI matrix with Ubuntu. Install `libwebkit2gtk-4.1-dev`,
   `build-essential`, `libgtk-3-dev`, `libxdo-dev`, `libssl-dev`,
   `libayatana-appindicator3-dev`, `librsvg2-dev`, and `patchelf` as Tauri's Linux build
   prerequisites. On Linux, run the vendored crate's
   `test_variant_str_iter` tests in release mode and a locked release build of the tray.
   Keep the existing macOS and Windows Rust gates. Add a CI assertion using Cargo metadata
   that the Linux graph resolves `glib 0.18.5` from `dash/tray/vendor/glib-0.18.5`, with
   no registry `glib` in the advisory's affected range.
4. Once the backport is merged and CI proves the resolved source, check alert #68. If
   Dependabot still reports the registry version against the patched path source, close it
   as inaccurate with a link to the reviewed source diff and CI evidence. Capture removal
   of the backport as a KyberDash todo keyed to a released Tauri GTK 0.19 update; only
   remove the patch after a Linux graph check shows `glib >= 0.20.0` and the tray gates pass.

## Verification and closeout

- The extracted crate hash equals the committed lockfile checksum; the source diff against
  the archive contains only the upstream fix.
- Linux `cargo metadata --locked` and `cargo tree -i glib --target x86_64-unknown-linux-gnu`
  resolve the patched path source with no vulnerable registry copy. The release-mode GLib
  iterator tests and locked Linux tray build pass.
- The existing macOS/Windows tray Rust gates pass. Run the repository's KyberDash
  typecheck, lint, test, and reachability gates; tray UI typecheck and tests; and tray Rust
  format, Clippy, and test gates.
- Run `docs validate .` and `docs drift .` for documentation edits. At implementation
  closeout, archive this plan and update the inventory before the PR's
  `docs validate . --merge-ready` gate, which rejects active plans.

## Implementation record

The verified crate was vendored with only the two upstream source lines changed.
Cargo's Linux metadata and reverse dependency tree resolve `glib 0.18.5` from
the local path, and the lockfile no longer selects the registry copy. The macOS
tray Rust gates, release build, tray UI gates, KyberDash gates, and documentation
validation passed locally. The Linux CI job must pass before merging the fix;
alert #68 remains open until then. The future removal condition is tracked in
[`tauri-glib-upstream-removal.md`](../../todo/tauri-glib-upstream-removal.md).
