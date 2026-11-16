---
id: standards/rust/skills
title: "rust skills"
doc-type: skill
status: current
technology: rust
owner: dpalfery
last-reviewed: 2026-11-16
---

# rust skills

## Inner loop

```bash
cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check
cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings
cargo test --manifest-path dash/tray/src-tauri/Cargo.toml
```

Cross-compilation and the platform targets the tray ships for are in `dash/tray/AGENTS.md`.
