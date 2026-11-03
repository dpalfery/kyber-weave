---
name: rust-dev
description: Use when writing Rust outside a Tauri app — crates, CLIs, daemons, error types, async/tokio, Cargo workspaces, or cross-compiling. Do not use for src-tauri, test authorship, or CI/CD.
license: MIT
---

# Rust Developer

Toolchain, error-handling, async, `unsafe`, dependency, and cross-compilation decisions live in
the path declared as **<rust-coding-standard>** in the repository root `AGENTS.md`. Read that
before the references below — this skill is procedure, not policy.

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Errors and Crate Boundaries | Defining error types, choosing where `thiserror` ends and `anyhow` begins, removing `unwrap`/`expect`, shaping a crate's public API | [Errors and Boundaries](./references/errors-and-boundaries.md) |
| Async and Tokio | Runtime setup, spawning and cancelling tasks, blocking work, locks across `.await`, graceful shutdown | [Async and Tokio](./references/async-tokio.md) |
| Build, Lint, and Test Gates | Running `fmt`, `clippy`, build, and the test runner locally on macOS, Linux, or Windows before reporting done | [Gates and Platforms](./references/gates-and-platforms.md) |
| Cross-Compilation and Dependencies | Building for another target triple, TLS without OpenSSL, pinning git dependencies, feature flags | [Cross-Compilation](./references/cross-compilation.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
