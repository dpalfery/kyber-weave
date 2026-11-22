---
id: standards/rust/skills
title: "rust skills"
doc-type: skill
status: draft
technology: rust
owner: unassigned
last-reviewed: 2026-11-22
---

# rust skills

## What this folder covers

- Standards, in `rust.standards.md`: errors and crate boundaries, and async and tokio (Coding
  standards); dependencies, TLS, targets and cross-platform (Naming and layout); `unsafe`
  (Security and data handling); the gates and per-platform notes (Commands).
- Cross-build procedure: `rustup target add <triple>`, then
  `cargo build --release --target <triple> --locked`; confirm no OpenSSL with
  `cargo tree -i openssl-sys --target all`.

## Hand-off

When ceding work to delivery, provide the target triples, the build, lint and test commands,
and any native toolchain need.
