---
id: todo/menu-bar-fix
title: The tray popover cannot load because four of its six IPC commands do not exist
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-22
status: superseded
---

# The tray popover cannot load because four of its six IPC commands do not exist

> [!NOTE]
> **Status: Completed — owner-confirmed 2026-09-22**
> This todo was delivered by the
> [menu-bar runtime wiring plan](../archive/plans/2026-09-20-kyberdash-menu-bar-runtime-wiring.md)
> and its canonical-projection correction. The deployed launchd-owned tray renders a live
> report in the popover, all six IPC commands work from the popover surface, the recovery
> and event paths are reachable, and the status item shows the KyberDash lightsaber as a
> monochrome template image. On 2026-09-22 the owner confirmed the corrected behavior on the
> deployed build: the multi-harness selector inventory, direct Codex scoping, and measured
> Claude context with no generic fallback. The body below is retained as intake history.

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

At intake, running the tray locally (`tauri dev` from `dash/tray`) built, launched, put a status item
in the menu bar and opens the popover on click. The popover then shows `Reading the store…`
forever, and no data ever arrives.

It is not a configuration or environment fault. The popover's first act is
`invoke<ViewState>('get_view_state')`, and that command is not registered in the Rust app.
The invoke rejects, nothing catches the rejection, `state` stays `null`, and
[`App.tsx`](../../dash/tray/ui/src/App.tsx) renders its loading branch for the rest of the
process's life. The failure presents as a hang and leaves no trace in the terminal.

The same run also shows a blank filled square where the status item's glyph should be. That
is a second, independent defect with the same blast radius — the menu bar is unusable — so it
is recorded here rather than split off.

Both were found while verifying an unrelated CI fix (`b560d5f5`), by looking at the running
app rather than at the process table. A green build and a live PID had been taken as evidence
that the tray worked; neither is.

## Original findings at intake

- **At intake, the UI called six commands while the app registered two.** `get_view_state`,
  `refresh_now`, `open_view` and `set_settings` had no `#[tauri::command]` in the crate. The
  registration point in [`lib.rs`](../../dash/tray/src-tauri/src/lib.rs) contained only
  `generate_handler![quit, hide_popover]`.
- **The capability would refuse them anyway.**
  [`capabilities/tray.json`](../../dash/tray/src-tauri/capabilities/tray.json) grants
  `allow-quit` and `allow-hide-popover`; [`permissions/tray.toml`](../../dash/tray/src-tauri/permissions/tray.toml)
  defines only those two. Registering the commands without widening both leaves them denied.
- **The logic they would wrap already exists and is unit-tested.** `ipc.rs` has `view_state`,
  `view_path_patterns`, `matches_view_path` and `open_view_url`; `supervisor.rs` has
  `refresh_now`. What is missing is the command layer between them and the webview.
- **At intake, nothing produced data to serve.** `supervisor` and `scheduler` were declared
  as modules but never instantiated, and nothing was `manage()`d into Tauri state. The tray
  spawned no child process, never consulted `KYBERDASH_BIN`, and emitted no runtime output.
- **At intake, the `view-state-changed` event had no emitter.** `App.tsx` subscribed to it;
  no Rust code sent it. Even a working `get_view_state` would have given one snapshot and
  never updated.
- **At intake, the failure was silent by construction.** The mount `invoke` in
  [`App.tsx`](../../dash/tray/ui/src/App.tsx) had a `.then` and no `.catch`, so a rejected
  command was indistinguishable from a slow one. This is why the symptom read as a hang.
- **At intake, the status item used the app icon as a template image.**
  [`lib.rs`](../../dash/tray/src-tauri/src/lib.rs) passed `default_window_icon()` to
  `TrayIconBuilder` with `.icon_as_template(true)`. macOS template images must be black and
  transparent; a full-colour PNG flattened to a filled silhouette, which was the blank square
  on screen. There was no monochrome template asset in [`icons/`](../../dash/tray/src-tauri/icons).
- **Task 8.9 is marked complete.** [`tasks.md:393`](../specs/kyberdash-context-surfaces/tasks.md:393),
  "IPC surface and opening views", is `[x]`. Its acceptance is written entirely as tests of
  the helper functions — `get_view_state` carrying the design's `ViewState`, `open_view`
  refusing unmatched paths — every one of which passes against `ipc.rs` without a command
  ever being registered. The task is checked and the running app does not expose the surface.

## Verified implementation evidence

- The Rust runtime contract moved from intentional RED to GREEN: 113 tray unit tests and 10
  runtime contract tests pass.
- The React rejection/retry and event-update coverage moved from intentional RED to GREEN;
  the tray UI suite passes all 60 tests, with typecheck and lint passing.
- A real Tauri launch using the built CLI resolved the CLI's SQLite dependency, reached the
  real loopback server at `127.0.0.1:4747` with HTTP 200, and reaped its owned child cleanly.
  The Tauri hook paths were corrected during the same implementation.
