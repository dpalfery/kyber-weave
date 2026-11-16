---
id: standards/tauri/rules
title: "tauri rules"
doc-type: rule
status: current
technology: tauri
owner: dpalfery
last-reviewed: 2026-11-16
---

# tauri rules

## Display layer

- The tray is a display layer over one engine and one `/api/kyber/*` API. It holds no feature
  logic, scheduler or job of its own.
- The Rust core validates every value the WebView sends across IPC; the WebView is untrusted.
- Capabilities are default-deny and scoped to the exact command or sidecar the UI needs.
- Secrets stay in the Rust core and never reach frontend-reachable code.

## Boundaries

- A change in `src-tauri` also follows the `rust` standard for language rules.
- The tray's gates are in `dash/tray/AGENTS.md`.
