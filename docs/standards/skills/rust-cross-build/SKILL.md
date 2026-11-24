---
name: rust-cross-build
description: "Cross-build a Rust crate for a target triple with a locked release build, and confirm that no OpenSSL dependency is pulled into the build. Load when preparing a Rust release build for another target or when checking that a Rust build does not depend on openssl-sys."
---

# Rust cross-build

## Procedure

Cross-build procedure: `rustup target add <triple>`, then
`cargo build --release --target <triple> --locked`; confirm no OpenSSL with
`cargo tree -i openssl-sys --target all`.
