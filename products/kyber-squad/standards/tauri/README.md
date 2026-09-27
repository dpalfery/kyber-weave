---
id: standards/tauri
title: "Tauri coding standard"
doc-type: coding-standard
status: draft
technology: tauri
owner: unassigned
last-reviewed: 2026-09-27
---

# Tauri coding standard

How the Rust core of a Tauri desktop app is written in this repository. Agents and skills
resolve this document as `<tauri-coding-standard>`.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

- **Framework:** Tauri v2, 2.10 or later. `tauri` and its plugin crates are pinned with
  semver and kept current; `tauri dev` / `tauri build` manage Cargo feature flags.
- **WebView:** the operating system's. Do not bundle one.
- **Frontend:** a React app owned by the frontend agent. The Rust core may ship the thin
  typed TypeScript `invoke` wrapper that expresses the IPC contract, and nothing more.

## Layout

- `src-tauri/src/lib.rs` holds the `Builder` and `run()`, with the mobile entry point behind
  `#[cfg_attr(mobile, tauri::mobile_entry_point)]`.
- Commands live in a `commands/` module rather than in `lib.rs`, grouped by concern.
- Error and state types are explicit, named types — not ad-hoc tuples or strings.

## Errors

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

## Commands and IPC

- Large binary payloads return through `tauri::ipc::Response` rather than JSON.
- The IPC contract — command names, argument and return types, event names, channel payload
  shapes — is written down and versioned like an API.

## Security configuration

- The CSP in `tauri.conf.json` is tightened to what the app loads.
- Sensitive apps use the Isolation Pattern for an extra IPC verification layer.

## Python sidecar

Where the app ships a Python component:

- It is built with PyInstaller and listed under `bundle.externalBin`, with the
  `-$TARGET_TRIPLE` rename automated in the build step rather than done by hand.
- It speaks line-delimited JSON over stdio.
- If it runs as a long-lived local API server instead, it binds to localhost only and the
  Rust core proxies to it; the frontend never calls it directly.

## Commands

`cargo clippy` runs with warnings denied in CI.

```bash
cargo fmt --check
cargo clippy -- -D warnings
cargo test
npm run tauri build
```
