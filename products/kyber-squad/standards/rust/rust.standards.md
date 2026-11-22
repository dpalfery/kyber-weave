---
id: standards/rust
title: "Rust coding standard"
doc-type: coding-standard
status: draft
technology: rust
owner: unassigned
last-reviewed: 2026-11-22
---

# Rust coding standard

How Rust is written in this repository, outside a Tauri app's `src-tauri` core (that follows
`<tauri-coding-standard>`). Agents and skills resolve this document as
`<rust-coding-standard>`.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Naming and layout

### Stack

- **Toolchain:** stable Rust, pinned in `rust-toolchain.toml` with the `rustfmt` and `clippy`
  components. Edition 2021 or later, set once in the workspace `Cargo.toml`. Raise the pin
  deliberately, in its own change.
- **Workspace:** one Cargo workspace per repository. Shared dependency versions live in
  `[workspace.dependencies]`; member crates inherit them with `workspace = true`.
- **Lockfile:** `Cargo.lock` is committed. Builds and tests run with `--locked`, which fails
  if the lockfile would change.
- **Async runtime:** tokio, and only tokio. A library crate does not start a runtime; a binary
  starts exactly one, in `main`.

Application code consumes collaborators through parameters or trait objects. It does not
construct clients, connections, or clocks internally, and it does not reach for a global for
something a caller could pass.

### Dependencies

- Prefer crates.io releases. A git dependency is pinned with `rev = "<full commit SHA>"`, never
  `branch` or `tag`, and carries a comment saying why it is not from crates.io.
- **TLS is rustls.** Select it through features (`default-features = false`,
  `features = ["rustls-tls"]`); `openssl-sys` and `native-tls` do not appear in
  `cargo tree`, checked with `cargo tree -i openssl-sys --target all` (no output) and the same
  for `native-tls`. This is what keeps cross-compilation a `rustup target add`.
- A pure-Rust crate is preferred over one with a C dependency, which is what makes
  cross-compilation hard.
- Features are additive: a feature that removes behaviour breaks the first crate that enables
  it alongside another dependent. Platform-specific dependencies go under
  `[target.'cfg(...)'.dependencies]`.
- A new dependency needs a reason in the change description. `cargo deny check` (licenses,
  advisories, duplicates) passes when the repository has a `deny.toml`.

### Cross-platform

The shipped binaries build for:

| OS | Target triple |
|---|---|
| macOS | `aarch64-apple-darwin`, `x86_64-apple-darwin` |
| Linux | `x86_64-unknown-linux-gnu` (or `-musl` for a static binary) |
| Windows | `x86_64-pc-windows-msvc` |

- Paths use `Path` / `PathBuf`, never string concatenation.
- Unix-only APIs sit behind `#[cfg(unix)]` with a `#[cfg(windows)]` counterpart, so clippy on
  one OS cannot hide a build break on another.
- A fully static Linux binary targets `x86_64-unknown-linux-musl`. Linux targets built from
  macOS or Windows use the one linker wrapper the repository names (`cargo zigbuild` or
  `cross`); do not add the other.
- Release automation belongs to `devops`; hand it the list of triples and the build command.

## Coding standards

### Errors

- **Libraries** return a typed error: an `enum` deriving `thiserror::Error`, with `#[source]`
  on wrapped errors so the chain survives. `anyhow::Error` never appears in a library's public
  API.
- **Binaries** (`main`, command handlers, top-level tasks) use `anyhow::Result` and add
  `.context(...)` naming the operation and its input as errors propagate.
- Propagate with `?`, or restructure so the type proves the value exists. Enforce with
  `clippy::unwrap_used` and `clippy::expect_used` set to `deny` outside `#[cfg(test)]`. An
  `expect` on a genuine invariant (a literal regex, a compile-time-checked value) carries a
  message stating the invariant and an `#[allow]` saying why.
- Keep `#[source]` (or `#[from]`) on a wrapped error; never flatten an error into a `String`.
  Convert at the boundary: a library function returns its own error type, and the binary adds
  context as it propagates. An error message is lowercase, has no trailing period, and names
  the thing that failed.
- Public API: items are `pub` only when another crate needs them, `pub(crate)` otherwise.
  Functions take borrowed inputs (`&str`, `&Path`, `impl AsRef<Path>`) and return owned
  outputs. An enum other crates match on is `#[non_exhaustive]` when variants are expected to
  grow.
