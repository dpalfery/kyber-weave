---
id: standards/rust
title: Rust coding standard
doc-type: coding-standard
status: current
technology: rust
owner: dpalfery
last-reviewed: 2026-10-27
---

# Rust coding standard

How Rust is written in this repository. Agents and skills resolve this document as
`<rust-coding-standard>` in the repository root [`AGENTS.md`](../../../AGENTS.md), so what it
says here outranks the defaults a portable agent shipped with.

Every first-party Rust file in this repository is in one crate (vendored third-party code
under `dash/tray/vendor/` is not first-party): the KyberDash menu-bar and
notification-area tray at [`dash/tray/src-tauri/`](../../../dash/tray/src-tauri/Cargo.toml)
(package `kyberdash-tray`, library `kyberdash_tray_lib`, binary `kyberdash-tray`). This standard
covers that crate's Rust. Its Tauri-specific rules (commands, IPC, capabilities, the webview
boundary) are in the [Tauri coding standard](../tauri/README.md), which defers to this document
for everything below. The shared application and display-layer boundaries are defined by
[KyberDash architecture](../../dash/architecture.md) and the
[display-layer rule](../../rules/kyberdash-display-layer.md).

This is a local decision record, not a summary of the language. Rules that the compiler,
`rustfmt`, or `clippy` already enforce are not restated.

## Crate layout and toolchain

- The tray is a standalone crate, not a member of a Cargo workspace. Its manifest declares
  `edition = "2021"` and `rust-version = "1.77"`. Raise either deliberately, in its own change.
- `Cargo.lock` is committed. Keep it committed for this binary crate.
- There is no `rust-toolchain.toml`. The toolchain is whatever `rustup` resolves for the
  developer, and the gates below run against it.
- The crate's `Cargo.toml` pins a vendored path patch for `glib` under `[patch.crates-io]`,
  with a comment naming the advisory it works around. A patch entry carries that comment, and
  it is removed when the upstream stack no longer needs it.

## Formatting and lints

`rustfmt` and `clippy` are the first reviewers. Do not restate their rules here, and do not
merge code that fails them.

- Formatting uses default `rustfmt` settings; the crate has no `rustfmt.toml`.
- Clippy runs with every warning denied. An `#[allow(...)]` is scoped to the smallest item
  that needs it and carries a comment saying why. A crate-level `allow` is not used.
- Naming follows the Rust API guidelines: `snake_case` for functions, modules, and variables;
  `UpperCamelCase` for types and traits; `SCREAMING_SNAKE_CASE` for constants and statics.

## Errors

- **Typed errors at command boundaries.** A Tauri command returns a crate-local error enum
  derived with `thiserror`, serialized as a tagged `{ kind, message }` payload. `CommandError`
  in [`lib.rs`](../../../dash/tray/src-tauri/src/lib.rs) is the model.
- **`anyhow` in the binary path.** Startup, supervision, and filesystem code returns
  `anyhow::Result` and adds `.context(...)` naming the operation and its input as an error
  propagates. Settings and login-item code are examples.
