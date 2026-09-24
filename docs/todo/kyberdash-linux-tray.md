---
id: todo/kyberdash-linux-tray
title: The KyberDash tray has no Linux build
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-09-24
---

# The KyberDash tray has no Linux build

Deferred by the KyberDash context-surfaces specification (Requirement 13.4). The tray ships
for macOS and Windows only; `kyberdash menubar` on Linux exits 1 with the platform message
(`dash/src/install/menubar.ts`, Requirement 12.8), and `release.yml` builds no Linux tray.

## What is known

- The Rust core already compiles its launch-at-login path for Linux
  (`dash/tray/src-tauri/src/autostart.rs` is gated on `windows` and `linux`), but no
  installer, asset name, or `kyberdash menubar` path exists for it.
- Linux tray support differs by desktop environment (StatusNotifierItem vs. legacy
  XEmbed trays), which is the main design question.

## What needs deciding

- Whether a Linux tray is worth shipping, and for which desktop environments.
- The package format (AppImage, `.deb`) and how `kyberdash menubar` would install it.
