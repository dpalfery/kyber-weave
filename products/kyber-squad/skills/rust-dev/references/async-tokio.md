---
name: rust-dev/async-tokio
description: Async Rust on tokio — one runtime, spawning and cancellation, blocking work, locks across await, graceful shutdown.
---

# Async and Tokio

Whether the crate is async at all, and which runtime it uses, is decided by
**<rust-coding-standard>**. This reference assumes tokio.

## One runtime

- A binary builds exactly one runtime, in `main` (`#[tokio::main]` or an explicit
  `Builder`). Libraries never create one; they expose `async fn` and let the caller drive it.
- Enable only the tokio features the crate uses; `full` is for prototypes.

## Never block the runtime

- File I/O, CPU-heavy work, and blocking client libraries go through
  `tokio::task::spawn_blocking` or the async equivalent (`tokio::fs`, an async client).
- `std::thread::sleep` in async code stalls a worker thread. Use `tokio::time::sleep`.

## Locks across `.await`

- Do not hold a `std::sync::Mutex` guard across an `.await`. Scope the guard so it drops first.
- When state genuinely must be held across an await point, use `tokio::sync::Mutex` — and treat
  that as a design smell worth a comment.
- Prefer message passing (`tokio::sync::mpsc`, `watch`) to shared mutable state for long-lived
  tasks.

## Tasks own their lifetime

- Every `tokio::spawn` has an owner that either awaits its `JoinHandle` or holds it in a
  `JoinSet`. A detached task's panic and error are lost.
- Cancellation is cooperative: pass a `tokio_util::sync::CancellationToken` (or a `watch`
  receiver) and `select!` on it at each long await.
- `select!` drops the losing branches. Only race futures that are cancel-safe; the tokio docs
  mark which methods are.

## Graceful shutdown (daemons)

```rust
let token = CancellationToken::new();
let mut tasks = JoinSet::new();
tasks.spawn(worker(token.clone()));

tokio::select! {
    _ = tokio::signal::ctrl_c() => {}
    _ = terminate_signal() => {} // SIGTERM on Unix; a no-op future on Windows
}
token.cancel();
while let Some(res) = tasks.join_next().await {
    res??; // surface both panics and errors
}
```

Handle `SIGTERM` behind `#[cfg(unix)]`; on Windows, `tokio::signal::windows::ctrl_close`
covers the service-stop case.