- **No `unwrap()` or `expect()` in new or changed non-test code.** Propagate with `?`, or
  restructure so the type proves the value exists. An `expect` on a stated invariant (a value
  compiled into the binary, a window declared in `tauri.conf.json`) is allowed when its message
  states the invariant. The existing exceptions are listed in [Existing exceptions](#existing-exceptions).
- **No silent failure.** `let _ = fallible();` discards an error. When that is intended, the
  comment on the line says so. Status-item and tray updates that cannot fail the caller are the
  usual case.
- **Callers see a failure as a state, not a panic.** A supervision or polling failure becomes
  a phase the popover shows, as `supervisor.rs` does. It does not abort the process.

## Process and async work

- The tray runs blocking work off the async runtime. Filesystem, process, and blocking HTTP
  work reached from a Tauri command goes through `tauri::async_runtime::spawn_blocking`, as
  `lib.rs` does. `std::thread::sleep` does not appear in async code.
- A process the tray starts is owned by a `Supervisor` and killed by it. Quitting stops every
  process the tray launched. A server the tray merely attached to is never stopped by the tray
  (see `supervisor.rs`).
- A `std::sync::Mutex` guard is not held across an `.await`.

## Dependencies

- Prefer crates.io releases. A git dependency is pinned by full commit SHA, never by branch
  or tag, and carries a comment saying why it is not from crates.io.
- Use the dependency already in the lockfile for an existing capability. The tray's HTTP client
  is `reqwest` with the blocking feature; a second HTTP client is a finding.
- A new dependency needs a reason in the change description. `cargo deny` is not configured:
  there is no `deny.toml` in the crate, so no licence or advisory gate runs today.
- Platform-only dependencies sit under `[target.'cfg(...)'.dependencies]`, as `libc`,
  `objc2`, and `windows-sys` already do.

## `unsafe`

- `unsafe` is not added to the tray. When one is unavoidable (a platform call with no safe
  binding), it is confined to a small function behind a safe signature.
- Every `unsafe` block has a `// SAFETY:` comment stating the invariant that makes it sound.
  See `supervisor.rs` for the form.
- The existing `unsafe` blocks in `tray_badge.rs` are listed in [Existing exceptions](#existing-exceptions).

## Cross-platform

The bundled targets are `darwin-arm64`, `darwin-x64`, and `win-x64`, as the `Cargo.toml` header
states, and the bundle targets in `tauri.conf.json` are macOS and Windows installers.

- Paths use `Path` and `PathBuf`. Joining with string concatenation is a finding.
- Platform code sits behind `#[cfg(unix)]`, `#[cfg(windows)]`, or `#[cfg(target_os = "macos")]`
  with a counterpart for each other platform the crate builds on. `pid_is_alive` in
  `supervisor.rs` is the model.
- A change that touches a `cfg`-gated item is checked on the platform it names, because clippy
  on one OS does not compile the other OS's branch.

## Tests

- Unit tests live in a `#[cfg(test)] mod tests` beside the code they exercise. Contract tests
  that pin a file or manifest live in `dash/tray/src-tauri/tests/`.
- Code is testable by construction: collaborators (spawner, clock, fetcher, sink, opener)
  are passed in, and the tray's runtime takes them as trait objects, so a test substitutes a
  fake for every system effect.
- Test authorship follows the [test coding standard](../test/README.md) and belongs to
  `test-dev`.

## Commands

The gate that a Rust change must pass, run from the crate directory:

```bash
cd dash/tray/src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo test
```

The same three commands are declared in [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml)
as council gates `cargo-fmt`, `cargo-clippy` and `cargo-test` under `review.gates`, run from the
repository root with `--manifest-path dash/tray/src-tauri/Cargo.toml`. The council runs them on every
review. `cargo-test` runs in the council only: no task gate runs `cargo test`.

Developer agents run `cargo-fmt` and `cargo-clippy` as task gates for changes under
`dash/tray/src-tauri/`: `rust-dev` and `tauri-dev` declare both, and `test-dev` declares both as well.

Prerequisites: `cargo` must be on the `PATH` (installed through `rustup`). On Linux, the Tauri
system packages listed in `TAURI_APT_PACKAGES` in [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml)
must be installed, or clippy and the tests cannot build the Tauri dependency tree.

The popover UI that the same tray ships has its own gates, listed in the root
[`AGENTS.md`](../../../AGENTS.md) and in [`dash/tray/AGENTS.md`](../../../dash/tray/AGENTS.md).

## Existing exceptions

These places in the tray predate this standard. They are not exempt from it for new or changed
code, and a change that touches one brings it to the rule where that is reasonable.

- `anyhow::Result` is returned from public functions of the library crate (for example
  `api::report_url` and `ipc::open_view_url`), where the rule says a library returns a typed
  error. The library and binary share one crate, so the public surface is the crate's.
- `expect` in production code: `ipc.rs` (`view_path_patterns`, a compiled-in JSON list),
  `lib.rs` (the `popover` window lookup, and the `tauri::generate_context!` build call).
- `unsafe` in `tray_badge.rs` (Windows small-icon metric) has no `// SAFETY:` comment, and the
  crate root does not carry `#![deny(unsafe_op_in_unsafe_fn)]`.
- `clippy::unwrap_used`, `clippy::expect_used`, and `clippy::undocumented_unsafe_blocks` are not
  set to `deny`; the gate is `-D warnings` only.
