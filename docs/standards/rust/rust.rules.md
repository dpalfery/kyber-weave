---
id: standards/rust/rules
title: "rust rules"
doc-type: rule
status: current
technology: rust
owner: dpalfery
last-reviewed: 2026-11-16
---

# rust rules

## The tray crate

- `dash/tray/src-tauri` is the tray's Rust crate. It displays the engine's data and holds no
  feature logic, scheduler or job.
- The Tauri layer of that crate also follows the `tauri` standard; language rules here apply
  to all of it.
- Warnings are errors; a new warning is a change defect.

## Commands

- Run cargo with the manifest path, never from a parent directory:
  `cargo <verb> --manifest-path dash/tray/src-tauri/Cargo.toml`.
