---
id: standards/tauri
title: "Tauri coding standard"
doc-type: coding-standard
status: draft
technology: tauri
builds-on: rust
owner: unassigned
last-reviewed: 2026-09-27
---

# Tauri coding standard

How the Rust core of a Tauri desktop app is written in this repository. Agents and skills
resolve this document as `<tauri-coding-standard>`.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Naming and layout

### Stack

- **Framework:** Tauri v2, 2.10 or later. `tauri` and its plugin crates are pinned with
  semver and kept current; `tauri dev` / `tauri build` manage Cargo feature flags.
- **WebView:** the operating system's. Do not bundle one.
- **Frontend:** a React app owned by the frontend agent. The Rust core may ship the thin
  typed TypeScript `invoke` wrapper that expresses the IPC contract, and nothing more.

### Layout

- `src-tauri/src/lib.rs` holds the `Builder` and `run()`, with the mobile entry point behind
  `#[cfg_attr(mobile, tauri::mobile_entry_point)]`.
- Commands live in a `commands/` module rather than in `lib.rs`, grouped by concern.
- Error and state types are explicit, named types — not ad-hoc tuples or strings.

## Coding standards

### Errors

- Fallible code returns `Result` and propagates with `?`. No `.unwrap()`, `.expect()`, or
  panic in a command path; a panic is reserved for an unrecoverable startup condition.
- One error enum per crate, derived with `thiserror`, serialized with the tagged-enum pattern
  so the frontend receives a typed `{ kind, message }`:

  ```rust
  #[derive(Debug, thiserror::Error)]
  enum Error {
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("sidecar failed: {0}")]
    Sidecar(String),
  }

  #[derive(serde::Serialize)]
  #[serde(tag = "kind", content = "message")]
  #[serde(rename_all = "camelCase")]
  enum ErrorKind { Io(String), Sidecar(String) }
  // impl serde::Serialize for Error mapping each variant to ErrorKind …
  ```

  `map_err(|e| e.to_string())` is for a throwaway prototype only.
- Log once, at the command boundary, not throughout the business logic.

### Commands and IPC

- Register every command in one `tauri::generate_handler![...]` call; calling
  `invoke_handler` a second time silently keeps only the last call.
- Commands defined in `lib.rs` are not `pub`; commands in a separate module are `pub`.
  Command names are global, so keep them unique across modules.
- Arguments and returns implement `serde::Deserialize` / `serde::Serialize`. Arguments
  arrive camelCase from JavaScript; use `#[tauri::command(rename_all = "snake_case")]` when
  the JavaScript side sends snake case.
- A fallible command returns `Result<T, E>` with `E: serde::Serialize`; that is how the
  error reaches the frontend.
- Large binary payloads return through `tauri::ipc::Response` rather than JSON.
- The IPC contract — command names, argument and return types, event names, channel payload
  shapes — is written down and versioned like an API.
- Commands are type-safe request/response. Events are fire-and-forget, untyped, always
  async and JSON-only; the frontend unlistens when scope ends. Stream progress over a
  `tauri::ipc::Channel`, not a flood of events.

### Async and state

- Prefer an async command for I/O or non-trivial work, so the UI never freezes; an async
  command runs on the Tauri async runtime, not the main thread.
- An async command cannot take a borrowed argument (`&str`, `State<'_, T>`). Use an owned
  type, or return a `Result`.
- Never block the async runtime. Offload blocking or CPU-bound work with
  `tauri::async_runtime::spawn_blocking` or `spawn`.
- Do not hold a `std::sync::Mutex` or `RwLock` guard across an `.await`. Drop it first, or
  use `tokio::sync::Mutex` for state that must live across await points; an async command
  using that mutex returns a `Result`.
- Register shared state with `.manage()` and read it through `tauri::State<T>`. Outside a
  command, reach state through the `Manager` trait (`app.state()`, `AppHandle`).

### Python sidecar

Where the app ships a Python component:

- It is built with PyInstaller and listed under `bundle.externalBin`, with the
  `-$TARGET_TRIPLE` rename automated in the build step rather than done by hand.
- It speaks line-delimited JSON over stdio.
- If it runs as a long-lived local API server instead, it binds to localhost only and the
  Rust core proxies to it; the frontend never calls it directly.

## Testing

_Nothing specific yet._

## Commands

_Nothing specific yet._

## Security and data handling

### Security configuration

- The CSP in `tauri.conf.json` is tightened to what the app loads.
- Sensitive apps use the Isolation Pattern for an extra IPC verification layer.

### IPC trust boundary

- Treat the WebView as untrusted. The Rust core has full system access; the frontend reaches
  it only through IPC. Validate every argument crossing the boundary.
- Default-deny exposure. Expose only the commands the frontend needs, and gate them with
  capabilities in `src-tauri/capabilities/`, granting the least permission and the
  narrowest scope.
- Never expose a generic run-arbitrary-command, shell, SQL or path surface. Wrap each
  privileged operation in a specific, validated command.
- A sidecar's shell capability is scoped to the exact binary name with argument validators;
  arbitrary arguments are not allowed unless genuinely required.
- Parse and validate a sidecar's output as an untrusted boundary, and surface its failures
  through the command's error type.
- Keep secrets, tokens and credentials in the Rust core, never in the WebView or in
  frontend-reachable code.
- Keep `tauri` and plugin crates on the pinned versions, and never widen the CSP to make
  something load.

## Done checklist

### Completion

`cargo clippy` runs with warnings denied in CI.

```bash
cargo fmt --check
cargo clippy -- -D warnings
cargo test '<name>'
npm run tauri build
```

`cargo fmt --check` is the format check and writes nothing. `cargo clippy -- -D warnings` compiles
the crate and denies a warning, so the build is green only when it is. The `cargo test` line is
filtered to the tests covering your change — the gate the review council and CI run is the
unfiltered crate's run, and a dev agent never runs the whole crate's tests. `npm run tauri build`
is the bundle, and it is the last command: a bundle is evidence that the Rust and the frontend
agree, not a substitute for the tests.
