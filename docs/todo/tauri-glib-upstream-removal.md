---
id: todo/tauri-glib-upstream-removal
title: Remove the GLib backport after Tauri updates its Linux GTK stack
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-09-29
---

# Remove the GLib backport after Tauri updates its Linux GTK stack

KyberDash's Linux tray source build uses a local backport of `glib 0.18.5` for
[GHSA-wrw7-89jp-8q8g](https://github.com/advisories/GHSA-wrw7-89jp-8q8g).
The source and patch provenance are in
[`dash/tray/vendor/README.md`](../../dash/tray/vendor/README.md).

When Tauri releases its GTK migration, update Tauri and the lockfile, then use
`cargo metadata --locked` and `cargo tree -i glib --target x86_64-unknown-linux-gnu`
to verify that the Linux graph resolves only `glib >= 0.20.0`. Remove the
`[patch.crates-io]` entry and vendored source, and run the Linux tray build,
iterator tests, and all tray gates before closing this todo.
