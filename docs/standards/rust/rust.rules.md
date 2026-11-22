---
id: standards/rust/rules
title: "rust rules"
doc-type: rule
status: current
technology: rust
owner: dpalfery
last-reviewed: 2026-11-22
---

# rust rules

## The tray crate

- `dash/tray/src-tauri` is the tray's Rust crate. It displays the engine's data and holds no
  feature logic, scheduler or job.
- The Tauri layer of that crate also follows the `tauri` standard; language rules here apply
  to all of it.
- Warnings are errors; a new warning is a change defect.

## Errors and async

- No `unwrap()` or `expect()` in new or changed non-test code outside a stated invariant; the
  existing exceptions are listed in the standard.
- Never hold a `std::sync::Mutex` guard across an `.await`, and never block the async runtime.

## Commands

- Run cargo with the manifest path, never from a parent directory:
  `cargo <verb> --manifest-path dash/tray/src-tauri/Cargo.toml`.
