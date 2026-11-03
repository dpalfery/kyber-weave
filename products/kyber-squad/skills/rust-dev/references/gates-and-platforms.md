---
name: rust-dev/gates-and-platforms
description: Running rustfmt, clippy with warnings denied, build, and the test runner on macOS, Linux, and Windows before reporting done.
---

# Build, Lint, and Test Gates

The exact commands, flags, and toolchain pin are in **<rust-coding-standard>**. When the
standard names a command, run that one; the table below is the shape it usually takes.

| Gate | Command | Passes when |
|---|---|---|
| Format | `cargo fmt --all -- --check` | Exit 0, no diff |
| Lint | `cargo clippy --workspace --all-targets --all-features -- -D warnings` | Exit 0; every warning is an error |
| Build | `cargo build --workspace --locked` | Exit 0; `--locked` fails if `Cargo.lock` would change |
| Test | `cargo nextest run --workspace` or `cargo test --workspace` | Exit 0 — authored by `test-dev`, but run it to prove you broke nothing |

Fix the cause of a clippy finding. An `#[allow(clippy::…)]` needs a comment saying why, and is
scoped to the item, never the crate.

## Toolchain

`rust-toolchain.toml` pins the channel and components (`rustfmt`, `clippy`) so every machine
runs the same compiler. `rustup` honours it automatically; do not override it with `+nightly`
unless the standard says so.

## Per platform

**macOS** — Xcode command-line tools (`xcode-select --install`) supply the linker. Both
`aarch64-apple-darwin` and `x86_64-apple-darwin` build natively on Apple Silicon once the
target is added.

**Linux** — needs a C toolchain (`build-essential` or equivalent) for crates with a `build.rs`
that compiles C. With rustls instead of OpenSSL (see the cross-compilation reference), no
`libssl-dev` is required.

**Windows** — use the MSVC toolchain (`x86_64-pc-windows-msvc`) with the Visual Studio Build
Tools "Desktop development with C++" workload. Run the gates from PowerShell or a Developer
prompt; the commands are identical. Watch for:

- Path handling — use `Path`/`PathBuf`, never string concatenation with `/`.
- Line endings — `rustfmt` normalizes; a `.gitattributes` keeps checkouts consistent.
- Signals — Unix-only APIs (`SIGTERM`, `nix`, `libc::fork`) must sit behind `#[cfg(unix)]`
  with a Windows branch, or clippy passes on one OS and the build fails on the other.

When you can run on only one OS, say which in the completion digest and name the
`#[cfg(...)]` branches you could not compile.
