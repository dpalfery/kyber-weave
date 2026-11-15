# Working under `dash/tray/`

The tray is a **display layer** ([`dash/AGENTS.md`](../AGENTS.md)).

## THE RULE

> **THE TRAY AND THE WEB DASHBOARD ARE DISPLAY LAYERS. NEITHER CONTAINS ISOLATED FEATURE
> LOGIC, SCHEDULERS OR JOBS. ALL LOGIC AND JOBS (REFRESH SCHEDULING, CLEAN, IMPORT, PAUSE)
> LIVE IN THE SHARED KYBERDASH C# APPLICATION (`dash/server/`, INCLUDING THE ENGINE, THE
> `kyberdash` CLI, AND THE `kyberdash web` SERVER) THAT BOTH SURFACES SHARE. BOTH SURFACES USE
> ONE DATASTORE (`canon.db`, resolved by `KyberDash.Domain.CanonDbPath.Resolve`) AND ONE API
> LAYER (`/api/kyber/*`). NO AGENT OR DESIGN MAY VIOLATE THIS.**

Rule text and checklist: [`docs/rules/kyberdash-display-layer.md`](../../../docs/rules/kyberdash-display-layer.md).
Decision: [ADR 0033](../../../docs/adr/0033-kyberdash-surfaces-are-display-layers.md) and
[ADR 0084](../../../docs/adr/0084-kyberdash-csharp-engine.md).
Architecture: [`docs/dash/architecture.md`](../../../docs/dash/architecture.md).

## Allowed here

- Rendering `ViewState`, the report, and what `/api/kyber/*` serves, plus the popover's own
  display preferences (selected harness, window of days, geometry).
- Loopback HTTP from Rust to the shared server, and invoking actions over it — a clean, an
  import, a pause, a shared setting. Asking the engine to act is not acting.
- Launching or attaching to the `kyberdash web` server named in `~/.kyberdash/server.json`,
  and killing only a server this process launched.
- Platform integration: the status item, autostart, window placement, IPC plumbing.

## Forbidden here

- A scheduler, a cadence or "is it due" check, or any timer that stands in for one. Polling
  to repaint the popover is display work; scheduling a job is not.
- Spawning CLI children to do a job (`refresh`, `clean`, `import-history`, `settings`,
  `otel`) — that is `JobSchedulerService` in
  `dash/server/KyberDash.Application/Services/JobSchedulerService.cs` and `OtlpCollectorService`
  in `dash/server/KyberDash.Application/Services/OtlpCollectorService.cs`, hosted by the
  `kyberdash web` server. `supervisor.rs` launching the server is the one legitimate process launch.
- Hosting the OTLP receiver, or a tray-local copy of any setting that belongs in
  `canon.db` metadata (`SettingsService` / `CanonStoreRepository`).
- Computing a domain result in the popover UI (`dash/tray/ui/src/**`).

## Gates

Run each command from the repository root, directly, with no process wrapper
([`dash/AGENTS.md`](../AGENTS.md#tests-that-start-processes)):

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint          # already covers dash/tray/ui
npm --prefix dash run test:related -- <files relative to dash/>
npm --prefix dash run check:reachable
```

The popover UI has its own gates after `npm --prefix dash/tray/ui ci`:

```bash
npm --prefix dash/tray/ui run typecheck
npm --prefix dash/tray/ui run test -- <path relative to dash/tray/ui>
cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check
cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings
cargo test --manifest-path dash/tray/src-tauri/Cargo.toml '<name>'
```

`npm --prefix dash run lint` does not cover the Rust crate — `cargo fmt`, `cargo clippy` and
a filtered `cargo test` are yours to run for any change under `src-tauri/`. Check the result line
shows a non-zero `passed` count.

These are targeted runs. The review council and CI run the whole suites and the whole crate; no
dev agent does.