- Where a value is provably present, restructure so the type says so
  (`let Some(x) = … else { return Err(…) };`) rather than asserting it.
- **No silent failure.** `let _ = fallible();` discards an error; when that is intended, the
  comment says so.

### Async

- Blocking I/O and CPU-heavy work go through `tokio::task::spawn_blocking`;
  `std::thread::sleep` does not appear in async code.
- Libraries expose `async fn`. File I/O and blocking client libraries use `spawn_blocking` or
  the async equivalent (`tokio::fs`); `tokio::time::sleep` replaces `std::thread::sleep`.
- Scope a `std::sync::Mutex` guard so it drops before an `.await`. State that must live across
  an await point uses `tokio::sync::Mutex`, with a comment, as a design smell. Prefer message
  passing (`mpsc`, `watch`) to shared mutable state for long-lived tasks.
- Every spawned task has an owner that awaits its `JoinHandle` or holds it in a `JoinSet`.
  A detached task's panic and error are lost. Long-lived tasks take a cancellation token
  (`tokio_util::sync::CancellationToken` or a `watch` receiver), `select!` on it at each long
  await, and stop when it fires.
- `select!` drops the losing branches, so only race cancel-safe futures; the tokio docs mark
  which methods are.
- Services and daemons shut down gracefully on Ctrl+C and, on Unix, `SIGTERM`: select on the
  signals, cancel the token, then `join_next` the `JoinSet`. A `JoinError` means the task
  panicked or was aborted; propagate it, and with a typed error map it explicitly instead of
  relying on `?`. `SIGTERM` handling sits behind `#[cfg(unix)]`; on Windows,
  `tokio::signal::windows::ctrl_close` covers service stop.

## Testing

### Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code be
testable: dependencies passed in, behaviour behind traits where a test must substitute it, no
global state. A task whose deliverable is tests goes to a developer tier like any other.

## Commands

### The formatter and clippy are the first reviewers

`rustfmt` and `clippy` are authoritative for formatting and mechanical rules. Do not restate
those rules here, and do not merge code that fails them.

- **Formatting:** default `rustfmt` settings. A `rustfmt.toml` exists only to state a choice
  the default gets wrong for this repository.
- **Lints:** `cargo clippy --workspace --all-targets --all-features -- -D warnings`. Every
  warning fails the gate. An `#[allow(...)]` is scoped to the item and carries a comment saying
  why — never a crate-level allow.
- `rust-toolchain.toml` pins the channel and components; do not override it with `+nightly`
  unless this standard says so.
- **Naming:** snake_case for functions, modules, and variables; UpperCamelCase for types and
  traits; SCREAMING_SNAKE_CASE for constants and statics.

## Security and data handling

### `unsafe`

- No `unsafe` in application crates. Crates that need it (FFI, a measured hot path) put it
  behind a safe API in one module, and the crate root carries `#![deny(unsafe_op_in_unsafe_fn)]`.
- The justification is a `// SAFETY:` comment; `clippy::undocumented_unsafe_blocks` is set to
  `deny`.
- Crates with no `unsafe` declare `#![forbid(unsafe_code)]`.

### Per platform

Run the same gates on each OS you can; name any OS you could not run on, and the
`#[cfg(...)]` branches you could not compile, in the completion digest.

- **macOS:** Xcode command-line tools supply the linker; both Apple targets build natively on
  Apple Silicon once the target is added.
- **Linux:** a C toolchain (`build-essential`) for crates whose `build.rs` compiles C; with
  rustls, no `libssl-dev` is needed.
- **Windows:** the MSVC toolchain with the Visual Studio Build Tools "Desktop development with
  C++" workload; run the gates from PowerShell or a Developer prompt.

## Done checklist

### Completion

The same on macOS, Linux, and Windows (PowerShell):

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --all-features -- -D warnings
cargo build --workspace --locked
cargo nextest run --workspace --locked   # or: cargo test --workspace --locked
```

The three commands answer the three questions a finishing worker has. `cargo fmt --check` is the
format check: it reports what it would rewrite and writes nothing. `cargo clippy ... -D warnings`
compiles every target and denies a warning, so it is the build too — run it before the tests, and
`cargo build --locked` when you want the artifacts without the lint pass. The test line is the
gate the review council and CI run. A dev agent never runs the full suite; it runs the tests
covering its change (`cargo nextest run -E 'test(<name>)'`).
