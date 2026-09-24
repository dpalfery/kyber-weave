---
id: adr/0023-kyberdash-report-model-and-tray-ownership
title: One KyberDash Report Model, Rust-Side HTTP, and the Tray's Ownership of Server, Refresh and Receiver
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-24
---

# ADR 0023: One KyberDash Report Model, Rust-Side HTTP, and the Tray's Ownership of Server, Refresh and Receiver

## Status

Accepted, 2026-09-24, recording decisions the KyberDash context-surfaces specification made
and delivered. The specification reserved this record as ADR 0021; that number went to the
ZCode record first, so it is 0023.

This record supersedes one sentence of [ADR 0016](0016-kyberdash-harness-source-refresh.md)
decision 1: "A dashboard refresh button is not part of this lifecycle." Everything else in
ADR 0016 stands, and ADR 0016 is not edited — the same shape as
[ADR 0008](0008-kyberdash-single-canonical-store.md) superseding one decision of ADR 0007.

## Context

KyberDash now has three surfaces: the `kyberdash report` CLI, the web dashboard, and a Tauri
tray for macOS and Windows. Three surfaces reading the same store invite three derivations
of the same figures, and a tray that starts processes and refreshes on a timer has to
coexist with a terminal that runs `dash refresh` by hand.

## Decision

1. **One report model.** Every surface renders one versioned `ContextReport` built by
   `buildContextReport` (`dash/src/analysis/report/build.ts`). The CLI prints it, the REST
   API serves it at `GET /api/kyber/report`, and the tray renders the same document. The
   document carries its own harness inventory (`coverage.harnesses`), so selecting a harness
   scopes the same report rather than fetching a second one. CLI and REST equality is pinned
   by `dash/src/cli/report-api-parity.test.ts`.
2. **The tray's Rust core does all HTTP.** The popover webview holds no network permission.
   Rust fetches the loopback report with `reqwest` and hands JSON to the UI over IPC, so the
   web server's cross-origin rule stays as it is and the loopback-only rule lives in one
   place.
3. **The tray owns the server, the scheduled refresh and the receiver.** It supervises one
   `kyberdash web --no-open` child (`dash/tray/src-tauri/src/supervisor.rs`), runs
   `kyberdash dash refresh` on its cadence and on **Refresh now**
   (`dash/tray/src-tauri/src/scheduler.rs`), and — when settings allow — the embedded OTLP
   receiver. A refresh button is therefore part of the refresh lifecycle.
4. **The refresh lock, and exit code 3.** `dash refresh` takes a PID-liveness lock at
   `~/.kyberdash/refresh.lock` (`dash/src/refresh/lock.ts`) before opening the store. A
   refresh that finds it held exits **3** (`REFRESH_BUSY_EXIT_CODE`,
   `dash/src/cli/register.ts`), so the tray and a terminal can tell "already running" from
   "failed" without parsing a message. Exit codes 0, 1 and 2 keep their ADR 0016 meanings.

## Alternatives Considered

- **Tray-specific endpoints such as `/glance`.** A second derivation of the same figures,
  needing a parity test forever.
- **Adding the Tauri origins to the server's allowlist.** Widens the origin rule the
  dashboard relies on for CSRF protection.
- **A lock row inside SQLite.** A process that crashed cannot release it.

## Consequences

- A figure cannot differ between surfaces without failing CI.
- The tray starts and restarts the processes it reads from; it does not share a server with
  a dashboard the user started by hand.
- Scripts that run `dash refresh` must treat exit 3 as "busy, retry later", not as failure.

## Related

- [ADR 0016](0016-kyberdash-harness-source-refresh.md) — harness-source refresh; one sentence
  superseded here
- [ADR 0020](0020-kyberdash-one-time-fork.md) — the one-time fork these surfaces were built on
- [KyberDash architecture](../dash/architecture.md) — surface layer and tray
