---
id: archive/plans/2026-09-20-kyberdash-menu-bar-runtime-wiring
title: Restore KyberDash menu bar runtime wiring
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-25
development-mode: test-first
---

# Restore KyberDash menu bar runtime wiring

**Status:** Archived  
**Archive Date:** 2026-09-22  
**Completion:** Complete — executed, review-approved, and owner-confirmed on 2026-09-22; see [Closeout (T28, 2026-09-22)](#closeout-t28-2026-09-22). The owner approved the canonical-projection architecture and execution on 2026-09-21; T20–T28 superseded the earlier proposed selector-only T20–T27 pass.  
**Date:** 2026-09-21  
**Development mode:** test-first  
**Goal:** Make the tray popover reach a live report, execute its commands, recover visibly from failures, show a legible macOS status glyph, and ensure static refresh plus live OTLP feed one authoritative canonical projection that every report surface reads.

## Problem and scope

At intake, the tray opened a popover that remained on `Reading the store…`. The missing Rust
command, runtime orchestration, event emission, and template icon are documented in [the
originating todo](../todo/menu-bar-fix.md). This plan records how the implementation is
reconciled with the existing context-surfaces requirements and design.

## Approved decisions and provenance

The conductor relayed the owner's explicit **approve and execute** decision on 2026-09-20 for this plan and its test-first Test contract as saved in Draft. No additional product decision was required. The existing [context-surfaces requirements](../specs/kyberdash-context-surfaces/requirements.md), [design](../specs/kyberdash-context-surfaces/design.md) C9–C10, and [task list](../specs/kyberdash-context-surfaces/tasks.md) stream E supply the product contract. In particular, Requirements 7.1 and 10.1 already require a tray-owned server and a refresh at start. Requirements 6.7, 7.4, and 8.11 distinguish missing CLI, stale data, and empty data. The todo's startup question is therefore settled by the existing contract. A visible IPC failure and dedicated monochrome template glyph are reversible implementation details within the approved acceptance criteria. The stream E audit is an evidence task: checkmarks are preserved only where reachable behavior is demonstrated.

The conductor relayed a second explicit **approve and execute** decision on 2026-09-21 after the owner tested the locally deployed bundle. That approval covers the corrective Test contract and tasks T8–T14 below. The owner's direct report settles the remaining product choice: the menu-bar glyph is the existing KyberDash lightsaber, projected as a monochrome macOS template image, rather than the temporary letter `K`. No decision remains open.

The conductor relayed the owner's third explicit decision on 2026-09-21 after the owner
reported that background OTEL collection was not durable. The approved lifecycle is:
launch at login, restart after an abnormal tray exit, and stop collection after an intentional
**Quit**. The tray remains the receiver owner; no independent always-on collector is added.
The legacy `KyberDashMenubar` login item and bundle are preserved. This decision is compatible
with Requirements 6.8, 6.9, 10.7, and 10.8 and does not reopen the feature specification.

The conductor relayed the owner's fourth explicit architecture decision on 2026-09-21:
preserve the OTLP receiver and static harness-history refresh as separate source adapters, but
make one shared canonical projection pipeline authoritative for web, tray, and CLI reporting.
The selector inventory is dynamic canonical data for the active day window, not a fixed list of
eight harnesses and not a second tray-side derivation. This selects the model already required
by ADRs 0008 and 0009 and by context-surfaces Decisions D2 and D3; it does not authorize a
second database, a tray database reader, or a second report derivation.

## Investigation findings

The repository's `docs_explore` MCP operation was not exposed in this session; discovery started at the path declared as **<documentation-index>** in root `AGENTS.md` and followed its links. `.codegraph/` exists; the CodeGraph CLI was used before direct source lookup. The working tree was clean at intake.

- `dash/tray/src-tauri/src/lib.rs` registers only `quit` and `hide_popover`. Its `setup()` creates an icon and window listener, but no runtime service or managed Tauri state. The full-colour default app icon is passed to `.icon_as_template(true)`, producing the observed filled square on macOS.
- `dash/tray/ui/src/App.tsx` calls `get_view_state` on mount without a rejection path. `null` state therefore retains `Reading the store…` indefinitely. It invokes `refresh_now`, `open_view`, and `set_settings`; `lib.rs` registers none of them. The UI listens for `view-state-changed`, but no Rust code emits it.
- `dash/tray/src-tauri/capabilities/tray.json` and `permissions/tray.toml` allow only the two existing commands. Handler registration alone cannot make the four missing calls reachable.
- `ipc.rs` already assembles `ViewState` and validates view paths; `supervisor.rs`, `scheduler.rs`, `api.rs`, `receiver.rs`, `settings.rs`, `status_item.rs`, and `cli.rs` contain separately tested policy and helper logic. Production adapters, scheduling, state ownership, and event delivery are absent. The existing Rust tests validate those helpers, not a running app.
- `dash/tray/ui/src/App.test.tsx` only renders the initial shell statically, so React effects and rejected invokes have no coverage. `Popover.test.tsx` validates report phases and fixtures but cannot prove an initial state arrives. No dedicated template icon exists under `dash/tray/src-tauri/icons/`.
- `dash/dist/cli.js` currently has a Node shebang and executable bit, which makes it a useful local `KYBERDASH_BIN` smoke target. The runtime must use `cli.rs` resolution and preserve its validated-path and argument rules; a local JavaScript file is not a substitute for verifying the installed binary path.
- The design calls for an opener plugin for validated dashboard paths, but the tray crate currently declares no opener dependency. Any added dependency must be justified by this contract and remain restricted to validated loopback URLs. The existing `open_view_url` helper's loopback validation must remain in force.
- The task list marks stream E's 8.1–8.10 subtasks `[x]` while parent task 8 remains open. Their helper tests do not prove status, login setting, receiver, report, scheduler, or IPC reachability. The audit must assess all stream E claims against the running surface.

## Test contract (test-first)

The following rows are the approved Test contract. `test-dev` records the exact RED command, failing assertion or compilation diagnostic for the intended absent behavior, and revision before any corresponding implementation. A missing test dependency or unrelated build break is not valid RED evidence. GREEN reruns the same assertion without weakening it. Source and test edits in one row are serialized when their file scopes overlap.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 → T4 Rust runtime and commands | `dash/tray/src-tauri/tests/runtime_contract.rs` (new; production seam may be exposed through `runtime.rs`) | `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test runtime_contract` | A fake CLI/process, clock, report fetcher and event sink show one resolved CLI, one supervised server, startup refresh, cadence and explicit refresh serialization, state snapshots and change events, stale/setup recovery, validated `open_view`, persisted settings, receiver opt-in, status updates, and clean child shutdown. A missing or incompatible CLI never starts a server. | New behavioral tests fail specifically because runtime composition or command behavior is absent. Record test names, output, and revision. | Same tests pass; assert state and effects rather than just module/function names. |
| T1 → T4 authorization boundary | `dash/tray/src-tauri/tests/runtime_contract.rs` plus live Tauri smoke in T6 | Same Rust command, then T6 smoke | Only the six design commands (`get_view_state`, `refresh_now`, `open_view`, `set_settings`, `quit`, and the existing `hide_popover`) are callable from the popover; arbitrary commands are denied. A route outside the embedded table never reaches the opener. | Contract test fails for missing registration or permission mapping; live pre-fix popover rejection is recorded if available. | Contract and live smoke agree; capability and permission files grant exactly these commands. |
| T2 → T5 UI rejection and recovery | `dash/tray/ui/src/App.test.tsx` (effectful DOM tests; test-only DOM dependencies if needed) | `npm --prefix dash/tray/ui run test -- App.test.tsx` | Rejected initial `get_view_state` becomes a visible IPC error with retry, a successful retry renders `Popover`, rejected action commands are visible, and `view-state-changed` updates an open popover. Listener cleanup prevents updates after unmount. | New effectful tests fail on the current perpetual loading branch or swallowed action rejection; record names/output/revision. | The same tests pass without turning IPC rejection into a missing-CLI setup message or showing stale data as current. |
| T3 icon asset | `dash/tray/src-tauri/icons/tray-template.png` (new) | No automated test; T6 macOS visual smoke | A transparent monochrome glyph remains recognizable against light and dark menu bars and macOS appearance/accessibility settings. | No RED test: visual legibility is the requirement and a pixel-shape unit test would only mirror the asset. Save a pre-fix observation of the filled square. | T6 records actual screenshots/observations for light, dark, Reduce transparency, and accent settings. |
| T6 live integration | Running `tauri dev` with built `dash/dist/cli.js` | T6 procedure below | Commands, server child, refresh, event update, error recovery, and icon work through a real popover. | The todo records the pre-fix hang and blank glyph; save fresh baseline where practical. | Every live acceptance item below is observed; do not substitute a green process/build for UI evidence. |
| T7 documentation closeout | Governed docs corpus | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `... docs drift .` | Stream E claims and canonical tray docs match proven behavior; originating todo and plan are closed or remain active with explicit gaps. | No RED test: documentation task follows the implementation evidence. | Both documentation checks report zero findings after each documentation edit. |

## Dispatchable tasks

### T1 — RED Rust runtime/IPC contract

- **Objective:** Write the `runtime_contract.rs` tests above against an externally observable, dependency-injected runtime seam. Cover at least the six design commands, startup and scheduled refresh, event delivery, setup/stale handling, security boundaries, settings persistence, status state, receiver opt-in, and child cleanup. Use fake processes and loopback fetchers; do not access the user's real store or login items.
- **Files/symbols owned:** New `dash/tray/src-tauri/tests/runtime_contract.rs` only. If a compile-only public seam is needed, report that contract to T4; production Rust remains T4-owned.
- **Acceptance:** Intentional RED evidence for absent runtime behavior, with assertions retained for T4 GREEN.
- **Depends on:** None. **Required skills:** `test-dev`.

### T2 — RED React effect and rejection contract

- **Objective:** Add effectful `App` tests for mount, rejection, retry, action failure, event update, and cleanup. Use a real DOM test environment if required; explain and pin any test-only dependencies in `package.json`/lockfile. Static server rendering alone cannot exercise the bug.
- **Files/symbols owned:** `dash/tray/ui/src/App.test.tsx`, and only test-runner dependency entries in `dash/tray/ui/package.json` and its lockfile if needed.
- **Acceptance:** Recorded RED failure on the current behavior, not a missing test runner. Tests stay intact for T5.
- **Depends on:** None. **Required skills:** `test-dev`.

### T3 — Mac status glyph asset

- **Objective:** Add a dedicated black-and-transparent menu bar template glyph for macOS, with sufficient padding and no filled rectangular background. Keep the coloured bundle icon for app packaging. This is an asset-only scope; T4 connects it to the tray.
- **Files/symbols owned:** New `dash/tray/src-tauri/icons/tray-template.png` (and source artwork only if needed under that icons directory).
- **Acceptance:** Image has transparent background and a recognizable silhouette; T6 supplies appearance evidence.
- **Depends on:** None. **Required skills:** `tauri-dev`.

### T4 — GREEN Rust runtime composition and authorized commands

- **Objective:** Turn the existing helper modules into one managed runtime. Resolve the CLI with `KyberdashCli::resolve_detailed`; spawn exactly one supervised `web --no-open`, run initial and due `dash refresh` without blocking the UI thread, poll the reported loopback URL at the specified 15/60 second cadence, probe/host receiver only as settings permit, and publish full `ViewState` snapshots through `get_view_state` and `view-state-changed`. Implement `refresh_now`, `open_view`, and `set_settings` commands, persist partial settings, apply launch-at-login, update the status item's title/tooltip/icon from `status_item`, and stop owned child processes on Quit/exit. Preserve the existing security bounds on CLI paths, loopback transport, report size/time, and route matching. Use the design's opener mechanism for validated URLs and justify any new crate in the implementation note. Expose testable adapters for subprocess, clock, fetcher, event sink, storage, and opener effects.
- **Files/symbols owned:** `dash/tray/src-tauri/src/lib.rs`, new `runtime.rs` and any production adapter module under `src/`, existing `cli.rs`, `supervisor.rs`, `scheduler.rs`, `api.rs`, `receiver.rs`, `settings.rs`, `status_item.rs`, `ipc.rs` only where integration demands, `dash/tray/src-tauri/Cargo.toml`/`Cargo.lock`, `capabilities/tray.json`, `permissions/tray.toml`, and tray icon registration. Do not edit T1's test file or T3's asset.
- **Acceptance:** T1's same Rust tests GREEN; all six commands registered and only those allowed; first state is available without waiting for a successful fetch; missing CLI yields setup, a failed fetch retains the last report as stale, events follow state changes, and app exit reaps owned processes.
- **Depends on:** T1 and T3. **Required skills:** `tauri-dev`.

### T5 — GREEN popover failure feedback

- **Objective:** Handle initial and subsequent IPC rejection with a visible error and retry action; make `get_view_state` retry clear the error only after a successful response. Handle rejected `refresh_now`, `open_view`, and `set_settings` promises so controls do not silently fail. Continue to render the existing setup/starting/ready/stale `ViewState` phases and event-fed updates without a new network path in the webview.
- **Files/symbols owned:** `dash/tray/ui/src/App.tsx`, `dash/tray/ui/src/Popover.tsx` if needed for an action error, new UI error component under `dash/tray/ui/src/components/`, and `dash/tray/ui/src/styles.css`. Do not edit T2's test or Rust files.
- **Acceptance:** T2's same React tests GREEN; an IPC failure cannot leave the initial loading string indefinitely. Existing `Popover.test.tsx` and command tests stay GREEN.
- **Depends on:** T2. **Required skills:** `react-dev`.

### T6 — Integrated and live verification

- **Objective:** Run the tray gates below, then build the CLI and launch `tauri dev` on macOS with `KYBERDASH_BIN` pointing to the built executable script. Exercise a real popover and record observed results for every item in the originating todo's How to verify section. Check `pgrep -P <tray pid>` (or an equivalent child-process observation), report and scheduler event updates, `refresh_now`, `open_view`, `set_settings`, `quit`, `hide_popover`, unavailable/killed CLI, and light/dark/accessibility icon views. A green build or PID is not live acceptance.
- **Files/symbols owned:** No source files; verification evidence in the task report. If the installed CLI path behaves differently from the script, report both. Do not mutate login settings or user data outside an isolated test configuration.
- **Acceptance:** All live claims observed. A failing item returns a precise rework scope to the conductor; do not mark T6 complete on a partial smoke.
- **Depends on:** T4 and T5. **Required skills:** `test-dev`, `tauri-dev`.

### T7 — Review and documentation closeout

- **Objective:** After code review and T6 evidence, audit tasks 8.1–8.10 against reachable behavior, not helper-only tests. Correct any inaccurate checkmarks and acceptance wording. Update the canonical KyberDash architecture/runbook for the shipped tray contract, reconcile or archive `docs/todo/menu-bar-fix.md` and its todo index, then close the plan and index only when the evidence supports it. If an item is unproved, leave its status active and state the gap.
- **Files/symbols owned:** `docs/specs/kyberdash-context-surfaces/tasks.md`, `docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/todo/menu-bar-fix.md`, `docs/todo/README.md`, this plan, the path declared as **<plan-index>**, and archive paths only if closeout is earned. Documentation beyond these paths needs a reported reason before editing.
- **Acceptance:** Source-specific claims match T6 evidence, review verdict is acceptable, and both documentation checks report zero findings. Any remaining todo is explicit and indexed.
- **Depends on:** T6 and whole-run review. **Required skills:** `docs-dev`, `app-docs-standard`, `kyber-weave-docs`.

## Dependency graph and concurrency audit

```text
T1 (Rust RED) ───┐
                 ├──> T4 (Rust GREEN) ──┐
T3 (icon asset) ─┘                      ├──> T6 (live) ──> whole-run review ──> T7 (docs closeout)
T2 (React RED) ─────> T5 (React GREEN) ─┘
```

**MAX_CONCURRENCY: 3.** T1, T2, and T3 have disjoint files and can begin together. T4 and T5 then run together on separate Rust and React scopes. T6 consumes both integrations and the icon; review consumes the complete change, and T7 consumes review plus live evidence. A worker must not start T4 or T5 until its corresponding RED artifact and output are saved.

## Risks and out-of-scope boundaries

- **UI thread and locks:** Existing supervisor and fetcher APIs are blocking. Production adapters must run these on background work and publish state without holding a mutex across a blocking fetch or child wait. Rapid popover open/close and repeated Refresh now must not start duplicate work.
- **Process ownership:** `Supervisor` owns a child, but `receiver.rs` currently records hosting without owning a process handle. T4 must supply an owned-process implementation so Quit/exit satisfies Requirement 6.9. A scheduler refresh may finish or stop safely, not leave a partial store write.
- **Security:** `open_view_url` and `report_url` are the route and loopback boundaries. Do not add a wildcard opener capability or let the webview fetch HTTP directly. Keep hosted receiver off by default and only allow the design's six commands.
- **Platform scope:** The app targets macOS and Windows. T6's icon acceptance is macOS-specific; Rust and UI gates must still compile and pass on the available host. No Linux tray, installer/release pipeline redesign, or change to the canonical report schema is in scope.
- **Specification truth:** This plan does not pre-check stream E or claim that the tray ships. T7 reconciles the specification and canonical docs with actual integrated evidence.

## Verification gates and evidence handoff

1. `npm --prefix dash/tray/ui ci` if dependencies changed, then `npm --prefix dash/tray/ui run typecheck` and `npm --prefix dash/tray/ui run test`.
2. `cargo fmt --check`, `cargo clippy -- -D warnings`, and `cargo test` from `dash/tray/src-tauri`; capture results and any platform limitation. Run `npm --prefix dash run lint` because it covers the tray UI.
3. Run `npm --prefix dash run build:cli` so `dash/dist/cli.js` is the current smoke target. Launch the Tauri app and complete T6's live checks, including the process child, report and event updates, command actions, setup/stale recovery, and icon appearance.
4. Run the repository's required `docs validate .` and `docs drift .` after T7 changes, using the CLI command form in root `AGENTS.md`. Refresh CodeGraph as required by repository practice before `docs drift` when semantic source changes have made the existing index stale.
5. Whole-run review covers source, tests, capability/permission grants, dependency changes, and live evidence. T7 closes only after review accepts the accumulated change and all stated behavior is observed.

The conductor receives from each task: changed paths, RED/GREEN commands and exact results, scope deviations, live observations (including screenshots for icon appearance), and unresolved gaps. The final implementation report must distinguish automated gates from observed UI behavior.

## Implementation and verification evidence

The implementation and verification run on 2026-09-21 produced the following evidence:

| Area | Evidence | Result |
|---|---|---|
| Rust runtime | Intentional RED/GREEN commands `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml` and `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test runtime_contract` passed. The tray crate passed 113 unit tests and the runtime contract passed 10 contract tests. | Pass |
| React popover | `npm --prefix dash/tray/ui run test` was taken from intentional RED to GREEN; the UI suite passed 60 tests. | Pass |
| Static UI gates | `npm --prefix dash/tray/ui run typecheck` and the applicable `npm --prefix dash run lint` gate passed. | Pass |
| CLI and Tauri process path | `npm --prefix dash run build:cli` produced the smoke target. A real Tauri launch used it, resolved the CLI's SQLite dependency correctly, reached the real loopback server at `127.0.0.1:4747` with HTTP 200, and reaped the owned child cleanly. The Tauri hook paths were corrected as part of this run. | Pass |
| Full Dash suite | `npm --prefix dash run test` reported 3,528 of 3,529 tests passed. The sole failure is the unrelated, pre-existing `dash/kyber` source-layout failure; it is not evidence against the tray change. | Known unrelated failure |

The implementation now registers and authorizes the six tray commands, composes and supervises the runtime, emits view-state changes, reports IPC/action failures with retry feedback, and uses the dedicated monochrome template glyph. The evidence above proves those contracts and the real CLI/process path; it does not prove the final macOS status-item surface.

## 2026-09-21 live deployment findings

The owner completed the missing live status-item test and found two visible defects: the
menu bar showed a letter `K` instead of the KyberDash lightsaber, and the popover remained on
`No session in this window`, `Refreshed never`, and `receiver unknown` after **Refresh now**.
That observation is the new visual RED baseline and supersedes the earlier assumption that
T6 only lacked evidence.

Read-only diagnosis separated the display failure from the data source:

- `dash/tray/src-tauri/icons/tray-template.svg` literally draws a `K`. The corresponding
  36-by-36 alpha PNG is loaded correctly and `lib.rs` correctly enables macOS template
  rendering. The canonical lightsaber already exists at
  `dash/web/public/kyberdash-logo.svg`; this is an artwork correction, not a Tauri icon bug.
- `App.tsx` obtains one initial snapshot and then relies on `view-state-changed`. The initial
  snapshot may precede runtime startup, while `capabilities/tray.json` omits Tauri's built-in
  `core:event:allow-listen` and `core:event:allow-unlisten` grants. The webview therefore
  cannot receive the later populated snapshot. The existing React test mocks `listen` and
  cannot prove native capability reachability.
- The deployed tray, web child, and receiver child were running against the intended
  `KYBERDASH_BIN` and isolated `KYBER_CANON_DB`. The seven-day report endpoint returned HTTP
  200, 90 sessions, five findings, and a non-null latest session. No source-ingestion,
  report-route, database-selection, or empty-data repair is warranted.
- The isolated database and `~/.kyberdash/refresh.lock` record a refresh owner whose PID no
  longer exists. `acquireStoreRefreshLock()` deliberately passes `waitMs: 0`, but
  `acquireCacheRefreshLock()` performs dead-owner observation and takeover only inside
  `while (monotonicNow() < deadline)`. A zero-wait caller therefore never performs its first
  observation and returns `timed-out`, contradicting the documented one-poll dead-PID
  recovery. This independently explains why **Refresh now** cannot produce a success time.

The T8–T14 repair does not change store topology, source taxonomy, IPC commands, report schema,
webview networking, or the global `~/.kyberdash` store-lock location. A future decision may
isolate locks by canonical-store path, but it is not needed to recover an abandoned lock and
is outside this pass.

## Corrective Test contract (approved)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T8 → T9 event capability reachability | New `dash/tray/src-tauri/tests/capability_contract.rs` | `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test capability_contract` | The popover capability grants event listen and unlisten in addition to the six existing custom commands, without granting webview emit, emit-to, or the broader event default set. | The new manifest contract fails specifically because `core:event:allow-listen` and `core:event:allow-unlisten` are absent. Record the test name, failing assertion, command, output, and revision before editing the manifest. | The identical assertion passes after the two narrow grants are added; all existing Rust and UI tests remain green. |
| T10 → T11 zero-wait dead-owner recovery | `dash/src/refresh/lock.test.ts` | `npm --prefix dash run test:locks` | A store refresh with `waitMs: 0` performs one safe observation: it immediately takes over a lock naming a dead PID, but returns contention without mutating a fresh lock held by a live PID. | New tests fail on the dead-owner case because the result is `timed-out`; the live-owner control already passes or remains passing. Record names, failure, command, output, and revision before production edits. | The same tests pass without sleeping or queueing behind a live owner. Existing token/mtime re-verification, heartbeat, takeover-guard, and Windows retry tests remain green. |
| T12 lightsaber template asset | `dash/tray/src-tauri/icons/tray-template.svg` and `.png` | Structural checks with `file` and `sips`; visual proof in T13 | The dedicated 36-by-36 black-and-alpha macOS template asset is a padded, recognizable projection of the canonical KyberDash lightsaber rather than a letter or coloured bundle icon. | No automated RED test: the owner's screenshot is the perceptual failure, and a pixel checksum would mirror implementation rather than behavior. Save it as the before evidence and record the current SVG's `K` path. | The SVG and generated PNG agree, the PNG remains 36-by-36 RGBA with transparency, `icon_as_template(true)` is unchanged, and T13 supplies actual light/dark/accessibility observations. |
| T13 rebuilt-bundle integration | Installed local macOS app with the existing isolated database and settings | Full gates below, build/bundle command from the runbook, then live interaction | Runtime events populate the already-open popover, receiver state updates, an abandoned refresh is recovered, and the lightsaber remains visible as a template icon. | T8, T10, and the owner's screenshot provide the three independent baselines. | Every live acceptance item below passes using the rebuilt bundle; automated output is not substituted for status-item evidence. |
| T14 review and documentation closeout | Whole accumulated diff and governed docs | Review gate suite, then `docs validate .` and `docs drift .` | The repaired implementation, evidence, plan, todo, and canonical tray claims agree. | No additional RED test; this consumes implementation and live evidence. | Whole-run review accepts the change; documentation checks have zero findings; the plan/todo close only after the owner confirms the deployed behavior. |

## Corrective dispatch tasks

### T8 — RED native event-capability contract

- **Objective:** Add a native contract test that parses `capabilities/tray.json`, proves the
  six existing custom command grants remain present, requires
  `core:event:allow-listen` and `core:event:allow-unlisten`, and rejects
  `core:event:default`, `core:event:allow-emit`, and `core:event:allow-emit-to`.
- **Files owned:** New `dash/tray/src-tauri/tests/capability_contract.rs` only. Do not edit
  the capability manifest or production code in this task.
- **RED command:**
  `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test capability_contract`
- **Evidence:** Test name, exact missing-permission assertion, command/output, revision, and
  `git diff -- dash/tray/src-tauri/tests/capability_contract.rs`.
- **Acceptance:** Failure is caused only by the missing listen/unlisten grants; parsing,
  paths, and test dependencies work. **Depends on:** None. **Required skill:** `test-dev`.

### T9 — GREEN narrow event grants

- **Objective:** Add exactly `core:event:allow-listen` and
  `core:event:allow-unlisten` to the popover capability.
- **Files owned:** `dash/tray/src-tauri/capabilities/tray.json` only. Do not edit UI,
  `permissions/tray.toml`, CSP, the RED test, or runtime code.
- **Acceptance:** T8's identical command passes; no event default/emit grants are present;
  existing Rust and UI suites remain green. **Depends on:** T8. **Required skill:**
  `tauri-dev`.

### T10 — RED zero-wait stale-lock regression

- **Objective:** Add isolated temporary-directory tests through
  `acquireStoreRefreshLock()`. One fixture contains a fresh lock naming a definitively dead
  PID and must be acquired without advancing or sleeping. A control fixture names the live
  test PID and must return `timed-out` without changing or deleting the lock bytes.
- **Files owned:** `dash/src/refresh/lock.test.ts` only. Do not edit `lock.ts` or touch the
  user's `~/.kyberdash` directory.
- **RED command:** `npm --prefix dash run test:locks`.
- **Evidence:** Test names, the dead-owner `timed-out` mismatch, command/output, revision,
  and focused diff. The live-owner control must not regress.
- **Acceptance:** The test reaches lock behavior rather than failing on setup; it proves
  recovery and non-stealing as a pair. **Depends on:** None. **Required skill:** `test-dev`.

### T11 — GREEN one-observation zero-wait acquisition

- **Objective:** Refactor `acquireCacheRefreshLock()` so a failed immediate create always
  receives one observation/takeover iteration, even when `waitMs` is zero. After that first
  iteration, the existing deadline governs further polling. Do not sleep, queue, or steal
  from a live owner.
- **Files owned:** `dash/src/refresh/lock.ts` only. Do not edit the RED test.
- **Acceptance:** T10's identical command passes; all existing lock safety tests remain
  green; live-owner file content is unchanged. **Depends on:** T10. **Required skill:**
  implementation worker with TypeScript ownership.

### T12 — Correct the macOS lightsaber template asset

- **Objective:** Replace the temporary `K` geometry with a monochrome, padded projection of
  `dash/web/public/kyberdash-logo.svg`. Maintain a transparent background and template-image
  semantics; the coloured application icon remains unchanged.
- **Files owned:** `dash/tray/src-tauri/icons/tray-template.svg` and the generated
  `dash/tray/src-tauri/icons/tray-template.png` only. The canonical web asset is a read-only
  geometry source. Do not edit `lib.rs`.
- **Evidence:** Owner screenshot and old SVG path as before evidence; focused asset diff;
  `file` output; `sips -g pixelWidth -g pixelHeight -g hasAlpha` output; T13 screenshots or
  recorded observations in light, dark, Reduce Transparency, and accent variants.
- **Acceptance:** Both assets depict the same lightsaber silhouette, PNG is 36-by-36 RGBA
  with alpha, no rectangular background appears, and the status item remains a template
  image. **Depends on:** None. **Required skill:** `tauri-dev`.

### T13 — Gates, recoverable local deployment, and live retest

Run:

1. `npm --prefix dash/tray/ui run typecheck`
2. `npm --prefix dash/tray/ui run test`
3. `npm --prefix dash run typecheck`
4. `npm --prefix dash run lint`
5. `npm --prefix dash run test:locks`
6. `npm --prefix dash run test`
7. `npm --prefix dash run check:reachable`
8. From `dash/tray/src-tauri`: `cargo fmt --check`, `cargo clippy -- -D warnings`, and
   `cargo test`

Preserve `/Users/dave/Applications/KyberDash.app`, its settings, and the active isolated
database until the replacement bundle is built and verified. Quit through the application,
verify that the web and receiver children are reaped, retain a recoverable copy of the prior
bundle, install the replacement, and relaunch with the same `KYBERDASH_BIN` and
`KYBER_CANON_DB`. Do not manually delete the global stale lock: successful application-level
takeover is part of the acceptance test.

Live acceptance requires all of the following:

1. The status item shows the lightsaber, not `K`, and remains recognizable across the four
   appearance/accessibility observations.
2. Without restart, the popover replaces the initial snapshot with the latest session within
   one open-popover poll interval; its session and harness agree with the seven-day report.
3. Receiver state becomes hosted/reachable rather than unknown.
4. **Refresh now** visibly enters a running state, then records a success timestamp; the
   isolated database records the completed refresh and advances appropriately.
5. `open_view`, `set_settings`, `hide_popover`, and `quit` still work, and Quit reaps both
   child processes. Relaunch the app for the owner's follow-up test.

### T14 — Whole-run review and closeout

Review the complete corrective diff once after T9, T11, and T12 are integrated. Rework any
accepted finding, rerun affected gates, and then update the existing evidence sections,
originating todo, and plan index. Run documentation validation and drift after documentation
edits. Keep the plan and todo active if any live item is unproved; close them only after the
owner confirms the locally deployed glyph and populated popover.

## Corrective dependency graph and concurrency audit

```text
T8 (capability RED) ──> T9 (capability GREEN) ──┐
T10 (lock RED) ───────> T11 (lock GREEN) ──────┼──> T13 (deploy/live) ──> review ──> T14
T12 (visual RED/GREEN asset) ───────────────────┘
```

**MAX_CONCURRENCY: 3.** T8, T10, and T12 own disjoint files and may start together. T9
waits for recorded T8 RED; T11 waits for recorded T10 RED. T13 waits for all three repair
branches. No worker may alter the installed bundle, settings, test database, or global lock
before T13.

## 2026-09-21 background lifecycle findings

The owner's follow-up correctly identified a durability gap, but live inspection distinguished
it from an active ingest outage:

- The deployed tray was resident as PID `22077` with web PID `22082` and OTLP receiver PID
  `22307`. The receiver answered HTTP 200 at `127.0.0.1:4318/healthz`, and the default
  canonical store continued to receive fresh Claude and Codex records. Closing the popover did
  not stop the process or its receiver.
- The current tray was an ordinary manually launched application service, not a launchd-owned
  `io.github.dpalfery.kyberdash` job. A valid
  `~/Library/LaunchAgents/io.github.dpalfery.kyberdash.plist` existed, but `launchctl print`
  reported no loaded service for that label.
- The local `~/.local/bin/kyberdash` was a developer shell wrapper whose second line was
  `exec node .../dash/dist/cli.js`. Reproducing the launchd environment with the standard
  system path made that wrapper exit 127 because `node` was not found. The manually launched
  tray worked only because it inherited the interactive shell's Homebrew path and
  `KYBERDASH_BIN` override.
- The LaunchAgent used `RunAtLoad` but deliberately omitted `KeepAlive`. That satisfies the
  earlier start-at-login and intentional-Quit contract, but it also means launchd cannot
  restart an abnormally exited tray. The approved lifecycle requires crash-only restart, not
  an independent receiver that survives **Quit**.
- A separate legacy `KyberDashMenubar` System Events login item still points at
  `/Users/dave/Applications/KyberDashMenubar.app`. It is out of scope: the corrective
  deployment must not unregister, replace, or delete that item or bundle.

The saved plan-authoring reference was unavailable in this session, so this additive
corrective pass follows the existing plan's approved task/test/evidence structure. CodeGraph
was used before source lookup; the repository's documentation MCP operations were not exposed,
so governed-doc discovery followed the documentation index fallback required by root
`AGENTS.md`.

## Background lifecycle Test contract (approved)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T15 → T16 crash-only LaunchAgent policy | New `dash/tray/src-tauri/tests/login_lifecycle_contract.rs` | `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test login_lifecycle_contract` | The generated macOS LaunchAgent runs at login and asks launchd to restart only after an unsuccessful exit. A clean tray exit remains stopped. | The new contract fails specifically because the current plist has no crash-only `KeepAlive` policy. Record test name, assertion, command, output, revision, and focused test diff before production edits. | The identical test passes with `KeepAlive` represented as a dictionary containing `SuccessfulExit=false`; `RunAtLoad`, label, absolute program argument, and XML escaping remain intact. |
| T17 self-contained local CLI | Host `darwin-arm64` SEA artifact built from the current checkout | Canonical host-RID steps from `.github/workflows/release.yml`, then the staged binary's `--version` and `codesign --verify` | The tray can resolve and execute KyberDash with a launchd-minimal environment, without `node`, Homebrew, `KYBERDASH_BIN`, or the checkout on `PATH`. | The existing `~/.local/bin/kyberdash` wrapper exits 127 under `env -i HOME=... PATH=/usr/bin:/bin:/usr/sbin:/sbin`; preserve that output as deployment RED evidence. | A staged self-contained binary reports version `0.9.23`, verifies after ad-hoc signing, and passes `--version` under the same minimal environment. The existing wrapper is not overwritten. |
| T18 launchd-owned deployment | Installed local app, staged SEA, LaunchAgent, settings, and default store | Full gates, recoverable bundle install, `launchctl` bootstrap/kickstart, process/port/store checks | With the popover closed, launchd owns one tray whose web and receiver children stay live; abnormal tray exit creates a new tray PID and resumes both children; intentional **Quit** reaps the children and does not restart the tray. | T15 and T17 supply deterministic RED evidence; the pre-fix live snapshot supplies the unloaded-service baseline. | Every live item below passes; the app is relaunched under launchd for owner testing. No legacy login item or bundle is changed. |
| T19 accumulated review and closeout | Whole corrective diff and governed docs | Review gate suite, `docs validate .`, and `docs drift .` | Source, launch policy, deployed artifact provenance, live evidence, plan, todo, and canonical tray docs agree. | No additional RED test; this task consumes T15–T18 evidence. | Whole-run review accepts the accumulated change and documentation has zero findings. Plan/todo closure still waits for owner confirmation of the deployed surface. |

## Background lifecycle dispatch tasks

### T15 — RED crash-only launch policy contract

- **Objective:** Add an integration contract over `login_item_macos::plist_for()` that requires
  the existing label, `RunAtLoad=true`, the absolute executable argument, and a `KeepAlive`
  dictionary with `SuccessfulExit=false`. Assert that `KeepAlive` is not an unconditional
  boolean and that no policy can restart a clean **Quit**.
- **Files owned:** New `dash/tray/src-tauri/tests/login_lifecycle_contract.rs` only. Do not
  edit `login_item_macos.rs`, the installed plist, the app bundle, or user settings.
- **RED command:**
  `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test login_lifecycle_contract`
- **Evidence:** Test names, the exact missing-policy assertion, command/output, revision, and
  focused test diff.
- **Acceptance:** Failure is caused by the absent crash-only policy, not XML parsing, test
  setup, or platform availability. Existing lifecycle assertions are not weakened.
- **Depends on:** None. **Required skill:** `test-dev`.

### T16 — GREEN crash-only LaunchAgent policy

- **Objective:** Change the generated macOS LaunchAgent so launchd restarts the tray after an
  unsuccessful exit while leaving a successful exit stopped. Preserve `RunAtLoad`,
  `ProcessType=Interactive`, label, absolute executable argument, atomic plist replacement,
  and XML escaping. Reconcile the existing inline unit assertion that currently bans every
  `KeepAlive` key so it instead pins the approved crash-only semantics.
- **Files owned:** `dash/tray/src-tauri/src/login_item_macos.rs` only. Do not add an
  independent receiver service, shell launcher, machine-specific path, or change Windows/Linux
  startup behavior.
- **GREEN command:** T15's identical command, then
  `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml login_item_macos`.
- **Acceptance:** Both commands pass; a clean exit remains non-restarting by construction;
  the plist stays valid under `plutil -lint` when materialized during T18.
- **Depends on:** T15. **Required skill:** `tauri-dev`.

### T17 — Build and stage a self-contained local KyberDash CLI

- **Objective:** Produce a host `darwin-arm64` Node SEA from the current checkout using the
  pinned Node `24.21.0`, `tsup.sea.config.ts`, `src/sea-shim.cjs`, postject
  `1.0.0-alpha.6`, fuse `NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2`, macOS
  `NODE_SEA` segment, and post-injection ad-hoc signing defined by the `build-kyberdash` job.
  Stage it at
  `/Users/dave/Library/Application Support/io.github.dpalfery.kyberdash/local-bin/kyberdash`
  through a temporary sibling and atomic rename. Point `~/.kyberdash/tray.json`'s
  `kyberdashPath` at that absolute binary while preserving any other keys and a recoverable
  copy of any prior file.
- **Files owned:** No repository source files. Deployment-owned paths are the new staged
  binary and `~/.kyberdash/tray.json`. The existing `~/.local/bin/kyberdash` wrapper,
  canonical database, settings, app bundle, and legacy menu-bar bundle are read-only.
- **RED evidence:** Run the existing wrapper under
  `env -i HOME=/Users/dave PATH=/usr/bin:/bin:/usr/sbin:/sbin` and record exit 127 plus the
  missing-`node` diagnostic.
- **Acceptance:** The staged binary reports `0.9.23` and passes `codesign --verify` under the
  same minimal environment; its SHA-256 and build source revision are recorded; no runtime
  resolution depends on `KYBERDASH_BIN`, shell startup files, Homebrew, or repository `PATH`.
- **Rollback:** Restore the saved `tray.json` and remove only the newly staged binary/directory.
- **Depends on:** None. **Required skill:** `github-devops` for the release-equivalent build
  and `tauri-dev` for the tray-resolution handoff.

### T18 — Gates, recoverable deployment, and launchd validation

- **Objective:** Integrate T16, build the replacement app, transition the local test instance
  from manual application ownership to launchd ownership, and prove collection while the
  popover is closed. Preserve `/Users/dave/Applications/KyberDash.app`, its settings, and its
  default canonical store until the replacement and staged CLI pass their pre-install checks.
  Keep a recoverable copy of the prior app. Do not touch `KyberDashMenubar.app` or its System
  Events login item.
- **Install location:** Keep the approved per-user installation at
  `/Users/dave/Applications/KyberDash.app`. Do not copy to `/Applications`, request
  administrator privileges, or change the fixture path in
  `login_lifecycle_contract.rs`. The test's `/Applications/KyberDash.app/...` value is an
  arbitrary absolute-path input to `plist_for`; production passes `current_exe()` and must
  therefore generate
  `/Users/dave/Applications/KyberDash.app/Contents/MacOS/kyberdash-tray` on this machine.
- **Pre-deployment gates:** Run the eight T13 gates plus T15's focused test. Materialize the
  generated plist in a temporary directory and run `plutil -lint`.
- **Deployment sequence:** Quit the current tray through its supported command; verify its web
  and receiver children are reaped; install the verified replacement bundle; preserve the
  existing settings with `launchAtLogin=true` and `hostReceiver=true`; ensure the LaunchAgent
  names the installed executable; bootstrap it into `gui/$(id -u)` and let `RunAtLoad` start
  the tray. Use `launchctl bootout` only for the new KyberDash label during a recoverable retry,
  never for the legacy login item.
- **LaunchAgent reconciliation:** Save a byte-for-byte backup of the current plist before
  changing it. Because an already-true setting does not call `set_settings` again at startup,
  reconcile this deployed plist explicitly: set `ProgramArguments.0` to the per-user
  executable above and replace `KeepAlive` with `{ SuccessfulExit = false; }`, leaving the
  label, `RunAtLoad`, and `ProcessType` unchanged. Validate with `plutil -lint` and `plutil -p`
  before `launchctl bootstrap`. Do not add `EnvironmentVariables`, a shell, or a Homebrew
  path; T17's recorded SEA must make those unnecessary.
- **Live acceptance:**
  1. `launchctl print gui/$(id -u)/io.github.dpalfery.kyberdash` reports a running job and its
     PID matches exactly one tray process.
  2. The tray owns exactly one web child and one receiver child; ports 4747 and 4318 are
     loopback listeners and `/healthz` returns HTTP 200.
  3. With the popover closed for at least one closed-popover poll interval, those PIDs remain
     live and the default store's record count or latest OTEL timestamp advances from a real
     configured harness. Do not inject synthetic content into the user's store.
  4. Terminating the tray abnormally produces a different launchd-owned tray PID with healthy
     web/receiver children and no duplicate listener.
  5. Sending the supported **Quit** path exits the tray with its children, and launchd does
     not restart it during an observation window of at least ten seconds.
  6. `launchctl kickstart` starts it again for the owner's test; the popover reports the
     receiver as hosted/reachable and shows the latest report without an interactive-shell
     environment.
- **Evidence:** Commands and timestamps, old/new PIDs, listener ownership, health response,
  before/after store metadata, staged CLI hash/revision, bundle backup path, and explicit
  confirmation that the legacy item was untouched.
- **Depends on:** T16 and T17. **Required skills:** `tauri-dev`, `test-dev`.

### T19 — Whole-run review and documentation closeout

- **Objective:** Review the complete T8–T18 accumulated change once. Rework accepted findings,
  rerun affected gates, then update implementation evidence, the originating todo, canonical
  tray architecture/runbook, this plan, and the plan/todo indexes. Keep the plan/todo active
  until the owner confirms the launchd-owned local deployment continues collecting with the
  popover closed and still respects **Quit**.
- **Files owned:** The documentation paths already granted to T14; no new source ownership.
- **Acceptance:** Whole-run review accepts the change; `docs validate .` and `docs drift .`
  report zero findings; automated, launchd, live-ingest, and owner-observed evidence remain
  separately identified.
- **Depends on:** T18 and owner confirmation. **Required skills:** `docs-dev`,
  `app-docs-standard`, `kyber-weave-docs`.

## Background lifecycle dependency graph and concurrency audit

```text
T15 (launch policy RED) ──> T16 (launch policy GREEN) ──┐
T17 (self-contained CLI build and stage) ───────────────┼──> T18 (deploy/live) ──> review ──> T19
T8–T13 corrective implementation and evidence ─────────┘
```

**MAX_CONCURRENCY: 2.** T15 and T17 have disjoint ownership and may start together. T16 waits
for recorded T15 RED. T18 is the sole owner of installed-app and launchd state and cannot
start before T16 and T17 finish. T19 consumes the accumulated review and owner evidence. No
earlier task may stop the running tray, load or unload LaunchAgents, replace the app, modify
the canonical store, or alter the preserved legacy login item.

## Superseded 2026-09-21 selector-only proposal

This section's dual-report-fetch T20–T27 proposal is retained as investigation history only.
It is not execution authority: it treated the selector symptom without repairing live OTLP
projection. The authoritative corrective pass begins at **Canonical projection correction**
below.

The owner's latest live test exposed two independent presentation defects. Read-only checks
show that neither is evidence that only Claude Desktop has been ingested:

- The unscoped seven-day report contains `antigravity-cli` (31 sessions), `claude-code` (1),
  `claude-desktop` (10), `codex-desktop` (3), `cursor` (14), `cursor-agent` (12), `opencode`
  (1), and `zcode` (1). The canonical store also contains older `codex` sessions and fresh
  Codex Desktop OTEL records. Ingestion coverage therefore exists beyond Claude Desktop.
- `runtime.rs::poll_report_inner` correctly asks for a report scoped by the persisted harness.
  `buildCoverage` correctly reports counts for that scope. However,
  `HarnessSelector.tsx` also uses the scoped report's `coverage.harnesses` as its option
  inventory. Selecting `claude-desktop` therefore collapses the selector to the one selected
  harness even though the unscoped report knows about the others.
- `buildLatestSession` asks `KyberBridge.getSessionContent()` for a `{ context }` object.
  Production `getSessionContent()` returns `{ sessionId, parts }`; it is the unclipped content
  route and never supplies derived context. `getSessionPayload()` returns the persisted
  session payload that actually contains `context`. The report test double currently masks
  the mismatch by making `getSessionContent()` return a context-shaped fixture.
- The selected live Claude Desktop session's persisted payload is measurable and contains a
  context limit, turns, buckets, and residual. Its report nevertheless returns the generic
  `harness exported no message structure for this session` fallback for every field. This is
  a report-builder source bug, not honest unobservability from the harness.

The launchd-owned tray, web process, OTLP receiver, installed bundle, staged CLI, settings,
and canonical store stay in place throughout T20–T25. A separate live observation found
stale `refresh_run` rows and terminated refresh children; that lifecycle is not explained by
either confirmed defect and is deliberately excluded from this corrective pass. It must be
reported as follow-up work rather than guessed at or mixed into these source changes.

## Cross-harness Test contract (proposed)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T20 → T21 persisted session context | `dash/src/analysis/report/build.test.ts` | `npm --prefix dash exec -- vitest run src/analysis/report/build.test.ts` | A bridge whose unclipped content result has only `{ sessionId, parts }`, while its persisted payload has measurable `context`, produces measured latest-turn fields and no generic missing-structure fallback. A payload with absent or unmeasurable context retains the honest fallback. | The new regression fails because `buildLatestSession` calls `getSessionContent`; record the failing assertion, command, output, and focused test diff before production edits. | The identical regression passes through `getSessionPayload`; existing report scope, clipping, measurability, and content-route tests remain green. |
| T22 → T23 stable native harness inventory | `dash/tray/src-tauri/tests/runtime_contract.rs` | `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test runtime_contract` | With `claude-desktop` persisted, the runtime fetches an unscoped report for selector inventory and a Claude-scoped report for displayed content. `ViewState` retains every nonzero harness from the unscoped report without widening the displayed report. Selecting Codex changes only the scoped report. | New assertions fail because the current runtime fetches only the scoped URL and `ViewState` has no independent inventory. Record test names, URLs, output, revision, and focused test diff. | The same assertions pass; report scope remains selected, inventory remains unscoped, zero-count rows are not invented, and fetch failures preserve the last successful inventory under the existing stale-state rules. |
| T24 → T25 stable webview harness inventory | `dash/tray/ui/src/commands.test.tsx` | `npm --prefix dash/tray/ui run test -- commands.test.tsx` | `HarnessSelector` renders its options from `ViewState`'s independent inventory while the selected report remains harness-scoped; choosing Codex sends the existing settings patch and does not mutate report data client-side. | A new test fails because `HarnessSelector` can only derive options from `report.coverage.harnesses`. Record the assertion, command, output, revision, and focused test diff. | The identical test passes; `All harnesses`, counts, selected value, existing settings command, and empty/setup behavior remain correct. |
| T26 deployment and owner retest | Built Dash CLI and tray bundle, existing launchd job and default store | Focused GREEN commands, full Dash/tray gates, recoverable bundle replacement, then live report checks | The selector lists all harnesses while Claude Desktop is selected; Codex Desktop can be selected directly; Claude Desktop renders measured latest-turn context instead of the generic fallback. | T20, T22, and T24 provide deterministic RED evidence; the owner's screenshots provide the live baseline. | Automated gates pass and all three behaviors are observed in the installed launchd-owned tray without clearing settings, rebuilding the store, or changing the legacy login item. |
| T27 accumulated review and closeout | Whole T20–T26 diff and governed docs | Review gate suite, `docs validate .`, and `docs drift .` | Source, tests, report and IPC contracts, deployment evidence, plan, todo, and canonical tray docs agree. | No additional RED test; this task consumes T20–T26 evidence. | Whole-run review accepts the accumulated change and documentation has zero findings. Plan/todo closure still waits for owner confirmation. |

## Cross-harness dispatch tasks

### T20 — RED persisted-payload report regression

- **Objective:** Correct the test double so content and persisted analysis are different bridge
  contracts. Add a regression where `getSessionContent()` returns only its production
  `{ sessionId, parts }` shape and `getSessionPayload()` returns measurable `context`. Assert
  exact latest-turn pressure, window, bucket, residual, index, and absence of the generic
  missing-structure reason. Preserve a control case for a genuinely absent context payload.
- **Files owned:** `dash/src/analysis/report/build.test.ts` only.
- **Acceptance:** The focused command fails for the production mismatch, not a missing fixture
  or type error. Record RED output and the focused diff before T21.
- **Depends on:** None. **Required skill:** `test-dev`.

### T21 — GREEN latest-session context source

- **Objective:** Make `buildLatestSession` read derived analysis from
  `KyberBridge.getSessionPayload()` rather than the unclipped content route. Do not alter
  `getSessionContent`, payload clipping, bucket calculations, fallback wording, report scope,
  or the bridge's public route contracts.
- **Files owned:** `dash/src/analysis/report/build.ts` only.
- **Acceptance:** T20's identical focused test passes, followed by the complete report test
  file. Measurable payloads render measured values; truly absent context remains unmeasurable.
- **Depends on:** T20. **Required role:** a TypeScript implementation worker selected by the
  conductor.

### T22 — RED native inventory/scoped-report contract

- **Objective:** Extend the runtime contract with separate fake responses for an unscoped
  inventory request and the selected-harness report. Pin request URLs, state projection,
  inventory retention after a scoped fetch failure, and a Claude-to-Codex settings change.
  Assert that the displayed report is never replaced by the unscoped inventory report.
- **Files owned:** `dash/tray/src-tauri/tests/runtime_contract.rs` only.
- **Acceptance:** The focused Rust contract fails because the runtime has one report fetch and
  one scoped coverage list. Preserve the assertions unchanged for T23.
- **Depends on:** None. **Required skill:** `test-dev`.

### T23 — GREEN native stable inventory

- **Objective:** Add an explicit harness-inventory field to native `ViewState`. Fetch that
  inventory from the existing unscoped report URL and continue fetching displayed content
  from the existing selected-harness URL. Keep independent last-success state so a scoped
  failure follows current stale semantics without erasing the selector. Reuse existing
  bounded loopback fetching, URL validation, cadence, and event publication; do not add a
  webview HTTP path or change server report semantics.
- **Files owned:** `dash/tray/src-tauri/src/api.rs`, `dash/tray/src-tauri/src/runtime.rs`, and
  `dash/tray/src-tauri/src/ipc.rs` only.
- **Acceptance:** T22's identical contract passes. Existing runtime, API security, polling,
  scheduler, and IPC tests remain green.
- **Depends on:** T22. **Required skill:** `tauri-dev`.

### T24 — RED webview selector contract

- **Objective:** Add a component regression that passes a Claude-scoped report plus an
  independent multi-harness inventory, then asserts that Claude, Codex, and every supplied
  harness remain selectable with their unscoped counts. Assert the existing settings patch
  when Codex is chosen and an honest empty selector when no inventory exists.
- **Files owned:** `dash/tray/ui/src/commands.test.tsx` only.
- **Acceptance:** The focused test fails because the component has no independent inventory
  input; it must not fail from test setup or a missing DOM runtime.
- **Depends on:** None. **Required skill:** `test-dev`.

### T25 — GREEN webview selector projection

- **Objective:** Carry the native inventory through the TypeScript `ViewState` and `Popover`
  into `HarnessSelector`. Render options solely from that inventory while retaining the
  selected report for cards, findings, latest session, and refresh state. Keep `All harnesses`
  and the existing `set_settings({ harness })` command unchanged.
- **Files owned:** `dash/tray/ui/src/viewState.ts`, `dash/tray/ui/src/Popover.tsx`, and
  `dash/tray/ui/src/components/HarnessSelector.tsx` only.
- **Acceptance:** T24's identical test passes; `App.test.tsx`, `Popover.test.tsx`, all command
  tests, UI typecheck, and UI lint remain green.
- **Depends on:** T24 and T23's agreed serialized field name. **Required skill:** `react-dev`.

### T26 — Gates, recoverable deployment, and live retest

- **Objective:** Integrate T21, T23, and T25; run the focused and full gates below; rebuild the
  self-contained CLI and Tauri bundle; then replace the local app recoverably without changing
  launchd policy, settings, the canonical store, or the legacy `KyberDashMenubar` item. Restart
  only `io.github.dpalfery.kyberdash` through its existing supported launchd flow.
- **Live acceptance:** With `claude-desktop` selected, record the full multi-harness option
  list and counts; select `codex-desktop` without first choosing All and verify the report is
  Codex-scoped; return to Claude Desktop and record measured current-context values with no
  generic missing-structure reason. Also confirm the web and OTLP children remain healthy.
- **Refresh boundary:** Record any stale derived-session/`refresh_run` lifecycle symptom as a
  separate follow-up. It is not a reason to alter refresh code in T26 and is not evidence that
  T20–T25 failed if the pinned existing session data satisfies their contracts.
- **Acceptance:** Focused and full gates pass, installed provenance is recorded, rollback is
  available, and all live acceptance items are observed. A failed item returns to its owning
  GREEN task rather than prompting an unplanned store rebuild.
- **Depends on:** T21, T23, and T25. **Required skills:** `tauri-dev`, `test-dev`.

### T27 — Whole-run review and documentation closeout

- **Objective:** Review the complete T20–T26 change once. Rework accepted findings, rerun
  affected gates, then update implementation evidence, the originating todo, canonical tray
  architecture/runbook, this plan, and the plan/todo indexes. Keep the plan active until the
  owner confirms the deployed selector and measured context.
- **Files owned:** The documentation paths already granted to T14; no new source ownership.
- **Acceptance:** Whole-run review accepts the change; documentation validation and drift have
  zero findings; the independent refresh lifecycle is reported without being marked fixed.
- **Depends on:** T26 and owner confirmation. **Required skills:** `docs-dev`,
  `app-docs-standard`, `kyber-weave-docs`.

## Cross-harness dependency graph and concurrency audit

```text
T20 (payload RED) ──> T21 (payload GREEN) ──────────────┐
T22 (native RED) ───> T23 (native GREEN) ──┐           ├──> T26 (gates/deploy/live) ──> review ──> T27
T24 (webview RED) ─────────────────────────┴─> T25 ────┘
```

**MAX_CONCURRENCY: 3.** T20, T22, and T24 have disjoint test ownership and may start
together. Each GREEN task waits for its recorded RED. T25 waits for T23's serialized field
name but does not edit Rust. T26 is the sole owner of installed app and launchd state. No
earlier task may stop the tray, replace the app, mutate settings, rebuild the canonical store,
or touch the legacy login item.

## Cross-harness verification gates and evidence handoff

1. Focused RED/GREEN: `npm --prefix dash exec -- vitest run src/analysis/report/build.test.ts`,
   `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml --test runtime_contract`, and
   `npm --prefix dash/tray/ui run test -- commands.test.tsx`.
2. Dash: `npm --prefix dash run typecheck`, `npm --prefix dash run lint`,
   `npm --prefix dash run test`, and `npm --prefix dash run check:reachable`.
3. Tray UI: `npm --prefix dash/tray/ui run typecheck` and
   `npm --prefix dash/tray/ui run test`.
4. Tauri: from `dash/tray/src-tauri`, run `cargo fmt --check`,
   `cargo clippy -- -D warnings`, and `cargo test`.
5. After source gates, build the CLI and app through the same local release-equivalent path
   already used by T17–T18, preserve the current bundle before replacement, and perform T26's
   live test against the existing launchd-owned service and canonical store.
6. After documentation edits, refresh CodeGraph when required, then run the root `AGENTS.md`
   forms of `docs validate .` and `docs drift .`. Whole-run review consumes every RED/GREEN,
   gate, deployment-provenance, live observation, and independent-follow-up record.

## Superseded decision record

No product or architecture decision is open. Existing requirements already require a
cross-harness selector, honest measurability, local loopback transport, and one canonical
store. The inventory field and corrected bridge method are reversible contract repairs within
those decisions. Execution of proposed T20–T27 still requires the conductor's explicit
approve-and-execute gate; the earlier approvals cover T1–T19 only.

## Canonical projection correction

### Confirmed architecture and root cause

The owner approved one canonical reporting model: static dot-folder refresh and live OTLP are
separate ingress adapters, but both must end in the same projection over
`~/.kyberdash/canon.db`. Web, tray, and CLI then consume that projection through the existing
Kyber API and `ContextReport`; no surface derives sessions from raw records.

Current code diverges in four places:

- `dash/src/refresh/orchestrator.ts` calls `buildSessions()` after static ingest, while
  `dash/src/otel/service.ts` commits accepted spans and log enrichment without rebuilding the
  derived `session`, `run`, `execution`, `harness_rollup`, and `finding` caches.
- `KyberBridge.listSessions()` hides that gap by synthesizing record-only sessions. Those rows
  have no authoritative persisted payload and can expose stale or non-canonical identities.
- `buildLatestSession()` casts `getSessionContent()`'s `{ sessionId, parts }` response to a
  context payload. The actual derived `context` lives in `getSessionPayload()`.
- `buildCoverage()` inventories all rollup rows and applies the selected report scope to their
  counts. Requirement 8.8 instead requires the canonical harnesses that have a derived session
  in the active day window. That navigation inventory must remain available when one harness is
  selected, while findings, dimensions, latest session, and cost remain selected-scope data.

ADRs 0008 and 0009 already decide the store and multi-signal model. Context-surfaces Decision
D2 already decides one `ContextReport`. No new ADR is required. Stale `refresh_run` rows are
not on the data path and remain out of scope unless T20 proves they prevent projection drain or
retry; they must not be mixed into this correction merely to make the health footer look fresh.

### Canonical projection Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T20 → T21 shared projection freshness | New `dash/src/canon/projection.test.ts`, `dash/src/otel/service.test.ts`, and `dash/src/refresh/orchestrator.test.ts` | `npm --prefix dash exec -- vitest run src/canon/projection.test.ts src/otel/service.test.ts src/refresh/orchestrator.test.ts` | A claimable OTLP span becomes a persisted derived session without static refresh; matching log enrichment updates that payload; burst requests run no overlapping projections, coalesce one trailing pass, retain dirty work after failure, and drain before collector close. Static refresh calls the same projection entry point. | New assertions fail because OTLP only writes `records` and no shared projection coordinator exists. Record failing assertions or the missing production-seam diagnostic, command, output, revision, and focused diff before T21. | Identical tests pass through one `projectCanonicalStore()` entry point over `buildSessions()`. Accepted records survive projection failure, the next request retries dirty work, and close waits for writer then projector before closing SQLite. |
| T22 → T23 derived-session reporting authority | `dash/src/server/kyber-bridge.test.ts` and `dash/src/server/kyber-api.test.ts` | `npm --prefix dash exec -- vitest run src/server/kyber-bridge.test.ts src/server/kyber-api.test.ts` | A store containing two derived sessions plus a raw record-only group lists and serves only the two derived sessions. The record-only id is absent from `/api/kyber/sessions` and has no session payload. Ordering and limit still apply to derived rows. | Change the existing “combines canonical derived sessions and records” regression to the authoritative expectation and record its failure on the raw fallback. | The same tests pass after deleting the records-table fallback, its aggregation types, and now-unused cost import. No legacy or raw-record session synthesis remains. |
| T24 → T25 canonical report payload and inventory | `dash/src/analysis/report/build.test.ts` and `dash/src/cli/report-api-parity.test.ts` | `npm --prefix dash exec -- vitest run src/analysis/report/build.test.ts src/cli/report-api-parity.test.ts` | Unclipped content returns only `{ sessionId, parts }`, while persisted payload context produces measured latest-turn fields. A Claude-scoped seven-day report carries a stable coverage inventory for every canonical derived harness with a session in those seven days, excludes outside-window and rollup-only harnesses, and keeps findings/latest/cost Claude-scoped. CLI JSON and REST are equal for the same seeded store and scope. | The payload regression fails because `buildLatestSession()` calls `getSessionContent()`. The inventory regression fails because `buildCoverage()` maps rollups and selected-scope counts. Record both failures and focused diffs before T25. | The same tests pass through `getSessionPayload()` and a window-only inventory derived from `listSessions()`. Inventory counts are stable under harness selection; report analysis remains selected-scope; truly absent context retains the honest unmeasurable reason. |
| T26 integrated UI/API contract | `dash/tray/ui/src/commands.test.tsx`, `dash/tray/ui/src/Popover.test.tsx`, and `dash/src/server/report-route.test.ts` | `npm --prefix dash/tray/ui run test -- commands.test.tsx Popover.test.tsx` and `npm --prefix dash exec -- vitest run src/server/report-route.test.ts` | `HarnessSelector` renders the report's canonical windowed inventory and sends only `set_settings({ harness })`; selecting Codex does not derive, merge, or fetch a second report in the webview. The report route preserves days and harness scope. | T24 supplies the engine RED; this integration task adds no duplicate failing derivation test. | Existing component behavior passes against a scoped multi-harness report fixture. No new Rust fetch, direct database read, or webview network permission exists. |
| T27 deployment and owner retest | Built CLI and tray bundle, existing LaunchAgent, settings, and canonical store | Focused GREEN commands, full gates, recoverable local replacement, then live API/process/UI checks | New real OTLP reaches a derived session and both API-backed surfaces; menu context is measured from the persisted payload; selector entries match canonical sessions in the selected day window. | T20, T22, and T24 provide deterministic RED evidence; the owner's screenshots and live store provide the deployed baseline. | Automated gates pass and the installed launchd-owned tray demonstrates the same store, projection, report, inventory, and payload as the web/API without clearing settings or rebuilding the user's database. |
| T28 review and documentation closeout | Whole accumulated change and governed docs | Review gate suite, then `docs validate .` and `docs drift .` | Source, tests, architecture, runbook, specification wording, plan, todo, and deployed evidence describe one canonical projection and report contract. | No additional RED test; this task consumes T20–T27 evidence. | Whole-run review accepts the change and documentation checks have zero findings. Plan/todo closure waits only for the owner's deployed confirmation. |

### T20 — RED shared canonical projection contract

- **Objective:** Add a deterministic projection-coordinator contract and a service-level OTLP
  regression. Use an in-memory store and a claimable adapter fixture. Prove immediate request,
  single-flight execution, one trailing pass for dirtiness arriving in flight, retry after a
  projection failure without losing the committed record, and drain-on-close. Add a matching
  log that enriches the stored span and prove the reprojected payload changes. Extend the
  refresh test only to pin that static refresh reaches the same exported projection entry.
- **Files owned:** New `dash/src/canon/projection.test.ts`,
  `dash/src/otel/service.test.ts`, and `dash/src/refresh/orchestrator.test.ts` only.
- **Acceptance:** The focused command fails on missing OTLP projection/shared coordination,
  not on an invalid fixture or unrelated runner error. Preserve assertions unchanged for T21.
- **Depends on:** None. **Required skill:** `test-dev`.

### T21 — GREEN shared projection pipeline

- **Objective:** Add `projectCanonicalStore(store)` as the sole exported full projection over
  `buildSessions()` and a serialized/coalescing `CanonicalProjectionScheduler` for the live
  service. Static refresh calls the same function once after its writer drains. Live accepted
  span batches and successful log enrichment mark the scheduler dirty; one projection runs at
  a time and dirtiness during a run causes exactly one trailing pass. Projection failure must
  not roll back or requeue an already committed canonical record; retain dirty state, report
  the error, and retry on the next request. Collector shutdown orders receiver stop, writer
  stop, projection drain, then store close.
- **Files owned:** New `dash/src/canon/projection.ts`, `dash/src/otel/service.ts`, and
  `dash/src/refresh/orchestrator.ts`. `buildSessions()` remains the authoritative derivation;
  do not fork its session/run/rollup/finding logic or add a second database.
- **Acceptance:** T20's identical tests pass. Existing ingest, writer, session, refresh, and
  OTLP tests remain green. No projection overlaps and no accepted record is dropped on error.
- **Depends on:** T20. **Required role:** `python-dev` is not applicable; dispatch a
  TypeScript implementation worker selected by the conductor.

### T22 — RED derived-session authority

- **Objective:** Rewrite the bridge regression so record-only groups are explicitly excluded,
  then pin the same behavior through `/api/kyber/sessions`. Preserve derived ordering, limit,
  summary parsing, and null-payload controls.
- **Files owned:** `dash/src/server/kyber-bridge.test.ts` and
  `dash/src/server/kyber-api.test.ts` only.
- **Acceptance:** RED points at the records fallback and the assertions remain unchanged.
- **Depends on:** None. **Required skill:** `test-dev`.

### T23 — GREEN remove raw-record reporting fallback

- **Objective:** Make `KyberBridge.listSessions()` read only the canonical derived `session`
  cache. Delete the raw `records` aggregation branch, `TraceRecordRow`, `seenIds`, and imports
  used only by that fallback. Do not change raw content endpoints or canonical ingest.
- **Files owned:** `dash/src/server/bridge.ts` only.
- **Acceptance:** T22 passes with bridge, API, content-route, and span-attribute route tests
  green. Record-only ids cannot enter reports or selector inventory.
- **Depends on:** T22 and T21, so live records have an authoritative projection before the
  fallback is removed. **Required role:** TypeScript implementation worker.

### T24 — RED persisted payload and windowed inventory

- **Objective:** Correct the report stub so content and persisted payload are distinct. Add a
  measurable payload regression and a true absent-context control. Seed inside-window Claude
  and Codex derived sessions, an outside-window session, and a rollup with no window session;
  assert the scoped report's selector inventory is exactly the inside-window canonical
  harnesses with unscoped window counts, while all analytic sections remain Claude-scoped.
  Extend CLI/REST parity with the same two-harness scoped store.
- **Files owned:** `dash/src/analysis/report/build.test.ts` and
  `dash/src/cli/report-api-parity.test.ts` only.
- **Acceptance:** Both root defects produce focused RED evidence, not a fixture/type failure.
- **Depends on:** None. **Required skill:** `test-dev`.

### T25 — GREEN canonical report contract

- **Objective:** Make `buildLatestSession()` read `getSessionPayload()`. Build
  `coverage.harnesses` from canonical derived sessions filtered only by the report's active
  day window, grouped and stably ordered by canonical harness id; enrich measurability from a
  matching rollup when present and use an empty measurability map when absent. Continue using
  the fully selected `scoped` list for hints, findings, dimensions, latest session, and cost.
  This field is selector/navigation metadata required by 8.8, not a second analytic report.
- **Files owned:** `dash/src/analysis/report/build.ts` and clarifying comments in
  `dash/src/analysis/report/types.ts` and
  `dash/tray/ui/src/components/HarnessSelector.tsx` only if the type contract requires them.
  Do not add a Tauri inventory field or an unscoped second fetch.
- **Acceptance:** T24 passes, report renderers and fixtures remain green, and schema shape stays
  compatible. CLI and REST parity is exact for the same store and scope.
- **Depends on:** T24 and T23. **Required role:** TypeScript implementation worker.

### T26 — Integrated tray/report verification

- **Objective:** Run the focused route and UI commands from the Test contract. Confirm the
  existing Rust runtime still performs one bounded `/api/kyber/report` fetch for the selected
  harness and days, and the webview still holds no analysis or HTTP path. Update a fixture only
  if its canonical generated report changed; never hand-author competing report data.
- **Files owned:** Test fixtures and comments only when demanded by generated contract output;
  no new production ownership is expected.
- **Acceptance:** Report route, command, Popover, Rust runtime, and API parity tests pass; the
  selector stays usable under a selected harness without a native dual-fetch workaround.
- **Depends on:** T21, T23, and T25. **Required skills:** `test-dev`, `react-dev`, `tauri-dev`.

### T27 — Gates, recoverable local deployment, and owner retest

- **Objective:** Run the gates below. Repeat T17's pinned Node SEA build/stage for the changed
  TypeScript CLI, then repeat T18's verified per-user Tauri bundle replacement at
  `/Users/dave/Applications/KyberDash.app`. Preserve the current app and plist backups,
  `~/.kyberdash/tray.json`, `launchAtLogin=true`, `hostReceiver=true`, the canonical database,
  and the legacy `KyberDashMenubar` item. Restart only
  `io.github.dpalfery.kyberdash` through its supported launchd flow.
- **Live acceptance:** Verify web and OTLP child processes both hold the same canonical store;
  record the store's pre-test maximum timestamp/session count; let a real configured harness
  emit OTLP with the popover closed; verify a new/updated derived session and payload appears
  without manual refresh; compare `GET /api/kyber/report` with the web dashboard and popover for
  the same harness/days; verify Codex and every other canonical in-window harness appears in
  the selector; verify Claude measured context no longer shows the generic fallback. Do not
  inject synthetic data into or wipe the user's store.
- **Rollback:** Restore only the saved app/plist/settings artifacts and restart the prior
  launchd job. Never delete or rebuild `canon.db` as deployment cleanup.
- **Acceptance:** All gates and live checks pass with provenance, timestamps, PIDs, store path,
  staged CLI hash, bundle backup, and rollback location recorded.
- **Depends on:** T26. **Required skills:** `github-devops`, `tauri-dev`, `test-dev`.

### T28 — Whole-run review and documentation closeout

- **Objective:** Run one `code-review` council over the complete accumulated change, rework
  accepted findings, and rerun affected gates. Then update `docs/dash/architecture.md`,
  `docs/dash/runbook.md`, context-surfaces requirements 8.8/11.11 and design D2 only to clarify
  the already-approved windowed navigation inventory, the originating todo and indexes, and
  this plan. Record stale refresh-run bookkeeping as separate deferred work only if still
  reproducible; do not claim it fixed by projection work.
- **Files owned:** Canonical KyberDash docs, context-surfaces requirements/design/tasks,
  `docs/todo/menu-bar-fix.md`, todo index, this plan, and the plan index.
- **Acceptance:** Review accepts the accumulated change; documentation checks report zero
  findings; owner-confirmed deployed behavior is distinct from automated evidence.
- **Depends on:** T27 and owner confirmation. **Required skills:** `code-review`, `docs-dev`,
  `app-docs-standard`, `kyber-weave-docs`.

### Dependency graph and concurrency audit

```text
T20 (projection RED) ──> T21 (projection GREEN) ──┐
                                                   ├──> T23 (bridge GREEN) ──┐
T22 (bridge RED) ──────────────────────────────────┘                         │
T24 (report RED) ───────────────────────────────────────────> T25 (report) ──┤
                                                                             └──> T26 ──> T27 ──> review ──> T28
```

**MAX_CONCURRENCY: 3.** T20, T22, and T24 own disjoint test files and may start together.
T21 waits for T20. T23 waits for both bridge RED and projection GREEN. T25 waits for T24 and
the bridge authority. T26 is the integration barrier. T27 alone owns installed app, launchd,
settings, and deployment state. T28 follows review and owner evidence.

### Verification gates and evidence handoff

1. Focused RED/GREEN commands are the three commands in the Test contract; retain exact RED
   output and focused diffs before each GREEN dispatch.
2. Run `npm --prefix dash run typecheck`, `npm --prefix dash run lint`,
   `npm --prefix dash run test`, `npm --prefix dash run test:locks`, and
   `npm --prefix dash run check:reachable`.
3. Run `npm --prefix dash/tray/ui run typecheck` and
   `npm --prefix dash/tray/ui run test`.
4. From `dash/tray/src-tauri`, run `cargo fmt --check`,
   `cargo clippy -- -D warnings`, and `cargo test`.
5. Run T27's pinned SEA/Tauri build, recoverable install, launchd process/port/store checks,
   real OTLP-to-projection freshness test, REST/web/tray parity comparison, and owner retest.
6. After documentation edits, refresh CodeGraph when required, then run the root `AGENTS.md`
   forms of `docs validate .` and `docs drift .`.

### Approval record

The conductor relayed the owner's approval of the shared canonical-projection architecture and
authorization to execute this plan on 2026-09-21. Development mode remains test-first. There
are no open product or implementation decisions; the precise internal report-field treatment
is pinned by T24–T25 as compatible `coverage.harnesses` navigation metadata, not a second
endpoint or report fetch.

## Closeout (T28, 2026-09-22)

**Review verdict.** The code-review council returned **Approve** on 2026-09-22 with risk
**LOW** and zero findings. All ten declared gates exited 0: dash typecheck, lint, test
(3546/3546), test:locks (36/36), and check:reachable; tray UI typecheck and 60/60 tests;
cargo fmt, clippy, and 125 Rust tests.

**Owner confirmation.** The owner confirmed the deployed behavior on 2026-09-22: the
multi-harness selector lists the canonical in-window inventory, Codex can be scoped directly,
and the Claude latest-session panel shows measured context with no generic fallback. The
deployed shape is the launchd-owned per-user tray (label `io.github.dpalfery.kyberdash`,
crash-only `KeepAlive { SuccessfulExit = false }`) running the self-contained staged CLI
(Node SEA, darwin-arm64, version 0.9.23, SHA-256
`b8b911ff13de23e923a8487e63a43eed167da2967732f53d61e22e7ee65ceb11`) at
`~/Library/Application Support/io.github.dpalfery.kyberdash/local-bin/kyberdash`.

**Shipped model, as confirmed.** Static dot-folder refresh and live OTLP remain separate
ingress adapters; both end in one shared projection over `~/.kyberdash/canon.db` through
`projectCanonicalStore()` over `buildSessions()`. Live batches mark a serialized
`CanonicalProjectionScheduler` dirty — single-flight, one trailing pass, failure retains the
dirty work and retries on the next request, and shutdown drains in receiver-stop →
writer-stop → projection-drain → store-close order. No surface derives sessions from raw
records: `KyberBridge.listSessions()` reads only canonical derived sessions, the raw-records
fallback having been deleted. `buildLatestSession()` reads the persisted payload
(`getSessionPayload()`), so measurable sessions show measured latest-turn context and truly
absent context keeps the honest fallback. `coverage.harnesses` is the window-only canonical
derived-session inventory — the selector/navigation metadata of Requirement 8.8, carrying
unscoped window counts and stable under harness selection — while findings, dimensions,
latest session, and cost remain selected-scope.

**Commit-message caveat.** Commit 5f2fe223's message cites the dash suite as 3545/3546 with
the source-layout `kyber` failure. That was stale on arrival: two untracked debris trees
(`dash/kyber/`, `dash/kyber-weave/dash/kyber/`, node_modules-only) were deleted the same day
with explicit owner approval after a guard verified nothing tracked was removed, and the
suite is 3546/3546 green at HEAD.

**Report-only dispositions (known notes, no action).** The manual `kyberdash build` CLI
command calls `buildSessions` directly rather than through `projectCanonicalStore`
(byte-identical behavior; the projection entry is a pure delegation). The REST report's
`coverage.storePath` renders relative where the CLI renders absolute; no requirement pins it.

**Deferred work.** Stale `refresh_run` rows remained reproducible on the deployed build
(rows at 03:34, 03:38, and 03:43 across 2026-09-21/22) and are recorded as separate deferred
work in [docs/archive/todo/stale-refresh-run-rows.md](../todo/stale-refresh-run-rows.md); they are
not on the report data path and are not claimed fixed by the projection work. The originating
todo, [docs/todo/menu-bar-fix.md](../todo/menu-bar-fix.md), is closed as completed by this
plan.

**Documentation closeout.** `docs/dash/architecture.md` and `docs/dash/runbook.md` now
describe the shared projection, the derived-session report contract, and the deployed tray;
context-surfaces requirements 8.8 and 11.11 and design D2 were clarified (wording only) so
the windowed navigation inventory is explicit; and the stale `dash/kyber/**` layout
references in those two canonical docs were corrected to the unified `dash/src/**` tree,
matching the debris cleanup above. The canonical-projection section of this plan is the
executed pass; the earlier T1–T19 corrective passes and the superseded selector-only proposal
remain as recorded history.

**Mechanical archival step.** The closeout session ran without a shell tool, so one physical
step remains for the orchestrator: move this file to `docs/archive/plans/`, set its
frontmatter to `id: archive/plans/2026-09-20-kyberdash-menu-bar-runtime-wiring` and
`status: archived` with the body header **Status:** Archived / **Archive Date:** 2026-09-22
(the convention of the other archived plans), repoint its relative links one level deeper
(`../../…`), and switch this plan's row in the plan index to the `../archive/plans/…` link.
Until that move, the file stays here and the index's archived register points at it.
