---
id: plans/2026-10-11-harness-otel-settings-drawer
title: "Harness OpenTelemetry collection switches in a web dashboard Settings drawer"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-11
development-mode: test-first
---

# Harness OpenTelemetry collection switches in a web dashboard Settings drawer

**Status: Ready 2026-10-11.** The user chose "Approve and execute" on 2026-10-11. Decisions D1–D6 are user decisions; D7–D20 were settled by the architect from cited evidence and are approved by that same approval. No decision is open.

**Development mode:** `test-first`. Every implementation task has a Test-contract row in [section 7](#7-test-contract-test-first); RED evidence precedes GREEN. The default was persisted because the user did not opt out.

## 1. Problem and goal

The user asked: a Settings button in the same menu as status and maintenance, with a right slide-out that turns on OpenTelemetry collection per harness. Selecting a button or checkmark next to an installed harness runs the configuration that turns OTel data on for that harness.

The configuration engine already exists: `kyberdash kyber capture status|enable|disable` ([ADR 0031](../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md)). What is missing is a way to reach it from the web dashboard without breaking the display-layer rule, a structured status the UI can render, Windows support, and the receiver coupling. The goal is a Settings drawer in `dash/web` that lists the harnesses, lets the user switch collection on and off for the three writable ones after a dry-run preview, shows drift refusals, and keeps the OTLP receiver in step.

## 2. Investigation findings

Facts below were read from the tree at `main` HEAD `2794cc8` unless marked self-gathered (external lookups performed by the architect because no delegation tool was available).

1. **Surface.** Neither surface has a menu with both Status and Maintenance entries. The web header (`dash/web/src/App.tsx`, header block, `NAV_TABS` at 173–177) holds the logo, three tabs (Context Doctor, Quarantine, Problems) and a right-hand cluster with only `ThemeToggle` (432–434). Maintenance is a panel inside Context Doctor (`dash/web/src/pages/ContextDoctor.tsx:939`, `dash/web/src/components/maintenance/MaintenancePanel.tsx`) and ingest status is `CoverageIngestPanel` on the same page. The tray popover has Settings already (`dash/tray/ui/src/components/Actions.tsx`) as a full-view swap. `docs/dash/runbook.md` (line 593) says four header tabs including Usage; the code and `App.test.tsx` say three (documentation drift).
2. **Slide-out pattern.** `dash/web/src/components/SessionInspectorDrawer.tsx` (1044–1157) is a right slide-in with backdrop, `role="dialog"`, `aria-modal`, Escape and close button, and accepts `children`. It has no focus management. Its tests are `SessionInspectorDrawer.test.tsx` and `AgentSessionDashboard.test.tsx`.
3. **Capture engine** (`dash/src/capture/`): registry of six harnesses in `harnesses/registry.ts` (110–117). Managed (writable): `claude-code` (`~/.claude/settings.json`, `env.*` keys, endpoint and several flags ensure-if-absent), `copilot` (VS Code Stable and Insiders user `settings.json`, five `github.copilot.chat.otel.*` keys), `opencode` (`~/.config/opencode/opencode.jsonc`, key `experimental.openTelemetry`; the endpoint is an environment variable OpenCode reads and is never written). Pending (status-only; enable and disable write nothing by earlier decision): `codex`, `pi`, `antigravity`. `runCapture` (`index.ts:207–266`) returns text only. It refuses Windows (`index.ts:218–223`), and `capture.test.ts:324` pins that refusal.
4. **No installed-harness concept.** Status reports only config file found or missing. `dash/src/cli/doctor.ts` probes provider session sources and is not keyed by capture ids.
5. **Phantom writes.** `enableFile` creates a missing config file and its directories (`writeText` uses `mkdirSync` recursive), so `enable` for Copilot would create a VS Code Insiders profile directory on a machine without Insiders.
6. **Display-layer rule** (root `AGENTS.md`, `dash/AGENTS.md`, [rule](../rules/kyberdash-display-layer.md), [ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md), enforced by `dash/src/architecture/display-layer.test.ts`): web and tray may not spawn CLI children, schedule, add timers beyond the allowlist, or take engine value imports. The UI must reach the engine over `/api/kyber/*`. A UI-spawned script is forbidden.
7. **Route plumbing.** `dash/src/server/routes.ts`: `/api/kyber/settings` (1471–1501) is the template (`requireJsonContentType`, `serveJsonObjectBody` with 64KB cap and `runGuardedBodyHandler`, fixed error strings, unmounted without `deps.store`). `KyberRouteDeps` (442–452). `dash/src/server/testing.ts` provides `callRoute` and `makeDeps`.
8. **Loopback guard exists** (`dash/src/cli/web.ts:739–756`): any request whose Host is not loopback or whose Origin is not loopback is answered 403 before routing. `dash/src/cli/web.test.ts` (237–293) covers cross-origin PUT and POST for settings, refresh, import-history and clean. It does not cover a rebinding Host header, and `routes-hardening.test.ts` covers body handling only. A request with no Origin header passes (the tray and curl), which is the existing trust model for every mutating route.
9. **Receiver.** `ReceiverHost` (`dash/src/jobs/receiver-host.ts`) starts a `kyberdash otel` child when `settings.receiver.hosted` is on (default off) and polls every 10s (`web.ts:233`). `probeReceiver` (`capture/probe.ts`) classifies the listener as kyberdash, foreign or none, with a 5s default timeout. `docs/dash/runbook.md` §2d states that pausing must not silently stop collection.
10. **CI.** `ts-gates` in `.github/workflows/ci.yml` (485–558) runs dash typecheck, test, lint and `check:reachable` on ubuntu only. There is no Windows or macOS runner for dash TypeScript tests. `tray-rust` is the only Windows job.
11. **Windows paths** (self-gathered, sources cited). Claude Code user settings are `%USERPROFILE%\.claude\settings.json` (code.claude.com/docs/en/settings). VS Code user settings are `%APPDATA%\Code\User\settings.json` (code.visualstudio.com/docs/configure/settings); the Insiders edition uses `Code - Insiders` by the same convention the existing writer already assumes. OpenCode global config is `path.join(xdgConfig, "opencode")` from `xdg-basedir` (`packages/opencode/src/global/index.ts` and `packages/core/src/global.ts` in the OpenCode repository), and `xdg-basedir` falls back to `<homedir>/.config` on every OS, so on Windows the file is `%USERPROFILE%\.config\opencode\opencode.jsonc`. A DeepWiki page claiming `%LOCALAPPDATA%\opencode` conflicts with the source and was rejected. OpenCode also honors `XDG_CONFIG_HOME` on every OS; the existing writer deliberately ignores XDG for temp-HOME isolation, so a user with `XDG_CONFIG_HOME` set would be written a file OpenCode does not read (known limitation, see risks).
12. **Documentation gap.** `docs/dash/runbook.md` has no capture section at all, and `docs/dash/architecture.md` has no capture or `/api/kyber/capture` content.
13. **Tooling.** `.codegraph/` did not exist; `codegraph init .` (authorized by root `AGENTS.md`) was run on a clean tree and introduced no tracked-file edits. The Kyber-Weave MCP `docs_explore` tool was not connected, so documentation discovery started from the documentation index and targeted search.

## 3. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which surface and where does the Settings button go? | The web dashboard (`dash/web`). A Settings button opens a right slide-out drawer reusing the SessionInspectorDrawer pattern. The tray is out of scope. | See D8 for the exact placement. Tray follow-up is possible later because the status and action API (D10) is surface-neutral; no todo is created. | User decision 2026-10-11 (conductor relay). |
| D2 | How does the UI run the configuration? | No external batch script on any OS. The UI calls a new engine route under `/api/kyber/*` that calls the existing capture core in-process. | Required by ADR 0033 and `display-layer.test.ts`. The user also said they do not care whether macOS uses a script or the engine. | User decision 2026-10-11, clarified 2026-10-11. |
| D3 | Toggle semantics | Each installed-harness checkbox turns collection on and off (capture enable and disable). A dry-run preview is shown before any write. Drift refusals on disable are shown visibly. | The preview is a separate dry-run request; the write recomputes (see risks, preview is advisory). | User decision 2026-10-11. |
| D4 | Which harnesses are togglable and what is installed? | Toggles for `claude-code`, `copilot`, `opencode` only. `codex`, `pi`, `antigravity` render read-only with their existing status reasons. No new writers. A structured status API with a concrete installed definition (D9) is added. Unknown renders "unknown". | Honors [honest unobservability](../rules/honest-unobservability.md). Codex and Antigravity stay status-only under the earlier G1 decisions. | User decision 2026-10-11. |
| D5 | Receiver coupling | Turning a harness on enables `settings.receiver.hosted` when it is off. The drawer shows the receiver state and a toggle (existing `PUT /api/kyber/settings`). The OpenCode `OTEL_EXPORTER_OTLP_ENDPOINT` variable stays a surfaced manual step. The last-off behavior is D7. | The preview names the receiver change before it happens. | User decision 2026-10-11. |
| D6 | Windows | Required. Windows paths are handled in-process in TypeScript for the three writers. The `win32` unsupported early exit is removed. No `.bat`, `.cmd` or `.ps1`. An ADR 0031 amendment is planned (T11). Tests for Windows path resolution run on any host. | See D14 for the paths and D18 for the inverted test. | User decision 2026-10-11. |
| D7 | What happens to the receiver when the last managed harness is turned off? | Nothing. The receiver setting is left as it is. The drawer states, after the last harness is turned off, that the receiver is still hosted and can be switched off in the receiver section. | Settled from existing rules: runbook §2d ("pausing must not silently stop collection"); ADR 0031 decision 4 and ADR 0033 make `receiver.hosted` an independent shared setting; other exporters (Codex, Pi, Antigravity bridge, Copilot CLI and OpenCode via environment) may still post to the port, so silently stopping it would stop collection the user did not ask to stop. | ANSWERED: settled by the architect from ADR 0031/0033 and runbook §2d; approved by the user 2026-10-11 (approve and execute). |
| D8 | Where exactly is the Settings button? | In the web header's right-hand cluster, immediately left of `ThemeToggle` (`dash/web/src/App.tsx`, the `ml-auto` div at 432–434), `data-testid="header-settings-button"`, label "Settings", `aria-haspopup="dialog"` and `aria-expanded`. Drawer open state is display state in `App`. | The repository has no literal Status or Maintenance menu entries (finding 1). The header is the only global control area and shows on every page, unlike the Maintenance panel (Context Doctor only). The Status and Maintenance panels are page sections, so no menu entries for them are added. Flagged for the approval gate because it interprets the request against repository reality. | ANSWERED: settled by the architect from findings 1 and 2; approved by the user 2026-10-11 (approve and execute). |
| D9 | What does "installed" mean? | Evidence-based, in-process, no PATH lookup. `claude-code`: `<home>/.claude` exists or `<home>/.local/bin/claude` (`claude.exe` on Windows) exists. `copilot`: the VS Code user directory (`.../Code/User`) or the Insiders one exists, evaluated per edition. `opencode`: `<home>/.config/opencode` exists or `<home>/.opencode/bin/opencode` (`.exe` on Windows) exists. States: `detected`, `not-detected` (every probe definitively absent), `unknown` (a probe failed with a permission-class error and nothing was detected). | The server often runs under launchd or a service with a reduced PATH, so PATH would report false negatives. Doctor was not reused: it enumerates provider session sources and parses samples. A harness that already has a KyberDash receipt stays switchable off even when not detected. | ANSWERED: settled by the architect from findings 4 and 6; approved by the user 2026-10-11 (approve and execute). |
| D10 | API shape | One resource, `/api/kyber/capture`. `GET` returns the structured status. `POST` takes `{action: "enable" or "disable", harness, dryRun}` with all three keys required and no others. Single harness per request, managed harnesses only. Contract in [section 5](#5-api-contract). | Mirrors the `/api/kyber/settings` pattern. `dryRun` is required so a missing field can never write. | ANSWERED: settled by the architect from finding 7; approved by the user 2026-10-11 (approve and execute). |
| D11 | How is structure added without disturbing the CLI? | `runCapture` gains an additive `events` array built beside the existing text lines. CLI text output is byte-identical and the existing capture suites stay unmodified (except D18). Status structure is built from the same primitives as the text status (`declaredFiles`, `presentKeys`, receipt drift) with a parity test. | Avoids a second derivation that can drift from the CLI and a stdout parser. | ANSWERED: settled by the architect from finding 3; approved by the user 2026-10-11 (approve and execute). |
| D12 | Concurrency | Capture writes are serialized in the server by an async mutex, and the UI allows one in-flight request. | Prevents a read-modify-write race on config files and the receipt from a double click or two tabs. A concurrent CLI run is not covered (risk). | ANSWERED: settled by the architect; approved by the user 2026-10-11 (approve and execute). |
| D13 | Does the API return process environment values? | No. The CLI status keeps printing them. The API returns manual steps as text plus the variable name and the value to set, never the server process's current value. | The server's environment is not the user's shell environment, so showing it would claim something the server cannot observe. | ANSWERED: settled by the architect from the honest-unobservability rule; approved by the user 2026-10-11 (approve and execute). |
| D14 | Windows path rules | Claude: `<home>\.claude\settings.json`. VS Code: `<appData>\Code\User\settings.json` and `<appData>\Code - Insiders\User\settings.json`, where `appData` is the injected value, else `APPDATA`, else `<home>\AppData\Roaming`. OpenCode: `<home>\.config\opencode\opencode.jsonc`. XDG variables stay ignored on every OS. Path joining uses `path.win32` for the win32 platform so resolution is testable on any host. | Findings 11. A real Windows run is an owner gate (section 10). | ANSWERED: settled by the architect from finding 11; approved by the user 2026-10-11 (approve and execute). |
| D15 | How is the SessionInspectorDrawer pattern reused? | Extract a generic `SlideOutDrawer` shell and make `SessionInspectorDrawer` a thin wrapper that keeps every existing test id and class; focus management is opt-in (`manageFocus`, off for the session drawer). | Keeps the existing drawer suites unmodified as the characterization guard while giving the settings dialog focus handling it needs. | ANSWERED: settled by the architect from finding 2; approved by the user 2026-10-11 (approve and execute). |
| D16 | Windows evidence in CI | Add a `windows-latest` job that runs only the capture-related dash tests. | D6 makes Windows required and CI has no dash TypeScript runner for it (finding 10). If `npm ci` fails on Windows for an unrelated native dependency, the fallback is the owner gate, recorded at closeout. | ANSWERED: settled by the architect from finding 10; approved by the user 2026-10-11 (approve and execute). |
| D17 | ADR 0031 amendment form | Amend ADR 0031 in place with a dated paragraph and update its row in `docs/adr/README.md`. No new ADR. | Precedent: ADR 0031, 0032 and 0016 were amended in place for issue #319. The amendment records: UI-triggered runs of the same engine, Windows support, the route-level receiver coupling, and applicability filtering. | ANSWERED: settled by the architect from the ADR index precedent; approved by the user 2026-10-11 (approve and execute). |
| D18 | The test that pins "Windows unsupported" | `capture.test.ts:324` ("reports Windows as unsupported") is replaced by its inverse. This is the single authorized change to an existing capture assertion. | It follows directly from D6; it is not a weakened assertion. | ANSWERED: settled from D6; approved by the user 2026-10-11 (approve and execute). |
| D19 | Which files does the route write? | Only config files that exist or whose host is detected. Copilot Insiders is skipped when Insiders is not installed. The CLI keeps writing every declared file. | Finding 5: avoids creating phantom VS Code Insiders profile directories. | ANSWERED: settled by the architect from finding 5; approved by the user 2026-10-11 (approve and execute). |
| D20 | Does the CLI also couple the receiver? | No. `capture enable` keeps printing the receiver remedy. Coupling is a property of the route, which has the store. | The CLI runs without the store; no behavior change for terminal users. | ANSWERED: settled by the architect; approved by the user 2026-10-11 (approve and execute). |

## 4. Design

Engine (`dash/src`):

- `capture/paths.ts` (new): a path environment `{platform, appData}` and per-platform resolvers; writers' `resolvePath(home, env?)` gain an optional second argument so existing calls stay valid.
- `capture/detect.ts` (new): the D9 install probes with an injectable existence probe.
- `capture/status.ts` (new): the structured status report from the same primitives as text status.
- `capture/lock.ts` (new): the async mutex (D12).
- `capture/index.ts`: `events`, a `fileFilter` for enable (D19), a probe timeout option, and removal of the win32 refusal.
- `capture/actions.ts` (new): structured action wrapper that returns outcome, events and drift.
- `server/capture-routes.ts` (new): pure route logic (body parse, receiver coupling, response shaping). `server/routes.ts` gets a short mount block that uses the existing body helpers, and `cli/web.ts` passes capture options through `KyberRouteDeps`.

Web (`dash/web/src`):

- `components/ui/SlideOutDrawer.tsx` (new shell, D15), `components/settings/SettingsDrawer.tsx` and `components/settings/HarnessCollectionList.tsx` (new), `lib/kyberApi.ts` (client), `App.tsx` (header button and open state).
- Query key `['kyber-capture']`. No timers and no polling: status is fetched on open and after each mutation, and a manual "Refresh status" button covers the roughly 10 second receiver start-up, so `TIMER_ALLOWLIST` is untouched.

Receiver coupling in the route: a non-dry enable that ends in `wrote` or `noop` writes `receiverHosted = on` when it is off, after the config write succeeds. If that setting write throws, the response is 200 with `receiver.enableFailed: true` (the config is already written, so a 500 would misreport state). Disable never touches the receiver.

## 5. API contract

`GET /api/kyber/capture` answers 200:

```text
{ platform: "darwin" | "linux" | "win32",
  receiver: { hosted: boolean, liveness: "kyberdash" | "foreign" | "none" | "unknown", detail: string },
  harnesses: [{
    id, displayName,
    mode: "managed" | "status-only",
    installed: "detected" | "not-detected" | "unknown",
    installEvidence: string[],
    state: "on" | "off" | "partial" | "unknown" | "not-applicable",
    receipt: "recorded" | "absent",
    files: [{ id, path, applicable: boolean, exists: boolean | null,
              expectedKeys: string[], presentKeys: string[], missingKeys: string[],
              drift: [{ key, written, current }] | null }],
    warnings: [{ code: string, text: string }],
    manualSteps: [{ id, text, env?: { name, value } }],
    statusLines: string[] } ] }
```

`state` is computed over applicable files only (D19): `on` when every applicable file holds every always-written key at its desired value and every ensure-if-absent key at any value; `off` when none of the keys are present; `partial` otherwise; `unknown` when any applicable file is unreadable or unparseable; `not-applicable` for status-only harnesses. `warnings` includes `endpoint-differs` when an existing ensure-if-absent endpoint does not point at the receiver, so a state of `on` never silently means "data goes elsewhere". A `state` of `on` with `receipt: "absent"` means the keys were set outside KyberDash and there is nothing to restore.

`POST /api/kyber/capture` takes exactly `{ action: "enable" | "disable", harness: string, dryRun: boolean }`.

- 200 `{ action, harness, dryRun, outcome: "previewed" | "wrote" | "noop", events, harnessState, receiver: { hosted, willEnable, enabled, enableFailed, lastManagedHarnessDisabled } }`.
- 409 `{ error, outcome: "refused", reason: "drift" | "conflict" | "unparseable" | "unreadable" | "busy", events }`.
- 400 invalid payload, unknown harness, unknown action, missing or non-boolean `dryRun`, extra keys, or a status-only harness. 404 unmounted. 405 other methods. 413 over 64KB. 415 non-JSON. 500 fixed `{ error: "Capture failed" }`.
- An event is `{ kind: "would-write" | "wrote" | "noop" | "would-restore" | "restored" | "refused" | "error", harness, fileId, path, changes?: [{ key, before?, after? }], reason?, drift?, code? }`. A host error is reduced to its errno `code`; no raw message crosses the wire. Responses contain only the keys KyberDash manages. They never include any other key's value from the user's config.

## 6. Constraints that bind every task

- The display-layer rule and `dash/src/architecture/display-layer.test.ts` stay green. No `setInterval`, no re-arming timer, no mutation from a timer, no value import from `dash/src` in `dash/web/src` outside the existing allowlist (`import type` is free).
- Agents never hand-edit a user's harness config; every test runs against a temporary home (and temporary application-data directory) and never the real home. Tests whose platform is `win32` on a non-Windows host are limited to path resolution and read-only status, because a Windows-style path is not a usable filesystem path there. Write tests run on the host's own platform.
- New exports must be reachable for `npm --prefix dash run check:reachable`; new test-support files must be imported by a test.
- Editing anything under `docs/` means re-running `docs validate` and `docs drift`.

## 7. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | No-test task: contract fixtures `dash/src/server/fixtures/capture-api/*.json` | Verified by T5 and T8 consuming them | Fixtures encode the section 5 shapes for: mixed status, unknown status, preview, wrote, refused-drift, disable-last | N/A; T5 RED and T8 RED fail on behavior (route 404, missing component), not on a missing fixture | T5 and T8 pass using the same files unmodified |
| T1 | `dash/src/capture/paths.test.ts` (new); `dash/src/capture/capture.test.ts:324` inverted (D18) | `cd dash && npx vitest run src/capture/paths.test.ts src/capture/capture.test.ts` | win32 resolution returns the exact D14 strings (via `path.win32`) for Claude, both VS Code editions and OpenCode, with `appData` injected and with the fallback; darwin and linux strings are unchanged; `runCapture('status', {platform: 'win32'})` exits 0 | New file fails because `paths.ts` is missing and the inverted test fails with exit 1 and "unsupported" | Both pass; every pre-existing capture suite passes unmodified |
| T2 | `dash/src/capture/windows-files.test.ts` (new); `dash/src/capture/errno.test.ts` (new) | `cd dash && npx vitest run src/capture/windows-files.test.ts src/capture/errno.test.ts` | JSONC with a UTF-8 BOM and CRLF line endings: enable preserves the BOM, writes only CRLF, and disable restores the prior bytes; errno codes EACCES, EPERM, EBUSY and ENOENT map to stable `code` and `reason` values | BOM file is refused as unparseable or gets mixed LF; mapper module absent | Both pass; existing `edit.test.ts` and `writer-seam*.test.ts` pass unmodified |
| T3 | `dash/src/capture/detect.test.ts`, `status.test.ts`, `lock.test.ts` (new) | `cd dash && npx vitest run src/capture/detect.test.ts src/capture/status.test.ts src/capture/lock.test.ts` | Install states per D9 with an injected probe; status over a temporary home covers: nothing installed, all keys on with receipt, drifted key, keys on without receipt, Copilot Stable only (no `partial`), unreadable file gives `unknown`, endpoint mismatch warning, status-only lines, and parity with `runCapture('status')` on enabled and missing keys; a config containing `env.ANTHROPIC_API_KEY` never has that value in the report; the mutex serializes and releases on throw | Modules absent | All pass; CLI text status tests unmodified and green |
| T4 | `dash/src/capture/actions.test.ts` (new) | `cd dash && npx vitest run src/capture/actions.test.ts src/capture` | Dry-run enable emits `would-write` with before and after and writes nothing; enable then enable again gives `wrote` then `noop`; dry-run disable gives `would-restore`; disable restores key-level; drift refuses with entries and leaves bytes untouched; unparseable and unreadable refuse with a code only; `fileFilter` skips Insiders and creates no phantom directory; two concurrent enables leave a valid receipt; a never-resolving health fetch returns within the probe timeout | `actions.ts` and `events` absent | All pass; full `src/capture` suite green with text output byte-identical |
| T5 | `dash/src/server/capture-route.test.ts` (new); additions to `dash/src/cli/web.test.ts` | `cd dash && npx vitest run src/server/capture-route.test.ts src/cli/web.test.ts src/server/routes-hardening.test.ts src/architecture/display-layer.test.ts` | GET and POST behave per section 5, including: every 400/405/413/415 case, a dry run writes nothing, a real enable writes the config and turns the receiver setting on, a failing setting write after the config write returns 200 with `enableFailed`, disabling the last harness leaves the receiver on, drift gives 409 with entries and untouched bytes, a thrown failure returns the fixed 500 with no path text, no response contains a secret from the config, concurrent POSTs serialize, response key sets match the T0 fixtures; cross-origin POST and a rebinding Host header on the capture route are 403 with nothing written, and the Host case is also asserted for settings PUT, refresh, import-history and clean | Route answers 404 (not mounted); the Host-header cases fail or are absent | All pass; hardening and display-layer suites unmodified and green |
| T6 | `dash/web/src/lib/kyberApi.capture.test.ts` (new) | `cd dash && npx vitest run web/src/lib/kyberApi.capture.test.ts` | Client parses the T0 fixtures; unknown fields are dropped; an out-of-vocabulary `state` becomes `unknown`; a 409 returns a bounded refusal object while other non-2xx throw `KyberApiError`; strings are capped and arrays bounded; the request body is exactly the three keys with a JSON content type | Functions absent | Passes; `npm --prefix dash run typecheck` clean |
| T7 | `dash/web/src/components/ui/SlideOutDrawer.test.tsx` (new); existing `SessionInspectorDrawer.test.tsx` and `AgentSessionDashboard.test.tsx` as characterization | `cd dash && npx vitest run web/src/components/ui/SlideOutDrawer.test.tsx web/src/components/SessionInspectorDrawer.test.tsx web/src/components/AgentSessionDashboard.test.tsx` | Closed renders nothing; open renders `role="dialog"`, `aria-modal`, label; Escape, backdrop click and the close button close it and a panel click does not; with `manageFocus` focus moves in on open and returns to the opener on close; test-id prefix is honored | New suite fails (module absent) | New suite passes and the two existing suites pass with zero edits |
| T8 | `dash/web/src/components/settings/SettingsDrawer.test.tsx` (new); `dash/web/src/App.test.tsx` additions | `cd dash && npx vitest run web/src/components/settings/SettingsDrawer.test.tsx web/src/App.test.tsx web/src/pages/Maintenance.test.tsx src/architecture/display-layer.test.ts` | Header button is present on all five pages with `aria-haspopup` and `aria-expanded`, and the three-tab assertions still hold; the drawer fetches status once on open; managed rows have a checkbox whose state comes only from the server; `partial` is indeterminate; `unknown`, not-detected and on-without-receipt are disabled with their reason; status-only rows have no checkbox and show their lines; clicking sends a dry-run POST and shows the preview (files, keys, receiver note); Cancel sends nothing more; Confirm sends the write then refetches; a 409 shows the drift as an alert and leaves the checkbox unchanged; in-flight blocks a second click; the receiver section toggles `receiverHosted` and states the last-off behavior; the OpenCode manual step names the variable without claiming a value; Escape closes and focus returns | Component and header button absent | All pass; display-layer guard green with no allowlist change |
| T9 | No-test task: CI workflow change | `cd dash && npx vitest run src/capture src/server/capture-route.test.ts` locally; the job on the PR | The new job runs only those tests on `windows-latest` and is in `ci-summary` needs | N/A | Local command passes; workflow parses; first PR run on Windows is green, or the owner gate in section 10 is recorded as the fallback |
| T10 | No-test task: read-only security review | `security-review` skill over the T5 diff | A ranked report; any high-confidence finding is fixed under T5's RED/GREEN | N/A | Report has no unresolved high-confidence finding |
| T11 | No-test task: ADR amendment | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` | ADR 0031 and its index row carry the amendment | N/A | Validation and drift are zero findings |
| T12 | No-test task: documentation | Same docs commands | Runbook, architecture and telemetry-inventory describe the final behavior | N/A | Validation and drift are zero findings |
| T13 | No-test task: whole-change review | `code-review` skill, then `review gates` | Council verdict | N/A | Verdict is approve, or findings are fixed and re-checked |
| T14 | No-test task: closeout | Docs commands with `--merge-ready` after archiving | Plan archived, index updated, closeout mapping written | N/A | Zero findings under `--merge-ready` |

Changing an approved row, including weakening an assertion to reach green, is a scope change that returns this plan to Draft.

## 8. Tasks

**T0 — API contract fixtures.** Skill: `test-dev`. Objective: author the shared JSON fixtures that pin section 5 so the engine and the web tests cannot drift. Files: `dash/src/server/fixtures/capture-api/status-mixed.json`, `status-unknown.json`, `post-preview-enable.json`, `post-wrote-enable.json`, `post-refused-drift.json`, `post-disable-last.json`. Acceptance: each file satisfies section 5 and includes at least one status-only harness, one `partial`, one `unknown`, and one `endpoint-differs` warning. Depends on: none.

**T1 — Windows path seam.** Skills: TypeScript engineering (no dedicated role; the conductor assigns the closest implementer), `test-dev` for RED. Objective: Windows path resolution per D14 and removal of the unsupported refusal. Files: `dash/src/capture/paths.ts` (new), `dash/src/capture/harnesses/registry.ts`, `claude-code.ts`, `copilot.ts`, `opencode.ts`, `codex.ts`, `pi.ts`, `antigravity.ts`, `dash/src/capture/index.ts` (options and refusal only; add `appDataDir` and wire a path environment to `resolvePath`), `dash/src/capture/capture.test.ts` (line 324 only), `dash/src/capture/paths.test.ts` (new). Acceptance: the D14 strings, optional second `resolvePath` argument with all current callers valid, `Library`/`darwin` branch in `copilot.ts` moved behind the injected platform, status exits 0 on `win32`. Depends on: none.

**T2 — Windows file-format robustness.** Skills: TypeScript engineering, `test-dev`. Objective: preserve BOM and CRLF in JSON and JSONC edits and add the errno classifier T4 consumes. Files: `dash/src/capture/edit-json.ts`, `dash/src/capture/errno.ts` (new), `dash/src/capture/windows-files.test.ts` (new), `dash/src/capture/errno.test.ts` (new). Acceptance: per the T2 row; block editors are unchanged because no managed harness uses them. Depends on: none.

**T3 — Detection, structured status, mutex.** Skills: TypeScript engineering, `test-dev`. Objective: D9 detection, section 5 status and D12 mutex. Files: `dash/src/capture/detect.ts`, `status.ts`, `lock.ts` (new) with their tests; `dash/src/capture/index.ts` (export-only changes to `declaredFiles`, `presentKeys`, `readTextIfPresent`, `loadReceiptLenient`). Acceptance: per the T3 row. Depends on: T1 (path environment) and T2 (errno classifier).

**T4 — Structured events and actions.** Skills: TypeScript engineering, `test-dev`. Objective: additive `events`, `fileFilter`, probe timeout, and `actions.ts`. Files: `dash/src/capture/index.ts`, `probe.ts`, `actions.ts` (new), `actions.test.ts` (new). Acceptance: per the T4 row; text output byte-identical. Depends on: T2, T3.

**T5 — Capture route, receiver coupling, hardening.** Skills: TypeScript engineering, `test-dev`, `csp-security` guidance. Objective: section 5 over HTTP with D5, D7, D10 and D12. Files: `dash/src/server/capture-routes.ts` (new), `dash/src/server/routes.ts` (mount block and `KyberRouteDeps` capture field only), `dash/src/cli/web.ts` (pass the capture options), `dash/src/server/testing.ts` (`makeDeps` capture option), `dash/src/server/capture-route.test.ts` (new), `dash/src/cli/web.test.ts` (additions). Acceptance: per the T5 row. RED may start once T0 exists; GREEN depends on: T0, T3, T4.

**T6 — Web API client.** Skills: `react-dev`, `test-dev`. Files: `dash/web/src/lib/kyberApi.ts` (append only), `dash/web/src/lib/kyberApi.capture.test.ts` (new). Acceptance: per the T6 row. Depends on: T0.

**T7 — SlideOutDrawer shell.** Skills: `react-dev`, `test-dev`. Files: `dash/web/src/components/ui/SlideOutDrawer.tsx` (new), `dash/web/src/components/SessionInspectorDrawer.tsx` (wrapper only), `dash/web/src/components/ui/SlideOutDrawer.test.tsx` (new). Acceptance: per the T7 row; existing drawer markup and test ids unchanged. Depends on: none.

**T8 — Settings drawer and header button.** Skills: `react-dev`, `test-dev`. Files: `dash/web/src/components/settings/SettingsDrawer.tsx`, `HarnessCollectionList.tsx` (new), `dash/web/src/App.tsx` (header button and open state only), their tests, `dash/web/src/App.test.tsx` (additions). Acceptance: per the T8 row. RED may start once T0 and T6 exist; GREEN depends on: T6, T7.

**T9 — Windows CI job.** Skill: `github-devops`. Files: `.github/workflows/ci.yml` (new `capture-windows` job and `ci-summary` needs). Acceptance: per the T9 row. Depends on: T4.

**T10 — Security review of the route.** Skill: `security-review`. Read-only. Depends on: T5.

**T11 — ADR 0031 amendment.** Skills: `architecture-decision-record`, `docs-dev`. Files: `docs/adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md`, `docs/adr/README.md` (row summary). Content: UI-triggered runs of the same engine on an explicit per-harness user action, Windows support, route-level receiver coupling (D5, D7, D20), applicability filtering (D19), and the revised Consequence that a green enable is not capture. Depends on: none (decisions are fixed by this plan).

**T12 — Documentation updates.** Skill: `docs-dev`. Files: `docs/dash/runbook.md` (new capture section covering CLI, the Settings drawer, Windows and the receiver coupling; fix the three-versus-four header tab drift in the same edit because that paragraph is touched), `docs/dash/architecture.md` (route table rows for `GET` and `POST /api/kyber/capture`, a capture paragraph, the drawer in the web dashboard list), `docs/dash/telemetry-inventory.md` (OpenCode row and a note on in-app enablement). Any new `code-refs` must exist in the CodeGraph. Depends on: T5, T8, T11.

**T13 — Whole-change review.** Skill: `code-review`, with the `security-review` result from T10 as input. Depends on: T9, T10, T12.

**T14 — `docs-dev` closeout (mandatory).** Skill: `docs-dev`. Archive this plan to `docs/archive/plans/`, move its row from Active to Archived in `docs/plans/README.md`, and write the closeout mapping in section 11 and the archive row. Run `docs validate . --merge-ready` and `docs drift .`. Depends on: T13.

## 9. Dependency graph and `MAX_CONCURRENCY` audit

```text
T0 ──► T6 ──┐
T0 ──► T5(RED)                         T7 ──┤
T1 ──┐                                      ├──► T8(GREEN) ──┐
T2 ──┼──► T3 ──► T4 ──► T5(GREEN) ──► T10 ──┘                ├──► T12 ──► T13 ──► T14
     └──────────────────► T9 (after T4)        T11 ───────────┘
```

- Wave A (no dependencies, disjoint file scopes): T0, T1, T2, T7, T11. Five tasks, so `MAX_CONCURRENCY: 5`.
- Wave B: T3 (after T1 and T2), T6 (after T0), T5 RED (after T0), T8 RED (after T0 and T6).
- Wave C: T4 (after T3), T8 GREEN (after T6 and T7).
- Wave D: T5 GREEN (after T0, T3, T4), T9 (after T4).
- Wave E: T10 (after T5), T12 (after T5, T8, T11).
- Wave F: T13, then T14, sequential.
- Sequential by file scope, not by output: `dash/src/capture/index.ts` is a hot file (T1 options, T3 exports, T4 events), so T1 then T3 then T4 never overlap. `dash/src/server/routes.ts` and `dash/src/cli/web.ts` belong to T5 alone, `dash/web/src/lib/kyberApi.ts` to T6 alone, and `dash/web/src/App.tsx` to T8 alone. The documentation files are disjoint between T11 (`docs/adr`), T12 (`docs/dash`) and T14 (`docs/plans`, `docs/archive`).
- Safe parallelism comes from the section 5 contract and the T0 fixtures: the web tasks do not wait for the engine, and both ends are checked against the same files.

## 10. Verification gates

Per touched surface, run before review:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
```

`npm --prefix dash run test` already includes `dash/web/src/**` and `display-layer.test.ts`. The tray is untouched, so the tray gates are not required. For documentation (T11, T12, T14), from the repository root:

```bash
dotnet restore KyberWeave.sln
dotnet build KyberWeave.sln -c Release --no-restore
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

T14 adds `--merge-ready` to `docs validate`. `docs drift` needs `.codegraph/`, which exists as of this plan; if it is unavailable the check is reported skipped, never passed.

Owner gates (cannot run in this plan's environment; recorded at closeout, not claimed): (1) on a Windows machine, enable then disable each of the three harnesses from the drawer, confirm the receipt and file bytes round-trip and that no unrelated key changed; (2) on macOS, the same round trip against copies of the real config files; (3) confirm the receiver reaches `kyberdash` liveness after the first enable.

## 11. Risks, scope, review and closeout

**Risks.**

- A mutating route edits files in the user's home from a loopback server. Mitigations: the existing Host and Origin guard (finding 8) plus the new Host-rebinding tests, a JSON-only 64KB body, a harness allowlist, fixed endpoint, no caller URL, key-level receipt edits, drift refusal, and the T10 review. A local process without an Origin header can still call it, which is the same trust level as every other mutating route.
- The preview is advisory: a file can change between dry run and write. The write recomputes, and drift protection still applies on disable.
- Concurrent CLI and UI runs are not serialized across processes (D12 covers the server only).
- The `endpoint-differs` case: an existing Claude or other endpoint pointing elsewhere is kept (ensure-if-absent), so collection reports `on` with a warning, not as working.
- OpenCode `XDG_CONFIG_HOME` and a pre-existing `opencode.json` beside the written `opencode.jsonc` are known limitations carried over from the current writer.
- The Windows CI job may fail on an unrelated native dependency; the fallback is the owner gate (D16).
- A Windows-style path on a non-Windows host is not a real path, so cross-platform tests stay resolution-only (section 6).
- A harness installed in a nonstandard place reads `not-detected` and cannot be switched on from the drawer; the CLI remains available.

**Out of scope.** The tray surface, new writers for Codex, Pi or Antigravity, any shipped script, changing the CLI's text output or receiver behavior, XDG support, and per-project config files.

**Review.** T13 runs the council over the whole accumulated change after T9, T10 and T12.

**Closeout mapping (to be completed by T14).** D1–D6 and D10–D20 harvest into `docs/dash/runbook.md` (capture section, Windows, receiver coupling) and `docs/dash/architecture.md` (route contract and drawer). D6, D7, D19 and D20 are recorded in ADR 0031's amendment (T11). D9, D13 and D14 live in the runbook and architecture text. Waivers: none requested; the architecture and runbook documents are updated, and the ADR amendment is the decision record.