- The broader Dash suite is 3,528/3,529 passing. Its one failure is the unrelated,
  pre-existing `dash/kyber` source-layout failure.

The remaining evidence is specifically visual and surface-level: CUA could not bind the
`LSUIElement`/status-item KyberDash host (`-10005`), so it did not produce actual popover
interaction or screenshots. On a host that can bind that status item, open the popover and
exercise `refresh_now`, `open_view`, `set_settings`, `hide_popover`, and `quit`; observe the
report/event update and visible CLI break/recovery; then capture the template glyph in light
and dark menu bars with Reduce Transparency and accent settings. Keep this todo in
`needs-review` until those observations are recorded.

Resolution 2026-09-22: those observations were made — by the owner exercising the deployed
launchd-owned tray directly rather than through CUA automation, across the plan's T13, T18,
and T27 live tests and the owner's final confirmation. See the completion note above and the
plan's closeout record.

## Original open questions

These questions were captured when the todo was written. The approved implementation plan
settled the runtime startup and failure-feedback choices; the broader task-checkmark audit
and live visual proof remain governed by the plan and are not claimed closed here.

- **Whether the gap is the tasks or the task template.** 8.9 is not the only task whose
  acceptance is phrased as unit tests over helpers. If the wiring is what gets skipped, then
  8.8 (settings), 8.7 (status item) and the rest of stream E deserve the same audit before
  any of them is trusted as done. Auditing them is cheap; deciding to trust the checkmarks is
  the expensive option.
- **Whether a task may be called done without the surface being reachable.** A rule that
  every `[x]` on a UI-facing task requires the app to demonstrate the behaviour would have
  caught this. It also makes tasks slower to close, and stream E was explicitly parallelised.
- **What the popover shows when a command fails.** Adding `.catch` is not in itself a
  decision; what it renders is. `ViewState` already carries `error: string | null` and a
  `phase` including `setup`, so an IPC failure could present as an error state, as the
  existing setup state, or as something new. An IPC failure is a defect, not a user
  condition, which argues against dressing it as either.
- **Whether the supervisor starts eagerly or on first popover open.** Eager start means the
  first click has data; it also spawns the CLI on login for a tray the user may never open.
  Requirement 15.3's posture — do not install a surface the user did not choose — suggests
  the same restraint applies to running one.
- **Where the template icon comes from.** Deriving a monochrome glyph from the existing
  `icon.png` at build time keeps one source of truth but constrains the artwork; a separate
  hand-drawn template asset is better looking and is a second thing to keep in sync.

## The code seam

- [`dash/tray/src-tauri/src/lib.rs`](../../dash/tray/src-tauri/src/lib.rs) — the
  `generate_handler!` list, the tray icon, and `setup()`, where the runtime is constructed and
  managed.
- [`dash/tray/src-tauri/src/ipc.rs`](../../dash/tray/src-tauri/src/ipc.rs) — `view_state`,
  `open_view_url`, `matches_view_path`; the functions the missing commands would wrap.
- [`dash/tray/src-tauri/src/supervisor.rs`](../../dash/tray/src-tauri/src/supervisor.rs),
  [`scheduler.rs`](../../dash/tray/src-tauri/src/scheduler.rs) — the data path now composed by
  the runtime.
- [`dash/tray/src-tauri/capabilities/tray.json`](../../dash/tray/src-tauri/capabilities/tray.json),
  [`permissions/tray.toml`](../../dash/tray/src-tauri/permissions/tray.toml) — the six-command
  authorization boundary.
- [`dash/tray/ui/src/App.tsx`](../../dash/tray/ui/src/App.tsx) — initial-state/error handling,
  event subscription, and action rejection feedback.
- [`dash/tray/src-tauri/icons/`](../../dash/tray/src-tauri/icons) — where a template asset
  would live.
- [`docs/specs/kyberdash-context-surfaces/tasks.md`](../specs/kyberdash-context-surfaces/tasks.md) —
  task 8.9, and whatever the audit above concludes about its neighbours.

## How to verify

- With the tray running, the popover leaves the loading state and renders a report. This is
  the whole point and no unit test substitutes for it.
- Each of `refresh_now`, `open_view`, `set_settings`, `quit` and `hide_popover` does its thing
  when driven from the popover, not only when its helper is called from a Rust test.
- Killing or breaking the CLI while the tray runs produces a visible error or setup state in
  the popover, never an indefinite loading string. The `.catch` is what this asserts.
- A refresh, or the scheduler's own tick, updates an already-open popover — proving
  `view-state-changed` is emitted and not merely subscribed to.
- The status item renders as a legible glyph in both light and dark menu bars, and under
  "Reduce transparency" and the macOS accent settings.
- `KYBERDASH_BIN` set to a built `dash/dist/cli.js` is honoured: the tray spawns it, and the
  real loopback server responds with HTTP 200. The owned child is reaped cleanly on shutdown.
