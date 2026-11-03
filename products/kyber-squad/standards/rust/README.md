---
id: standards/rust
title: "Rust coding standard"
doc-type: coding-standard
status: draft
technology: rust
owner: unassigned
last-reviewed: 2026-10-21
---

# Rust coding standard

How Rust is written in this repository, outside a Tauri app's `src-tauri` core (that follows
`<tauri-coding-standard>`). Agents and skills resolve this document as
`<rust-coding-standard>`.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

- **Toolchain:** stable Rust, pinned in `rust-toolchain.toml` with the `rustfmt` and `clippy`
  components. Edition 2021 or later, set once in the workspace `Cargo.toml`. Raise the pin
  deliberately, in its own change.
- **Workspace:** one Cargo workspace per repository. Shared dependency versions live in
  `[workspace.dependencies]`; member crates inherit them with `workspace = true`.
- **Lockfile:** `Cargo.lock` is committed. Builds and tests run with `--locked`.
- **Async runtime:** tokio, and only tokio. A library crate does not start a runtime; a binary
  starts exactly one, in `main`.

Application code consumes collaborators through parameters or trait objects. It does not
construct clients, connections, or clocks internally, and it does not reach for a global for
something a caller could pass.

## The formatter and clippy are the first reviewers

`rustfmt` and `clippy` are authoritative for formatting and mechanical rules. Do not restate
those rules here, and do not merge code that fails them.

- **Formatting:** default `rustfmt` settings. A `rustfmt.toml` exists only to state a choice
  the default gets wrong for this repository.
- **Lints:** `cargo clippy --workspace --all-targets --all-features -- -D warnings`. Every
  warning fails the gate. An `#[allow(...)]` is scoped to the item and carries a comment saying
  why — never a crate-level allow.
- **Naming:** snake_case for functions, modules, and variables; UpperCamelCase for types and
  traits; SCREAMING_SNAKE_CASE for constants and statics.

## Errors

- **Libraries** return a typed error: an `enum` deriving `thiserror::Error`, with `#[source]`
  on wrapped errors so the chain survives. `anyhow::Error` never appears in a library's public
  API.
- **Binaries** (`main`, command handlers, top-level tasks) use `anyhow::Result` and add
  `.context(...)` naming the operation and its input as errors propagate.
- **No `unwrap()` or `expect()` in non-test code.** Propagate with `?`, or restructure so the
  type proves the value exists. Enforce it with `clippy::unwrap_used` and
  `clippy::expect_used` set to `deny` outside `#[cfg(test)]`. An `expect` on a genuine
  invariant (a literal regex, a compile-time-checked value) is allowed with a message stating
  the invariant and an `#[allow]` saying why.
- **No silent failure.** `let _ = fallible();` discards an error; when that is intended, the
  comment says so.

## Async

- Never block the runtime: blocking I/O and CPU-heavy work go through
  `tokio::task::spawn_blocking`; `std::thread::sleep` does not appear in async code.
- Never hold a `std::sync::Mutex` guard across an `.await`.
- Every spawned task has an owner that awaits its `JoinHandle` or holds it in a `JoinSet`.
  Long-lived tasks take a cancellation token and stop when it fires.
- Services and daemons shut down gracefully on Ctrl+C and, on Unix, `SIGTERM`.

## `unsafe`

- No `unsafe` in application crates. Crates that need it (FFI, a measured hot path) put it
  behind a safe API in one module, and the crate root carries `#![deny(unsafe_op_in_unsafe_fn)]`.
- Every `unsafe` block has a `// SAFETY:` comment stating the invariant that makes it sound.
  `clippy::undocumented_unsafe_blocks` is set to `deny`.
- Crates with no `unsafe` declare `#![forbid(unsafe_code)]`.

## Dependencies

- Prefer crates.io releases. A git dependency is pinned with `rev = "<full commit SHA>"`, never
  `branch` or `tag`, and carries a comment saying why it is not from crates.io.
- **TLS is rustls.** Select it through features (`default-features = false`,
  `features = ["rustls-tls"]`); `openssl-sys` and `native-tls` do not appear in
  `cargo tree`. This is what keeps cross-compilation a `rustup target add`.
- Features are additive. Platform-specific dependencies go under
  `[target.'cfg(...)'.dependencies]`.
- A new dependency needs a reason in the change description. `cargo deny check` (licenses,
  advisories, duplicates) passes when the repository has a `deny.toml`.

## Cross-platform

The shipped binaries build for:

| OS | Target triple |
|---|---|
| macOS | `aarch64-apple-darwin`, `x86_64-apple-darwin` |
| Linux | `x86_64-unknown-linux-gnu` (or `-musl` for a static binary) |
| Windows | `x86_64-pc-windows-msvc` |

- Paths use `Path` / `PathBuf`, never string concatenation.
- Unix-only APIs sit behind `#[cfg(unix)]` with a `#[cfg(windows)]` counterpart, so clippy on
  one OS cannot hide a build break on another.
- Release automation belongs to `github-devops`.

## Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code be
testable: dependencies passed in, behaviour behind traits where a test must substitute it, no
global state. Test authorship belongs to `test-dev`.

## Commands

The same on macOS, Linux, and Windows (PowerShell):

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --all-features -- -D warnings
cargo build --workspace --locked
cargo nextest run --workspace --locked   # or: cargo test --workspace --locked
```
