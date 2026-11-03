---
id: adr/0039-single-instance-engine
title: "One KyberDash Engine per User: OS-Enforced Single Instance, Ensure-or-Attach, Version Handoff"
doc-type: adr
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-26
---

# ADR 0039: One KyberDash Engine per User

## Status

Accepted, 2026-10-16. Replaces the "a second server over one state dir is a supported
configuration" position in `dash/src/cli/web.ts` and the port-probe adoption model of
`ReceiverHost` as the only guard against duplicate processes.

Amended 2026-10-26 (OpenCode capture deploy plan, D12): `kyber-weave update` is a new caller of the
ensure-or-attach contract. After it commits the binaries, it runs `kyberdash engine ensure --if-running`,
which calls `ensureEngine` and replaces a live engine of another version. It starts nothing when no
engine runs, and it runs even when the release is unchanged ("already on"). This clears a stale engine
with one command. No invariant changed: `ensureEngine` is still the only restart path, and the
receiver is still the engine's child.

## Context

`kyberdash web` is the engine: dashboard, scheduled jobs, and the hosted OTLP receiver. In
practice more than one process could end up serving a user:

- `kyberdash otel` ran a standalone receiver that competed with the receiver the engine
  hosts. `ReceiverHost` only probed `/healthz` and, finding any receiver answering,
  reported `reachable` and adopted it — including a receiver from an older binary.
- A second `kyberdash web` was allowed to start over the same state dir; only the jobs
  lease and the dashboard port stopped it from doing harm, and only by accident of
  configuration (`--port` bypassed the port guard).
- Processes outlived an upgrade. After a new binary was installed, the engine and receiver
  from the previous build kept running for days, so the user ran old parsing and old
  receiver code while every health check was green.
- Exclusivity was by convention: `server.json` and `jobs.lock` are files that a crash
  leaves behind and that need PID-liveness heuristics to interpret.

## Decision

1. **One engine per user, enforced by the operating system.** The engine binds a local IPC
   endpoint in the state directory — a Unix domain socket (`engine.sock`), or a named pipe on
   Windows. The kernel permits one binder and releases the endpoint when the process dies by
   any means. A leftover socket file that refuses connections is cleared and re-bound; a
   holder that accepts connections but does not answer is never bound over.
2. **The holder describes itself.** `engine.json` records pid, executable path, version,
   start time, and dashboard URL. It is a hint: a newcomer always asks the live holder over
   the endpoint (`info`), so the answer comes from a running process, not a file.
3. **Ensure-or-attach is the single entry for every caller.** `ensureEngine` returns the
   running engine when its version matches, replaces it when the version differs, and
   otherwise starts one from the caller's own executable (`web --no-open`) — the same
   `~/.local/bin/kyberdash` the tray launches. The tray's existing attach-or-launch contract
   (`server.json`, the `listening` line) is unchanged and still applies.
4. **`kyberdash web` refuses to be a second engine.** With a live engine of the same version
   it prints the attach record, opens the browser if asked, and exits 0. With an older
   version it asks that engine to shut down over the endpoint and takes over: the newer
   binary wins.
5. **`kyberdash otel` is an attach alias.** It turns `receiver.hosted` on and ensures the
   engine; it never binds a receiver. The only receiver process is the engine's child,
   started as the internal `otel --serve`, which exits when its parent does so a killed
   engine cannot leave an orphan holding the port. A custom `--port` or `--host` is refused.
6. **In-process suites opt out.** `singleInstance` is an option on `runWebDashboard`; the CLI
   turns it on, tests that start several servers over temporary state dirs do not.

## Consequences

- A second engine cannot start, whichever way it is invoked.
- An install replaces the running engine on the next `ensureEngine`/`web`; stale receivers
  stop being adopted because the receiver is always the current engine's child.
- Hosts that previously ran `kyberdash otel` as a long-lived foreground process must run
  `kyberdash web` (or let the tray) instead; `kyberdash otel` now returns once attached.
- The `jobs.lock` lease remains as defence in depth for jobs, but is no longer the
  mechanism that keeps a second server out.
- The tray (Rust) is not changed by this ADR. Moving its supervisor to the endpoint query,
  instead of `server.json` pid liveness, is follow-up work.

## Alternatives considered

- **Extend the PID lock file.** Rejected: it needs liveness and staleness heuristics, and it
  is exactly the mechanism that failed to stop the stale receiver.
- **Fail loudly instead of attaching.** Rejected for `web` and `otel`: the caller's intent
  (a running dashboard / a receiver) is already satisfied, so attaching is correct and exit 0.
