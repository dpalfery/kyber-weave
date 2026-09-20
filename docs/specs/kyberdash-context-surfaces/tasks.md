---
id: specs/kyberdash-context-surfaces/tasks
title: KyberDash context surfaces tasks
doc-type: spec
status: draft
owner: dpalfery
last-reviewed: 2026-09-18
component: KyberDash
---

# Implementation Plan

## How to read this plan

Every task is written test-first: write or adapt the failing test named in the task, then
make it pass. A task is done only when the gates for the paths it touched pass: the npm
scripts for `dash/`, `cargo` for `dash/tray/src-tauri`, `dotnet test` for .NET, and
`docs validate` plus `docs drift` for `docs/`. The gate commands are listed in the
repository's `AGENTS.md`.

Each task carries a **stream** tag from the design's
[delivery and delegation](design.md#delivery-and-delegation) section. The stream fixes which
paths the task may write:

| Stream | May write | Harness | Starts when |
|---|---|---|---|
| A | `dash/**` (moves and deletions), CI configs, `AGENTS.md`, `tests/KyberWeave.Tests/` severance tests, ADR 0006 and 0020 | Claude Code | now |
| B | `dash/src/{analysis,canon,refresh,otel,server,cli/web.ts}` | Claude Code | A merged |
| C | `dash/src/analysis/report/render-*`, `dash/src/cli/report.ts`, removal of the Ink dashboard | Antigravity | 5.1 and 5.4 merged |
| D | `dash/web/**` | Antigravity | A merged; 7.1 also needs 5.7 |
| E | `dash/tray/**`, the tray jobs in `ci.yml` | Cursor | A merged; the UI tasks need 5.4 fixtures |
| F | `dash/src/install/`, `scripts/install.sh`, `src/KyberWeave.Cli/Update/`, the `build-tray` job in `release.yml` | Claude Code | E's 8.1; 9.4 also needs the signing secrets |
| G | `docs/**` except ADR 0006 and 0020 | Claude Code | throughout; 10.1 last |

A delegated stream never writes outside its paths. If it needs a change elsewhere, for
example a missing REST field, it stops and asks. It does not work around the gap in its own
paths, because Requirement 7.5 forbids analysis in the tray.

### Delegation brief

Each delegated session gets this brief, filled in from the task. Nothing else is assumed to
carry over from the session that wrote the plan.

```text
Repository: dpalfery/kyber-weave. Read AGENTS.md first and follow it.
Spec: docs/specs/kyberdash-context-surfaces/ (requirements.md, design.md, tasks.md).
Do tasks <ids> only, in order, test-first. Tick each checkbox in tasks.md when its tests pass.
You may write only: <stream paths>. Do not edit any other path; if you need to, stop and report.
Contract: build against dash/src/analysis/report/types.ts and fixtures/*.json; do not change them.
Gates to run before finishing: <commands from AGENTS.md for the paths touched>.
Finish by opening a PR into <integration branch> titled "<stream>: <task ids>", listing the
requirement ids satisfied and any gate you could not run, with the reason.
```

Cursor: `cursor-agent -p --force -w kyberdash-<stream> "<brief>"`.
Antigravity: `git worktree add ../kw-<stream> <integration branch> && cd ../kw-<stream> &&
agy -p --dangerously-skip-permissions "<brief> Follow the conductor and react-dev skills."`.

## Tasks

- [ ] 1. Remove the soft-fork machinery
- [x] 1.1 Retire the merge-boundary rules and vendored-path exclusions
  - Stream A.
  - Delete `tests/KyberWeave.Tests/MergeBoundaryTests.cs`. Remove the merge-zone import rule
    from `dash/kyber/tools/boundary.ts`, keeping its cost-isolation rule (Decision D9), which
    is independent of the fork; the file becomes `cost-isolation.ts` and `test:boundary`
    becomes `test:cost-isolation`.
  - Remove the vendored-path exclusions for `dash/**` from the semgrep and trivy steps of
    `.github/workflows/ci.yml` and from `.github/codeql/codeql-config.yml`.
  - Replace the merge-zone non-negotiable in `AGENTS.md` with a statement that `dash/` is
    first-party code under the same gates.
  - `dotnet test` and `npm --prefix dash run test` pass with the rules gone.
  - _Requirements: 1.1, 1.2, 1.4, 4.3_
- [x] 1.2 Record the one-time fork and archive ADR 0006
  - Stream A.
  - Write `docs/adr/0020-…md`. It records the fork commit, restates ADR 0006's receiver,
    canonical-model and SEA-distribution decisions, and re-decides the engine language on
    current grounds.
  - Move ADR 0006 to `docs/archive/adrs/` with status `superseded`, and repoint every live
    link to it (the ADRs, `docs/dash/*`, `docs/reference/kyberdash-rationale.md`). Name it in
    ADR 0020 in prose, not in `supersedes`.
  - Correct the ADR index's sentence about superseded ids resolving, and add the 0020 row.
  - Remove the merge-zone sections from `docs/dash/architecture.md`, and add the
    remove-your-`codeburn`-remote instruction to the runbook.
  - `docs validate` and `docs drift` report zero findings.
  - _Requirements: 1.3, 1.4, 1.7, 1.8, 1.9, 1.10, 13.3_

- [ ] 2. Prune upstream-only features
- [x] 2.1 Add the reachability check
  - Stream A.
  - Write `dash/scripts/unreachable.mjs`. It walks the import graph (TypeScript
    `preProcessFile`, so type-only and dynamic imports count) from every entry and lists each
    non-test source file under `dash/src`, `dash/kyber` and `dash/dash/src` it never visits.
  - Test it against a small fixture tree with one reachable and one orphan module.
  - Add the `check:reachable` npm script and run it in the `ts-gates` CI job.
  - _Requirements: 2.3_
- [x] 2.2 Remove the deleted commands
  - Stream A.
  - First write a test asserting that the registered top-level commands of the CLI program
    are exactly `report`, `web`, `menubar`, `doctor`, `kyber`, `dash`, `otel` and
    `cursor-hook`.
  - Split `dash/src/main.ts` into `program.ts` (exports `buildProgram`) and a thin runner,
    so the test can build the command tree without running it.
  - Then remove every command in the Requirement 2.1 list, the `act`, `guard`, `sync` and
    `plugin` registrations and the run-time plugin loader, with the tests of those commands.
    `report` keeps its Ink implementation until task 6.2 replaces it.
  - _Requirements: 2.1, 2.2_
- [x] 2.3 Remove the Usage tab and Share chrome from the web dashboard
  - Stream A.
  - Update the `NAV_TABS` test first: the tabs are Context Doctor, Quarantine and Problems.
  - Delete the Usage and Share UI in `dash/dash/src`, and the usage, share and devices
    endpoints and prewarm in `dash/src/web-dashboard.ts`. Keep the loopback binding and the
    `Host`/`Origin` checks.
  - _Requirements: 2.9, 5.5_
- [x] 2.4 Salvage the tray files, then delete the native surfaces
  - Stream A.
  - `git mv` `dash/windows/src-tauri/src/{cli.rs,autostart.rs,tray_badge.rs}` into
    `dash/tray/src-tauri/src/`.
  - Extract `position_popover` and its helpers from `lib.rs` into
    `dash/tray/src-tauri/src/position.rs`. The files are not compiled until task 8.1.
  - Delete `dash/mac/`, `dash/windows/`, `dash/app/` and `dash/gnome/`, and the e2e specs and
    CI steps that referenced them.
  - _Requirements: 2.4, 6.11_
- [x] 2.5 Delete the upstream project files
  - Stream A.
  - Delete `BRIEF.md`, `SUBMISSION.md`, `RELEASING.md`, `CHANGELOG.md`, `CONTRIBUTING.md`,
    `SECURITY.md` and `codeburn-desktop-wireframes.html` under `dash/`, plus the marketing
    images and star history under `dash/assets/`. Keep `LICENSE` and
    `THIRD_PARTY_NOTICES.md`.
  - Replace `dash/README.md` with a short pointer to `docs/dash/`.
  - _Requirements: 2.5_
- [x] 2.6 Delete unreachable code and the credential, quota and currency paths
  - Stream A.
  - Run `check:reachable`, delete every module it lists together with that module's tests,
    and repeat until it is empty.
  - Delete `menubar-json.ts`, the status contract test and the MCP parity test along with
    `status` and `mcp`.
  - Add a test that no retained module imports the quota or subscription services or the
    currency service.
  - Add a test that a model absent from the pricing table yields cost status `no_rate`.
  - Confirm the synthesizer and refresh-registry tests still pass, which proves every
    consumed provider was kept.
  - _Requirements: 2.3, 2.6, 2.7, 2.8, 7.6_

- [ ] 3. Restructure into one tree
- [x] 3.1 Move to the target layout
  - Stream A.
  - Move `dash/kyber/**` and the retained `dash/src/**` into the layout in the design's
    target-layout table: `cli`, `providers`, `ingest`, `pricing`, `synth`, `canon`,
    `analysis`, `otel`, `refresh`, `server`, `install` and `branding` under `dash/src/`.
  - Move `dash/dash/` to `dash/web/`, and fold `dash/tests/` into colocated `*.test.ts`.
  - Update `tsconfig`, `vitest`, `eslint`, `tsup` and `tsup.sea` configs, the package
    scripts, the `ts-gates` and `build-kyberdash` CI paths, and the `AGENTS.md` gate commands.
  - Add a test that no directory or import path under `dash/` contains `kyber/`.
  - Fix any newly gated findings rather than baselining them.
  - _Requirements: 3.1, 3.2, 4.1, 4.4, 4.5_

- [ ] 4. Adopt the KyberDash identity
- [x] 4.1 Rename the package, variables, directories and strings
  - Stream A.
  - Write the tests first:
    - `package.json` has name `kyberdash`, a single bin `kyberdash`, and dpalfery metadata.
    - Setting `CODEBURN_CACHE_DIR` has no effect and `KYBERDASH_CACHE_DIR` does.
    - The cache resolves to `~/.kyberdash/cache`.
    - CLI `--help` output and the built `web/index.html` contain no `CodeBurn`.
  - Then rename every `CODEBURN_*` variable to `KYBERDASH_*` and `getCodeburnCacheDir` to
    `getCacheDir`, and update the user-visible strings.
  - _Requirements: 3.3, 3.4, 3.5, 3.7_
- [x] 4.2 Guard the severance and keep the attribution
  - Stream A.
  - Add `tests/KyberWeave.Tests/UpstreamSeveranceTests.cs`. It fails, naming the file, when
    any tracked file outside `docs/`, other than `dash/LICENSE` and
    `dash/THIRD_PARTY_NOTICES.md`, contains `getagentseal`, `agentseal.org` or
    `codeburn.app`.
  - Add a `build-kyberdash` step, and a test over the packaging script, that puts
    `THIRD_PARTY_NOTICES.md` in every `kyberdash` archive.
  - _Requirements: 1.5, 1.6_

- [ ] 5. Engine contracts
- [x] 5.1 Define the `ContextReport` contract
  - Stream B.
  - Create `dash/src/analysis/report/types.ts` with `ContextReport`, `Measured<T>`,
    `ReportScope` and `ReportSection`, exactly as in the design's data models.
  - Add a `formatMeasured` helper whose tests prove that null never renders as `0`.
  - _Requirements: 7.5, 11.10, 14.1_
- [x] 5.2 Move the six-dimension scorecard into the engine
  - Stream B.
  - Port the `ScorecardMatrix` derivation cases into `dash/src/analysis/scorecard.test.ts`
    first, then implement `scorecard.ts`.
  - Serve `scorecard` on the harness endpoints, and make `web` render the served value
    instead of deriving it.
  - Add a test that no scorecard output has a key combining dimensions.
  - _Requirements: 11.6, 11.14, 14.3_
- [x] 5.3 Refresh lock and run log
  - Stream B.
  - Extend `migration.test.ts` for schema 11 → 12 and the `refresh_run` table, then implement
    the migration.
  - Move `cache-refresh-lock.ts` to `src/refresh/lock.ts` with a directory parameter. Re-point
    its process tests at `~/.kyberdash/refresh.lock`, and add a test that a second concurrent
    `dash refresh` exits 3 without writing and prints the holding PID.
  - Make the orchestrator write and complete `refresh_run` rows, and add the hidden
    `--trigger` flag.
  - _Requirements: 10.3, 10.4, 10.5_
- [x] 5.4 Build the report
  - Stream B.
  - Write seeded-store tests for `buildContextReport` covering:
    - scope by harness, session, run and days
    - latest-session choice
    - latest-turn pressure, buckets, residual and cache-invalidation flag
    - findings in the window, ordered by rank score, with every contract field carried
      verbatim
    - separate dimensions
    - detection mapped to canonical harness ids
    - costs kept per basis and emitted last
  - Implement `build.ts`.
  - Add `npm run report:fixtures`, which regenerates `fixtures/*.json` from the seeded stores
    (full, empty, stale, unmeasurable pressure, no findings, mixed-basis cost), and commit
    the fixtures.
  - _Requirements: 8.2, 8.3, 8.4, 8.5, 8.9, 11.2, 11.3, 11.4, 11.5, 11.6, 11.7, 11.11, 11.15, 14.2, 14.4_
- [x] 5.5 Serve the report over REST
  - Stream B.
  - Write route tests first:
    - `GET /api/kyber/report` returns the builder's output for the query scope.
    - `sections` filtering works, and `detection` is excluded by default.
    - `GET /api/kyber/meta` carries `version` and `apiVersion`.
    - The existing header, 404 and 405 rules hold.
    - A non-loopback `Host` is still rejected.
  - Implement both routes in `src/server/routes.ts`.
  - _Requirements: 5.5, 6.7, 7.1, 7.5_
- [x] 5.6 Receiver health endpoint
  - Stream B.
  - Test that `GET /healthz` returns `{"service":"kyberdash-otlp","version":…}` while other
    `GET` paths still return 404 or 405.
  - Implement the route in `src/otel/receiver.ts`.
  - _Requirements: 10.6, 10.8_
- [x] 5.7 Listening line, view paths and `--view`
  - Stream B.
  - Create `dash/src/server/view-paths.json`, the one list of view path patterns shared by the
    CLI, the web router and the tray.
  - Test that `kyberdash web` prints the JSON `listening` line as its first stdout line, that
    `--view finding/<id>` opens `url/finding/<id>`, and that an unknown view exits 2 listing
    the valid forms.
  - _Requirements: 5.6, 5.7_

- [x] 6. CLI context report
- [x] 6.1 Text and Markdown renderers
  - Stream C.
  - Write snapshot tests over every fixture for `render-text.ts` and `render-markdown.ts`.
  - Add display-rule assertions:
    - a null value renders as `— (reason)`, never `0`
    - cost appears only after token figures
    - no composite score
    - recommendation text is verbatim
    - Markdown has no escape sequences
    - text is coloured only for a TTY with `NO_COLOR` unset
  - Implement the renderers.
  - _Requirements: 11.8, 11.9, 14.1, 14.2, 14.3, 14.4_
- [x] 6.2 Replace `report` with the context report
  - Stream C.
  - Write command tests first:
    - It is the default command.
    - `--format`, `--harness`, `--session`, `--run`, `--days` (positive integer, default 7),
      `--limit` (default 5) and `--db` behave as specified.
    - An invalid argument exits 2 before the store opens; an unreadable store exits 1;
      otherwise the exit is 0, including an empty store.
    - The store is opened read-only, with no ingest and no network.
    - Each finding ends with its `kyberdash web --view` command.
  - Implement `src/cli/report.ts`.
  - Delete the Ink dashboard, `context-tui` and the Ink and yoga dependencies, and confirm
    `check:reachable` and the SEA build still pass.
  - `tests/parse-workers.test.ts` and `tests/cli-provider-validation.test.ts` drive the parse
    path through `report --format json`, which stops parsing here. Move the parse-worker
    equivalence cases to `dash refresh` (the retained caller of `parseAllSessions`), and
    rewrite provider validation against `--harness`.
  - _Requirements: 11.1, 11.4, 11.5, 11.10, 11.11, 11.12, 11.13_
- [x] 6.3 Prove report and API parity
  - Stream C.
  - Add a test that runs `kyberdash report --format json` and `GET /api/kyber/report` against
    the same seeded store and scope, and asserts deep equality ignoring `generatedAt`.
  - _Requirements: 11.14_

- [ ] 7. Web deep links
- [x] 7.1 URL router
  - Stream D.
  - Write round-trip tests (spine location → path → location) for every pattern in
    `view-paths.json`, then implement `dash/web/src/lib/router.ts`.
  - _Requirements: 5.1_
- [x] 7.2 Wire history and direct opens
  - Stream D.
  - Test that `openSpine` pushes history, that `popstate` restores the stack, and that opening
    `/run/:id`, `/session/:id`, `/session/:id/turn/:n` or `/finding/:id` directly rebuilds
    the same ancestry navigation would.
  - Implement `resolveAncestry` and wire the router into `App`.
  - _Requirements: 5.2, 5.3_
- [x] 7.3 Not-found state
  - Stream D.
  - Test that a URL naming an id the API returns 404 for renders `NotFoundPanel` inside the
    shell, naming the id and linking to `/`. Then implement it.
  - _Requirements: 5.4_
- [x] 7.4 End-to-end deep links
  - Stream D.
  - Add Playwright specs under the existing config: open each route directly, reload, go
    back and forward, and hit an unknown id, all against a seeded store.
  - _Requirements: 5.1, 5.2, 5.3, 5.4_
- [x] 7.5 Extract the shared design tokens
  - Stream D.
  - Move the dashboard `@theme` tokens into `dash/web/src/tokens.css`, imported by the
    dashboard.
  - Add a test that the built CSS still defines every token the components reference.
  - _Requirements: 8.12_

- [ ] 8. Tray
- [x] 8.1 Scaffold the Tauri app and its CI
  - Stream E.
  - Create `dash/tray/` with a Tauri 2 crate in `src-tauri` and a React/Vite UI in `ui`.
  - `tauri.conf.json` sets identifier `io.github.dpalfery.kyberdash`, product name
    `KyberDash`, the darwin-arm64, darwin-x64 and win-x64 bundles, and a
    CSP of `default-src 'self'; connect-src ipc: http://ipc.localhost`. The capability
    file grants only the tray's own commands.
  - Compile the salvaged `cli.rs`, `autostart.rs`, `tray_badge.rs` and `position.rs`.
  - On macOS set the accessory activation policy and hide the Dock icon.
  - Register `tauri-plugin-single-instance`: a second launch shows the popover, and `--quit`
    quits the running instance.
  - Add a `cargo test` for argument handling.
  - Add CI jobs for `cargo fmt --check`, `cargo clippy -- -D warnings` and `cargo test` on
    macOS and Windows, plus the UI's typecheck, lint and test. Fix findings rather than
    baselining them.
  - _Requirements: 3.6, 4.1, 4.2, 4.4, 4.5, 6.1, 6.2, 6.3, 6.4, 6.5, 6.11_
- [x] 8.2 Resolve `kyberdash` and gate on its version
  - Stream E.
  - Test the resolution order: `KYBERDASH_BIN`, then `tray.json` `kyberdashPath`, then
    `~/.local/bin/kyberdash`, then `PATH`. Only validated absolute paths are spawned, with the
    salvaged size and time limits.
  - Test that a missing binary, or an `apiVersion` below the declared minimum, yields the
    `setup` phase with the probed paths and the remedy.
  - _Requirements: 6.6, 6.7_
- [x] 8.3 Supervise the web server
  - Stream E.
  - Using a fake process runner and a fake clock, test the following, then implement
    `supervisor.rs`:
    - It spawns `kyberdash web --no-open`.
    - It reads the listening line within 5 s and uses the reported URL.
    - Restart backoff runs 1, 2, 4 … 60 s.
    - After 5 consecutive failures it enters the stale phase.
    - Quit kills the process tree, and an in-flight refresh either finishes or leaves no
      partial write.
  - _Requirements: 6.9, 7.1, 7.2, 7.3_
- [x] 8.4 Loopback client and polling
  - Stream E.
  - Test, then implement `api.rs`:
    - Any host other than `127.0.0.1` is refused.
    - Polling runs every 15 s while the popover is open and every 60 s while it is closed.
    - The last good report is kept with its fetch time, and the view is marked stale on
      error.
  - _Requirements: 6.10, 7.4, 7.7_
- [x] 8.5 Scheduled refresh
  - Stream E.
  - Test, then implement `scheduler.rs`:
    - A refresh runs at start and every *cadence* (default 5 minutes).
    - Refresh now starts one unless one is already running.
    - Exit 3 maps to "running elsewhere".
    - A failure keeps the last success time and records the failure.
    - A refresh started from a terminal (visible in the report's `coverage.refresh`) blocks
      a new one.
  - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5_
- [x] 8.6 Receiver status and optional hosting
  - Stream E.
  - Test that probing `127.0.0.1:4318/healthz` yields `reachable`, `not-reachable`,
    `port-held-by-other` or `unknown`.
  - Test that hosting (off by default) starts `kyberdash otel` with backoff, and is not
    attempted, or retried, when the port is held by another process.
  - _Requirements: 10.6, 10.7, 10.8_
- [x] 8.7 Status item
  - Stream E.
  - Test a pure function from report, settings and phase to status-item state:
    - the whole-number percentage
    - neutral, attention (≥ 70%), critical (≥ 90%) and stale states
    - no number when pressure is unmeasurable or there is no session
    - never cost
    - tooltip text
  - Render the state as title text on macOS and as a `tray_badge.rs` badge on Windows.
  - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6_
- [x] 8.8 Settings and launch at login
  - Stream E.
  - Test the settings JSON round trip and defaults: harness `all`, window 7 days, refresh
    every 5 minutes, thresholds 0.70 and 0.90, launch at login off, receiver hosting off.
  - Test the Windows `Run` key through the salvaged `autostart.rs`, and a new macOS
    LaunchAgent writer for `~/Library/LaunchAgents/io.github.dpalfery.kyberdash.plist`,
    against a temporary directory.
  - _Requirements: 6.8, 8.8, 9.2_
- [x] 8.9 IPC surface and opening views
  - Stream E.
  - Test that `get_view_state` and the `view-state-changed` event carry the design's
    `ViewState`.
  - Test that `open_view` accepts only paths matching `view-paths.json` (embedded with
    `include_str!`) and opens server URL + path. Everything else is refused.
  - Test that `refresh_now`, `set_settings` and `quit` behave as specified.
  - _Requirements: 6.10, 8.6, 8.7_
- [x] 8.10 Popover UI
  - Stream E.
  - Write Testing Library tests over every report fixture and every `ViewState` phase:
    - order: harness selector, session panel, findings (at most 3), health footer, actions
    - `—` with the reason for unmeasurable figures, never `0`
    - the cache-invalidation notice
    - cost as one secondary line after the token figures
    - no quota, plan, currency, budget or score elements
    - the empty state with Refresh now
    - the setup and stale states
  - Implement `SessionPanel`, `FindingsList`, `HarnessSelector`, `HealthFooter`, `Actions`,
    `SetupState`, `EmptyState`, `StaleBanner` and `SettingsView`, importing
    `dash/web/src/tokens.css` and the report types with type-only imports.
  - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5, 8.8, 8.9, 8.10, 8.11, 8.12, 14.1, 14.2, 14.3, 14.4_

- [ ] 9. Distribution
- [x] 9.1 Tray installer in `kyberdash menubar`
  - Stream F.
  - With a local HTTP origin (`KYBER_WEAVE_RELEASE_ORIGIN`) and an injected verifier, test:
    - origin resolution for the running version
    - checksum refusal
    - on macOS, refusal on a codesign, team-id (`J2UNNQ466J`) or `spctl` failure, and no
      quarantine stripping
    - on Windows, the NSIS `/S` invocation
    - backup and restore on any failure, with a non-zero exit naming the step
    - `~/.kyberdash/tray.json` including `kyberdashPath`
    - `--update` as a no-op without an install
    - quit via `--quit`, then replace and relaunch
    - the Linux refusal message
  - Implement `dash/src/install/`, replacing `menubar-installer.ts`.
  - _Requirements: 12.5, 12.6, 12.8, 12.10, 12.11, 15.5, 15.6_
- [ ] 9.2 `install.sh --with-menubar`
  - Stream F.
  - Extend `ReleaseTests.cs`: the script invokes `"${INSTALL_DIR}/kyberdash" menubar --force`,
    and dies with a message when `--no-kyberdash` is also set. Then change the script.
  - _Requirements: 12.7_
- [ ] 9.3 Self-updater delegates to `kyberdash`
  - Stream F.
  - With the existing fake host, write xunit tests for:
    - the `--no-menubar` option
    - `ShouldUpdateTray` skip reasons (flag, `--no-kyberdash`, release older than
      `TrayMinVersion`, no `tray.json`), each logged
    - delegation to `kyberdash menubar --update` after the KyberDash update
    - a non-zero exit becoming a `SelfUpdateException` that names the tray step
  - Implement, run `./scripts/update-loop.sh`, and record the loop's inability to build the
    tray in the `kyberdash-local-release-loop` todo.
  - _Requirements: 15.1, 15.2, 15.3, 15.4, 15.6, 15.7_
- [ ] 9.4 `build-tray` release job
  - Stream F. Needs the secrets from the
    [signing todo](../../todo/macos-developer-id-signing.md).
  - Add the matrix job under `environment: release`:
    - A secrets-presence step fails the macOS legs first.
    - Import into a temporary keychain, write the `.p8`, run `tauri build --target …`.
    - Assert `spctl` acceptance, `stapler validate` and `TeamIdentifier == J2UNNQ466J`.
    - Zip with `ditto` on macOS; produce the NSIS installer on Windows.
    - Add the SHA-256 lines to `SHA256SUMS.txt`, and add the Windows SmartScreen line to the
      release notes.
  - Pin every action to a commit SHA verified against its repository.
  - Extend `ReleaseTests.cs` to assert the job's asset names, the presence check and the SHA
    pins.
  - _Requirements: 12.1, 12.2, 12.3, 12.4, 12.9_

- [ ] 10. Documentation and decisions
- [ ] 10.1 ADR 0021, the KyberDash docs and the deferred-work todos
  - Stream G, after streams B to F merge.
  - Write ADR 0021. It covers the single report model, Rust-side HTTP, the refresh lock with
    exit 3, and the tray's ownership of the server, refresh and receiver. It supersedes only
    ADR 0016's refresh-button clause.
  - Rewrite `docs/dash/README.md`, `architecture.md` and `runbook.md` for the three surfaces,
    and update the KyberDash row in `docs/catalog.md`.
  - Add the Linux-tray and Windows-code-signing todos.
  - `docs validate` and `docs drift` report zero findings.
  - _Requirements: 10.9, 13.1, 13.2, 13.3, 13.4_

- [ ] 11. Specification closeout
  - Assign to `docs-dev`. Verify every requirement against implementation evidence,
    migrate the specification's durable content into canonical documentation, update
    the specification index, then archive `kyberdash-context-surfaces/`.
  - _Requirements: all_
