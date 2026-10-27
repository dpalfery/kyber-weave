---
id: standards/tauri
title: Tauri coding standard
doc-type: coding-standard
status: current
technology: tauri
owner: dpalfery
last-reviewed: 2026-10-27
---

# Tauri coding standard

How the Tauri layer of the KyberDash tray is written: the Tauri builder, its IPC commands, its
capability manifest, its window and content-security configuration, and the way it starts the
KyberDash engine. Agents and skills resolve this document as `<tauri-coding-standard>` in the
repository root [`AGENTS.md`](../../../AGENTS.md).

This standard covers only the Tauri layer, in [`dash/tray/src-tauri/`](../../../dash/tray/src-tauri/tauri.conf.json).
It defers every language rule to the [Rust coding standard](../rust/README.md), which applies
to all Rust in the crate. The TypeScript that runs in the webview is governed by the
[TypeScript coding standard](../typescript/README.md). The shared application and display-layer
boundaries are defined by [KyberDash architecture](../../dash/architecture.md) and the
[display-layer rule](../../rules/kyberdash-display-layer.md).

## Authority and scope

The tray is a display layer. It renders the state the shared KyberDash engine reports and asks
the engine to act; it does not schedule, import, clean, or compute domain results. The rule is
stated in [`dash/tray/AGENTS.md`](../../../dash/tray/AGENTS.md), and the Tauri layer must keep
to it. A Tauri command that starts a job, or a window that polls on its own cadence to decide
when something is due, is outside this standard's allowance.

## Stack

- **Framework:** Tauri 2, with `tauri` 2.11 and `tauri-build` 2 as declared in
  [`Cargo.toml`](../../../dash/tray/src-tauri/Cargo.toml). The `tray-icon`, `image-png`, and
  `macos-private-api` features are enabled on `tauri`.
- **Plugins:** `tauri-plugin-opener` opens the loopback view URL in the browser, and
  `tauri-plugin-single-instance` hands a second launch's arguments to the running tray
  (`args.rs`) instead of starting another.
- **WebView:** the operating system's webview. The app does not bundle one.
- **Frontend:** a React and TypeScript UI in [`dash/tray/ui/`](../../../dash/tray/ui/src).
  Its `invoke` calls live in the UI sources, such as `App.tsx` and `hidePopover.ts`.

## Layout

- `src/lib.rs` holds the `Builder` and `run()`. `src/main.rs` is a one-line call to
  `kyberdash_tray_lib::run()`.
- Commands are defined in `lib.rs` and registered in one `tauri::generate_handler!` list.
  There is no `commands/` module. The tray has nine commands: `get_view_state`, `refresh_now`,
  `open_view`, `set_settings`, `quit`, `hide_popover`, `clean_database`,
  `import_folder_history`, and `set_shared_settings`.
- There is no mobile entry point. The tray targets desktop only, and its bundle targets are
  `app`, `dmg`, and `nsis`.
- The tray ships no Python or other sidecar. `tauri.conf.json` declares no `bundle.externalBin`.

## The engine is a child process, not a sidecar

The tray starts the shared KyberDash engine as a child process, `kyberdash web --no-open`
(`SERVER_ARGS` in `supervisor.rs`). `Supervisor` reads the engine's listening line, records its
URL, restarts it with bounded backoff, and kills only processes it launched. A server the tray
attaches to, recorded in `~/.kyberdash/server.json`, is never stopped by the tray. The
supervision rules are in the Rust standard's process section.

## Network and the webview boundary

The webview has no network access, and the tray is built that way on purpose.

- The content-security policy in `tauri.conf.json` is `default-src 'self'; connect-src ipc: http://ipc.localhost`.
  The webview can reach only its own assets and Tauri IPC.
- Rust does all HTTP to the loopback engine, through `LoopbackFetcher` in `api.rs`. Every
  origin is checked against the loopback address the engine reported.
- Opening a view is also a Rust decision. `ipc::open_view_url` refuses any URL that is not
  loopback, and the opener plugin receives only that approved URL.

A change that lets the webview fetch from another origin, or widens `connect-src`, changes
this boundary and needs its own review.

## IPC contract

- Each command is a `#[tauri::command]` with a `/// IPC contract:` doc comment stating its
  name, its arguments, and its return type, for example `get_view_state() -> ViewState`. The
  comment is the contract; keep it in step with the signature.
- A command that can fail returns `std::result::Result<T, CommandError>`. `CommandError` is a `thiserror` enum
  serialized as `{ kind, message }`, so the frontend receives a typed error. Do not return a
  bare string for a failure the UI must branch on.
- The payload the webview receives is a full JSON snapshot of `ViewState`, emitted by
  `TauriEventSink::emit` under the event name it is given. The native status item reads the same
  snapshot, so the webview and the status item cannot disagree about one state.

## Capabilities and permissions

- The webview's authority is one capability file, [`capabilities/tray.json`](../../../dash/tray/src-tauri/capabilities/tray.json).
  It applies only to the `popover` window and grants the tray's own commands plus event
  listening.
- Each application command has a per-command permission in
  [`permissions/tray.toml`](../../../dash/tray/src-tauri/permissions/tray.toml), declared as
  `commands.allow = [...]`. A command reachable from the webview needs both a permission entry
  and a line in the capability file.
- The webview may listen for events (`core:event:allow-listen`, `core:event:allow-unlisten`)
  but may not emit them. `core:event:default`, `core:event:allow-emit`, and
  `core:event:allow-emit-to` are not granted.
- [`tests/capability_contract.rs`](../../../dash/tray/src-tauri/tests/capability_contract.rs)
  pins this manifest. It asserts that seven of the nine custom command permissions are present (the other two,
  `allow-import-folder-history` and `allow-set-shared-settings`, are granted in the capability
  file and declared in `tray.toml` but not yet asserted by the test; they are webview-reachable,
  so a change to either is a change to the security boundary), that
  event listening is granted, and that the three forbidden event permissions are absent. A
  change to the capability file that breaks the test is a change to the security boundary.

## Window and configuration

- `withGlobalTauri` is `false`. The UI imports Tauri's API explicitly.
- The `popover` window is declared in `tauri.conf.json` and starts hidden, undecorated,
  always on top, outside the taskbar, not resizable, and not focused. Losing focus hides it
  (`lib.rs`, the `Focused(false)` handler on the popover window, which calls
  `close_popover_when_unfocused`).
- `app.macOSPrivateApi` is `true`, matching the `macos-private-api` feature on `tauri`.

## Gates

A change to the Tauri layer passes the Rust gates of the Rust standard, and the tray UI gates
that the root `AGENTS.md` lists. Run them from the repository root:

```bash
cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check
cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings
cargo test --manifest-path dash/tray/src-tauri/Cargo.toml
npm --prefix dash/tray/ui run typecheck
npm --prefix dash/tray/ui run test
```

The same three Rust commands run from `dash/tray/src-tauri`, as the Rust standard gives them.
The bundled application is built by the release automation owned by `github-devops`, not by a
command in this standard.
