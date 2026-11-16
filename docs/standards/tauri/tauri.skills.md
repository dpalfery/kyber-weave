---
id: standards/tauri/skills
title: "tauri skills"
doc-type: skill
status: current
technology: tauri
owner: dpalfery
last-reviewed: 2026-11-16
---

# tauri skills

## Commands

```bash
cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check
cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings
cargo test --manifest-path dash/tray/src-tauri/Cargo.toml
npm --prefix dash run typecheck
```

## IPC

Write the command, event and channel contract down before implementing both sides. A sidecar
is scoped to its exact binary with argument validators.
