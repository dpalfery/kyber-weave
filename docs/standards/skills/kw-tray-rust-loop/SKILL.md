---
name: kw-tray-rust-loop
description: "Run the Kyber-Weave tray (Rust and Tauri) inner loop: format, clippy, tests, the dash typecheck, and the tray IPC contract rule. Load when changing Rust or Tauri code under dash/tray in this repository. For cross-building see rust-cross-build."
---

# Kyber-Weave tray Rust loop

## Inner loop

```bash
cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check
cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings
cargo test --manifest-path dash/tray/src-tauri/Cargo.toml
npm --prefix dash run typecheck
```

Cross-compilation and the platform targets the tray ships for are in `dash/tray/AGENTS.md`. The
standard's bundle names map to Rust triples: `darwin-arm64` is `aarch64-apple-darwin`,
`darwin-x64` is `x86_64-apple-darwin`, and `win-x64` is `x86_64-pc-windows-msvc`. The
`rust-cross-build` skill holds the build procedure.

## IPC

Write the command, event and channel contract down before implementing both sides. A sidecar
is scoped to its exact binary with argument validators.
