---
id: standards/rust/skills
title: "rust skills"
doc-type: skill
status: current
technology: rust
owner: dpalfery
last-reviewed: 2026-11-22
---

# rust skills

## Inner loop

```bash
cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check
cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings
cargo test --manifest-path dash/tray/src-tauri/Cargo.toml
```

Cross-compilation and the platform targets the tray ships for are in `dash/tray/AGENTS.md`.

## Cross-build

```bash
rustup target add <triple>
cargo build --release --locked --target <triple> --manifest-path dash/tray/src-tauri/Cargo.toml
```

The bundled triples are the three the standard names under Cross-platform.
