---
id: standards/rust/rules
title: "rust rules"
doc-type: rule
status: draft
technology: rust
owner: unassigned
last-reviewed: 2026-11-16
---

# rust rules

## Tauri apps

- A crate under `src-tauri`, or one that depends on `tauri`, follows the `tauri` standard as
  well as this one. Fetch it once and follow both.
- When a shared crate is consumed by a Tauri app, agree the crate's public API before editing
  `src-tauri`; edit it only where the task names it.

## Security and data handling

- Justify every `unsafe` block in a comment with the invariant that makes it sound.
