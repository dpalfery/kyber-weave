---
id: todo/kyberdash-tray-supervisor-restart-loop
title: Tray does not restart a dead engine
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-10
---

# Tray does not restart a dead engine

On 2026-11-10 the KyberDash tray, running since 20:29:48 EST, left the engine dead from shortly after 20:30. The engine stayed down until a manual `kyberdash web --no-open` at 21:25 EST, about 55 minutes later. The tray did not bring it back.

## What the code does

`Supervisor::attempt` in `dash/tray/src-tauri/src/supervisor.rs` runs one start attempt. It sleeps the backoff owed by earlier failures, spawns the server, and waits up to `LISTENING_TIMEOUT` for its listening line. It returns `Attempt::Listening` or `Attempt::Failed { reason, retry_in }`. Its doc comment says the attempt is one step, so the caller owns the loop.

The only production caller is `ensure_server` in `dash/tray/src-tauri/src/runtime.rs`. It calls `attempt` only when the tray has no reusable server and `server.json` discovery finds none. A failed attempt is recorded as an error, and nothing in `Supervisor` schedules the next one. The retry therefore depends on `ensure_server` running again, and nothing in the tray guarantees that. The restart delays (1, 2, 4 … 60 seconds) are defined but nothing drives them. The packet and the plan called the type `ServerSupervisor`; the code's type is `Supervisor`.

The cause of the 55-minute gap is not diagnosed. This note records what was observed, not a fix.

## Why it is separate

This is a defect in the Rust tray layer, not in the KyberDash refresh. The refresh fix that prompted the investigation touched only the engine. Folding a tray change into it would have added a Tauri worker and its gates to that change.

## To decide

- Who owns the restart schedule: a dedicated loop in the tray, or a periodic `ensure_server` call that the runtime is guaranteed to make.
- What the popover shows while the engine is down and the tray is waiting to retry.
- A regression test that a dead engine is restarted within the documented backoff ceiling of 60 seconds.
