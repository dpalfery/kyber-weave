---
id: plans/2026-10-25-opencode-capture-deploy-kyberdash
title: "OpenCode capture that actually deploys: loadable plugin, one-command install and update, consent file, engine restart, honest measurability"
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-25
documentation-impact: extends-component
impact-components: [KyberDash, KyberSquad]
development-mode: test-first
---

# OpenCode capture that actually deploys: loadable plugin, one-command install and update, consent file, engine restart, honest measurability

**Status: Draft — decision-complete (Q1, Q2 answered by the owner 2026-10-25). Awaiting the
approve-and-execute gate (AP1). `docs validate` / `docs drift` not yet run by this harness
(no process tool); the conductor runs both before presenting AP1.**

**Development mode:** `test-first` (default; no opt-out supplied).
**Autonomy:** `supervised`.

## 1. Problem and goal

### 1.1 Defect (bug-crusher escalation, investigator verdict NEEDS_ARCHITECT)

Owner report: "opencode is not sending or showing telemetry data in kyberdash, no system
prompt, no tools, no skills, and the tray is not showing token counts. We designed a solution
with hooks but it does not seem to be working or it is not deployed."

The hook design from [the archived context-capture plan](../archive/plans/2026-10-23-opencode-context-capture-kyberdash.md)
(PR #425, envelope contract `'2'`) is on `main` and in `v0.1.8-rc.7`, but five separate
breaks stop any of it reaching the owner's dashboard:

1. **Nothing installs the plugin.** Installer automation was deferred (archived plan D7).
   `resolveOpencodePluginPaths` (`dash/src/capture/harnesses/opencode-plugin/plugin.ts:423`)
   has no production caller. `capture enable --harness opencode` writes only
   `experimental.openTelemetry` (`dash/src/capture/harnesses/opencode.ts:57-59`). Neither
   `kyber-weave squad install/update` nor `kyber-weave update` touches it. The owner's
   `~/.config/opencode/plugins/` does not exist.
2. **The released binary cannot ship the plugin.** `entry.ts` is not a build entry
   (`dash/tsup.config.ts:4`, `dash/tsup.sea.config.ts:11`); it is only an allowlisted entry
   in `dash/scripts/unreachable.mjs:149`. There is no self-contained file to copy.
3. **Even if copied, the installed OpenCode would not load it (new finding).** The entry was
   pinned against `@opencode/plugin 2.1.4` (`fixtures/context-payload.json`). The owner's
   OpenCode plugin API is `@opencode-ai/plugin 1.17.3`
   (`~/.config/opencode/node_modules/@opencode-ai/plugin/dist/index.d.ts`), whose module
   contract is `PluginModule = { id?, server: Plugin }` with
   `Plugin = (input, options?) => Promise<Hooks>`, and whose hooks include
   `experimental.chat.system.transform`, `tool.definition`, `chat.params`, `event`, and
   `tool.execute.before/after`. The entry's default export is a plain map keyed by invented
   kinds (`session.start`, `session.context`, …) and its named export is `{ id, setup }`.
   Neither is a 1.x plugin. The live-proven Kilo plugin (`dash/src/capture/kilo-plugin/source.ts`,
   an OpenCode fork) uses exactly the 1.x `{ id, server }` shape and those three hooks.
4. **Content consent cannot reach OpenCode.** System prompt, tool definitions, and skills
   ride only when `KYBERDASH_OPENCODE_CONTEXT=1` is in OpenCode's process environment
   (`entry.ts:36-41`). An installer cannot set that, and a Desktop app started from the Dock
   does not inherit shell variables.
5. **The running engine is stale.** The engine is `v0.1.8-rc.6` (pid 82901), which knows only
   contract `'1'`, while `rc.7` is on disk. ADR 0039's ensure-or-attach replaces an older
   engine only when a caller runs `ensureEngine` (`dash/src/engine/ensure.ts:76`); the C#
   `SelfUpdater.RunCore` (`src/KyberWeave.Cli/Update/SelfUpdater.cs:104-247`) replaces the
   `kyberdash` binary and, only when a tray is recorded and the release changed, runs
   `kyberdash menubar --update` (which quits and relaunches the tray,
   `dash/src/install/menubar.ts:188-212`). Nothing restarts the engine, and the
   "already on <version>" early return (`SelfUpdater.cs:131-132`) skips every follow-up.
   `canon.db` holds 35 spans quarantined as "unknown opencode contract version".

Two display faults sit on top, independent of the plugin:

6. **Measurability hardcodes OpenCode as blind.** `dash/src/canon/measurability.ts:822-832`
   (cache survey) and `:1135-1136`, `:1155-1156`, `:1181-1182`, `:1196-1199`, `:1204-1206`
   return `not_measurable` ("experimental OpenTelemetry disabled") for token usage, cost,
   cache, pressure, tools, and delegation. `evidencedAvailability` (`:1067-1084`) lets
   evidence override only the tool and pressure dimensions. The store holds 2,279 measured
   OpenCode records, so token usage and cost read as unmeasurable — the reason the tray shows
   no token counts.
7. **Absent buckets render as zero.** `extractContext` / `buildLatestSession`
   (`dash/src/analysis/report/build.ts:410-446`) mark any numeric bucket `measured`, so a
   `system_prompt` or `tool_definitions` bucket that the analysis zero-filled because no such
   part was observed shows "0 tokens" in the tray
   (`dash/tray/ui/src/components/SessionPanel.tsx:83-94`). The context window reads "source
   reported no context window", although OpenCode's `chat.params` input carries
   `model.limit.context` (`@opencode-ai/sdk` `Model.limit.context`, required).
8. **Test pollution (code-confirmed).** `KyberdashOpencodePlugin.setup` calls
   `createEntryHandlers()` with no transport options, so the default endpoint
   `http://127.0.0.1:4318/v1/traces` and the global `fetch` are used. `entry.test.ts:131`,
   `:141`, and `:153` call `setup` without `fetchFn`, so a developer machine with a live
   receiver receives contract-`'2'` spans from the test run. `dash/tests/setup/env-isolation.ts`
   isolates environment variables but not the network. Likely source of the 35 quarantined
   spans; T0 confirms.

### 1.2 Fixed owner constraints (relayed by the conductor, 2026-10-25)

- **C1.** `kyber-weave squad update --global` installs the OpenCode capture plugin (and
  `kyber-weave squad install` does the same).
- **C2.** `kyber-weave update` installs the new `kyberdash` executable **and** restarts the
  KyberDash processes (engine and tray) itself.
- **C3.** No extra manual KyberDash restart after `kyber-weave update`: one command does it
  all. This also clears the stale rc.6 engine.
- **C4.** Squad installs update but never overwrite: user edits and config (for example
  `opencode.jsonc`, or a plugin file the user modified) are preserved.

### 1.3 Goal

After `kyber-weave squad update --global` (OpenCode target) or `kyber-weave update`, with no
other command, the OpenCode capture plugin is installed or upgraded in OpenCode's global
plugin directory, the consent file exists (content off when newly created), and the
KyberDash engine and tray run the new version. When the owner turns OpenCode capture on in
the KyberDash Settings drawer (or runs `capture enable --harness opencode`), content capture
switches on. After the owner restarts OpenCode (KyberDash cannot restart the owner's
OpenCode), an OpenCode session shows the system prompt, tool definitions, and skills listing; measured
token usage, cost, and cache where the store holds counters; a reported context window; and
honest "not measurable" text — never a fabricated zero — for any bucket not sent. The tray
shows the same, because it renders the same `ContextReport`. Tests cannot reach a live
receiver.

### 1.4 Non-goals / out of scope

- No per-message token capture through the plugin's `event` hook (`message.updated`). Tokens
  keep arriving through OpenCode's own OpenTelemetry export (and the owner's third-party
  `@devtheops/opencode-plugin-otel`); this plan only stops declaring them unmeasurable. A todo
  is proposed in closeout.
- No lifecycle span emission from real 1.x hooks (such spans carry no model markers and would
  only add non-model quarantine rows).
- The Squad and update paths never edit `opencode.jsonc` or the owner's `plugin` array (C4).
  The plugin is installed by file into local discovery. Only an explicit
  `kyberdash kyber capture enable --harness opencode` still writes `experimental.openTelemetry`,
  with its existing drift refusal.
- No restart of the owner's OpenCode process; both commands print that OpenCode must be
  restarted to load a new or upgraded plugin.
- No automatic relink or deletion of the 35 quarantined spans (D9).
- No envelope contract bump (D6).
- No display logic in the tray or web dashboard (ADR 0033).
- No new runtime dependency (the bundle uses the esbuild tsup already brings).

## 2. Investigation findings

Discovery: `codegraph 10, docs 1, direct reads 17 (repo) + 5 (owner machine, read-only), greps 4`.

CodeGraph calls covered: `entry.ts`, `plugin.ts` (`createOpencodePlugin`,
`registerOpencodePlugin`, `createInstalledOpencodePlugin`, `resolveOpencodePluginPaths`),
`otlp-sender.ts`, `outbox.ts`, `kilo-plugin/install.ts`, `kilo-plugin/source.ts`,
`harnesses/opencode.ts`, `harnesses/kilo.ts`, `harnesses/registry.ts`, `capture/index.ts`,
`capture/detect.ts`, `server/capture-routes.ts`, `canon/measurability.ts`, `canon/harnesses.ts`,
`canon/sessions.ts`, `analysis/report/build.ts`, `SelfUpdater.cs` (`RunCore`,
`ShouldUpdateKyberDash`, `ShouldUpdateTray`, `UpdateTray`), `SquadUpdateCommand.cs`
(`RunUpdate`, `UpdateRun` with `ReplaceManaged`), `install/menubar.ts` (`installTray`,
`installMacos`, `quitRunningTray`, `launch`), `engine/ensure.ts` (`ensureEngine`). The docs
call covered ADR 0039, the architecture surface-layer section, and runbook §1.

Direct reads, each with its reason:

1. Root `AGENTS.md` (grep) — Config Reg paths and doc gates; not code.
2. `products/kyber-squad/agents/architect/references/plan-authoring.md` — role contract.
3. `…/references/test-first-contract.md` — role contract.
4. `docs/plans/README.md` — index shape.
5. `docs/archive/plans/2026-10-23-opencode-context-capture-kyberdash.md` — verbatim D1–D7
   and T0 gap record needed; a docs-tool excerpt would cut them.
6. `dash/src/capture/harnesses/opencode-plugin/entry.ts` — CodeGraph gap over lines 130-200.
7. `dash/tsup.config.ts` and 8. `dash/tsup.sea.config.ts` — build config, not indexed.
9. `dash/scripts/unreachable.mjs` (130-164) — `ENTRIES` / `TEST_ENTRIES` verbatim. Corrects
   the investigator: the entry is allowlisted, not reported unreachable.
10. `…/fixtures/context-payload.json` and 11. `…/fixtures/install-targets.json` — JSON, not indexed.
12. `dash/tests/setup/env-isolation.ts` — confirm no network guard exists.
13. `entry.test.ts` (grep) — which tests call `setup` without `fetchFn`.
14. `canon/measurability.ts` (800-849, 1050-1219) — CodeGraph trimmed these regions.
15. `analysis/report/build.ts` (400-454) — CodeGraph gap over `buildLatestSession`.
16. `tray/ui/src/components/SessionPanel.tsx` (40-104) — confirm the tray only renders.
17. `dash/package.json` (grep) and `dash/scripts/bundle-litellm.mjs` (head) — build-script precedent.

Greps: `filePlugin` usages (`capture/index.ts:834`, `:1255`, `status.ts:365`,
`capture-routes.ts:82`, `:87`); `ensureEngine` location; Squad command files; OpenCode in
`KyberWeave.Core/Squad` (`SquadGlobalRoots.cs:47`, `:122` — OpenCode global root honours
`$OPENCODE_CONFIG_DIR` before `~/.config/opencode`).

Owner-machine reads (read-only, outside the repo, labelled self-gathered):
`~/.config/opencode/` (no `plugins/`), `@opencode-ai/plugin` `package.json` (1.17.3) and
`dist/index.d.ts` (finding 3), `@opencode-ai/sdk` types grep (`Model.limit.context`),
`~/.cache/opencode` grep (`@devtheops/opencode-plugin-otel` 2.0.0 is the only plugin package).

### 2.1 What the checkout already gives the fix

1. A correct transport (bounded outbox, never-fail-host, OTLP/HTTP to `127.0.0.1:4318`) and a
   correct contract-`'2'` receiver side (`applyEnvelope` bucketing, `opencode.context.skills`).
2. A proven file-plugin installer: `kilo-plugin/install.ts` (marker, SHA-256 receipt,
   conflict and drift refusal, dry run, uninstall, `partial` for an older marker).
3. A proven 1.x hook surface for this data: the Kilo plugin's three hooks.
4. ADR 0039 `ensureEngine`: replaces an engine of another version and starts the new one
   detached, with test seams.
5. Tray update already quits and relaunches the tray (`installMacos`, `installWindows`).
6. `SelfUpdater` already runs `kyberdash` as a child through an injectable `_runProcess`
   seam, which the new restart and plugin-sync steps reuse.
7. Squad already resolves OpenCode's real global root (`SquadGlobalRoots`) and has
   preserve-local-edits semantics (`ReplaceManaged` defaults off).
8. `SessionDigest` already sums `totalInput` / `totalCacheRead` per harness.

### 2.2 GAPS

- **G1 — 1.x loader rule for extra named exports.** Mitigation: the bundle exports only
  `default` (D2); T0 pins the rule from the OpenCode source at the installed tag.
- **G2 — where OpenCode 1.x lists skills** (the `skill` tool description, or the system
  prompt). T0 pins it; otherwise the `readSystemPromptListing` fallback and a named
  not-measurable reason.
- **G3 — validation toolchain.** No process tool here; the conductor runs `docs validate` /
  `docs drift` before PLAN_READY.
- **G4 — the 35 quarantined spans.** T0 confirms test pollution by comparing their session
  ids against test fixture ids (read-only query on a copy of `canon.db`).
- **G5 — engine liveness probe from C#.** `SelfUpdater` must know whether an engine or tray
  is running. Mitigation: the new `kyberdash engine ensure --if-running` verb (D12) owns that
  decision in TypeScript; C# only calls it.

## 3. Architecture

### 3.1 Placement (unchanged rules)

- **Display-layer rule (ADR 0033).** Absent-versus-zero and the window are decided in
  `buildContextReport`; tray and web only render.
- **One owner per file (ADR 0031).** KyberDash owns the OpenCode plugin file, its receipt,
  and the consent file. `kyber-weave` (Squad and update) never writes them itself; it calls
  the `kyberdash` verb that does. ADR 0031 is amended: a managed writer may own a plugin file,
  and the install verb has a preserve mode used by Squad and update.
- **One engine (ADR 0039).** Restart goes through `ensureEngine`, never a second process.

### 3.2 Flows

```text
kyber-weave squad install|update --global   (OpenCode target selected)
   └─ after the Squad lifecycle succeeds:
      kyberdash kyber capture plugin install --harness opencode --preserve --config-dir <Squad-resolved OpenCode root>
         ├─ plugin file absent           → write + receipt
         ├─ ours, unmodified, older/same → upgrade or noop
         ├─ ours but edited by the user  → keep, warn, exit 0      (C4)
         ├─ not ours                     → keep, warn, exit 0      (C4)
         ├─ consent file absent          → write with content OFF (Q2); present → never touched
         └─ never touches opencode.jsonc                            (C4)
      kyberdash absent → Squad logs a skip naming `kyber-weave update`; Squad's own result unchanged

kyber-weave update
   ├─ commit binaries (unchanged)
   ├─ kyberdash engine ensure --if-running        → replaces an older live engine (C2, C3)
   ├─ tray step: menubar --update (quits + relaunches tray) — or, when the release did not
   │  change but the tray is older than kyberdash, the same relaunch            (C2, C3)
   ├─ kyberdash kyber capture plugin install --harness opencode --preserve --if-installed
   │                                              → upgrades an installed plugin only
   └─ prints "restart OpenCode to load the upgraded plugin" when the plugin changed
   (the "already on <version>" path still runs the engine, tray and plugin reconciliation)

KyberDash Settings drawer: OpenCode on/off  ≡  kyberdash kyber capture enable|disable --harness opencode
   (POST /api/kyber/capture → runEnable / runDisable in dash/src/capture/index.ts)
   ├─ enable : writes experimental.openTelemetry (unchanged), installs/upgrades the plugin (strict mode),
   │           sets consent content = ON, ensures the engine
   └─ disable: sets consent content = OFF, removes the plugin and the JSONC key (unchanged restore rules)

OpenCode 1.x loads kyberdash-opencode.js → default { id: 'kyberdash-opencode', server }
   server() hooks: experimental.chat.system.transform, tool.definition, chat.params
   chat.params → one session.context event: sessionId, messageId, model, agent, provider,
                 contextLimit, + systemPrompt/toolsText/skills when consent allows
   consent = consent file (read per event) ; env KYBERDASH_OPENCODE_CONTEXT=0 forces off, =1 forces on
   → outbox → 127.0.0.1:4318 (contract '2', + opencode.model.context_limit) → engine → report
```

### 3.3 Bundle and delivery

`dash/scripts/bundle-opencode-plugin.mjs` bundles `entry.ts` with esbuild into one ESM file
that imports only `node:` built-ins and uses global `fetch`, and writes the text into a
committed generated module (`opencode-plugin/generated/plugin-source.ts`, exporting
`OPENCODE_PLUGIN_SOURCE` and `OPENCODE_PLUGIN_MARKER`), so npm and SEA builds both carry it.
A drift test fails when the committed text differs from a fresh bundle. First line:
`// kyberdash-opencode-plugin v<N>`.

### 3.4 Consent file

`~/.kyberdash/opencode-plugin.json` — `{ "version": 1, "content": <boolean> }`.

- **Install/update (Squad, `kyber-weave update`, preserve mode):** creates the file with
  `content: false` only when it is absent; an existing file is never rewritten.
- **Enable (Settings drawer or `capture enable --harness opencode`, through `runEnable`):**
  sets `content: true`.
- **Disable (Settings drawer or `capture disable --harness opencode`, through `runDisable`):**
  sets `content: false`.
- `capture status` and the capture status API report the consent state, so the drawer shows
  it without deriving anything (ADR 0033).

No separate consent verb is added; the Settings switch is the consent. The plugin reads it on
each `chat.params` (cheap, synchronous, failure means off), so a change takes effect without
reinstalling. `KYBERDASH_OPENCODE_CONTEXT` stays as an optional override (`0` off, `1` on).
Content still follows the 14-day content purge.

## Decisions

Permanent table; preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| C1–C4 | Owner constraints for install, update, restart, and non-overwrite. | Fixed constraints as stated in §1.2. | Relayed verbatim-intent by the conductor. They supersede archived D7 ("no installer") and drive D11–D14. | ANSWERED — owner requirement, relayed by conductor 2026-10-25 |
| Q1 | How does the owner give consent for OpenCode content (system prompt, tool definitions, skills)? | **A KyberDash-owned config file that the installer writes and the plugin reads** (§3.4). The `KYBERDASH_OPENCODE_CONTEXT` env var stays only as an optional override. | An installer cannot set OpenCode's process env, and Desktop does not inherit shell variables. The file is preserved once written (C4). | ANSWERED — owner YES, relayed by conductor 2026-10-25 |
| Q2 | When the installer first writes the consent file, is content on? | **Off.** Install/update (Squad, `kyber-weave update`, preserve mode) creates the consent file with `content: false` only if none exists and never overwrites an existing one. Turning OpenCode capture **on in the KyberDash Settings drawer** (the same path as `capture enable --harness opencode` / `runEnable`) sets `content: true`; turning it off (`runDisable`) sets `content: false`. | Keeps the archived D5 "off by default, explicit opt-in" stance; the opt-in is the existing Settings switch, so it needs no new verb or env var. The architect's earlier recommendation (on at install) was not taken. | ANSWERED — owner decision, relayed by conductor 2026-10-25 |
| D1 | What plugin shape does the bundle export? | **The OpenCode 1.x `PluginModule`: `export default { id: 'kyberdash-opencode', server }`**, `server` returning `experimental.chat.system.transform`, `tool.definition`, and `chat.params` hooks. The invented default hooks map and the `{ id, setup }` object are removed. | Verified against the owner's `@opencode-ai/plugin 1.17.3` types and the live-proven Kilo plugin. A future OpenCode v2 gets its own adapter behind the same pipeline. | RECOMMENDED — architect technical call (low risk, reversible) |
| D2 | Which named exports may the bundle carry? | **Only `default`.** | Removes G1 risk under any loader rule. | RECOMMENDED — architect technical call |
| D3 | How is the plugin built and shipped? | **esbuild bundle → committed generated text module → embedded in npm and SEA builds**, guarded by a drift test. | Mirrors the Kilo "source as text" pattern; no new dependency; a runtime file asset would not exist inside the SEA. | RECOMMENDED — architect technical call |
| D4 | How does KyberDash install and own the plugin? | **New `opencode-plugin/install.ts`** mirroring `kilo-plugin/install.ts` (marker, receipt `~/.kyberdash/opencode-plugin-receipt.json`, dry run, uninstall, state), with two modes: *strict* (`capture enable`, refuses conflict/drift with exit 1 as Kilo does) and *preserve* (Squad and update: keeps a foreign or edited file, warns, exits 0; consent file created `content: false` only if absent). `ManagedHarnessWriter` gains optional `filePlugin` plus an optional consent hook, so `capture enable` (strict install + consent on), `capture disable` (consent off + uninstall), `capture status`, and the Settings drawer cover the OpenCode plugin and its consent. Target: `<OpenCode root>/plugins/kyberdash-opencode.js`, root = `--config-dir` when given, else `$OPENCODE_CONFIG_DIR`, else `~/.config/opencode` (same precedence as `SquadGlobalRoots`). | Preserve mode is C4. One owner (KyberDash) per ADR 0031; Squad passes its resolved root so both agree on the path. | RECOMMENDED — architect technical call |
| D5 | What do `capture status` / `capture enable` report about the engine? | **Status reports the running engine's version against the CLI's own and the remedy (`kyber-weave update`); `capture enable --harness opencode` calls `ensureEngine`.** | Complements D12; uses ADR 0039 semantics. | RECOMMENDED — architect technical call |
| D6 | Contract bump for the context-window attribute? | **No bump.** Optional `opencode.model.context_limit` inside contract `'2'`, read as a `reported` window. | Additive; an rc.7 engine ignores it. A bump would quarantine every rc.7-era span again. | RECOMMENDED — architect technical call |
| D7 | How does measurability become evidence-driven for OpenCode? | **`DimensionEvidence` gains observed token, cache-read, and cost evidence from `SessionDigest`; `evidencedAvailability` returns `measured` (tokens, cache) / `derived` (cost) when the harness's sessions carry counters.** Static OpenCode reasons are reworded to the real condition. | Same pattern as the existing tool and pressure overrides (ADR 0009/0011). Tests pinning the old reason strings are updated in T6 (pinned-assertion tripwire acknowledged). | RECOMMENDED — architect technical call |
| D8 | Where is absent-versus-zero decided? | **In `buildContextReport`**: a bucket whose part was never observed is `unmeasurable` with a named reason; the analysis context block carries the observed-parts evidence. Tray gets render tests only. | ADR 0033. Old rows need a re-projection; D13 runs it. | RECOMMENDED — architect technical call |
| D9 | What happens to the 35 quarantined contract-`'2'` spans? | **Leave them in quarantine.** | Likely test pollution; relinking would create junk sessions. Quarantine is evidence (ADR 0035); the owner can wipe the `opencode` harness. | RECOMMENDED — architect technical call |
| D10 | How is the test network isolated? | **A vitest setup guard that fails closed on any fetch to `127.0.0.1:4318` / `localhost:4318`, plus `entry.test.ts` injecting `fetchFn`.** | Protects every future plugin test. | RECOMMENDED — architect technical call |
| D11 | How does Squad install the plugin (C1, C4)? | **After a successful Squad install or update whose resolved targets include OpenCode at global scope, the Squad command runs `kyberdash kyber capture plugin install --harness opencode --preserve --config-dir <resolved OpenCode global root>`** through an injectable process seam. A dry run passes `--dry-run`. `kyberdash` is found beside `kyber-weave` in the install directory, then on `PATH`; absent → a logged skip. A plugin-step failure is reported as a warning line and does not fail or roll back the Squad deployment. Project-scope Squad runs do not install it (the plugin is global capture). | One owner of the file (KyberDash); Squad keeps its own receipts for its own files. Rejected: rendering the plugin as a Squad pack resource — two owners of one file, and the bundle lives in the dash build, not the Squad pack. | RECOMMENDED — architect technical call |
| D12 | How does `kyber-weave update` restart KyberDash (C2, C3)? | **New non-blocking verb `kyberdash engine ensure --if-running`** (calls `ensureEngine`; replaces a live engine of another version; starts nothing when no engine is running; exits 0 with a one-line outcome). `SelfUpdater` runs it after the binaries commit, then the tray step. The tray step also runs when the release did not change but the recorded tray version is older than the installed `kyberdash`. The "already on <version>" path no longer returns before these reconciliation steps. | `kyberdash web --no-open` cannot be used: it becomes the engine in the foreground. Tray relaunch already exists in `menubar --update`. Running the reconciliation on "already on" is what clears today's stale rc.6 engine with one command. | RECOMMENDED — architect technical call |
| D13 | Does `kyber-weave update` also upgrade the plugin and re-project? | **Yes, only when already installed:** it runs `capture plugin install --harness opencode --preserve --if-installed`; the restarted engine runs one projection pass at start when its detector version is newer than the store's stamp (existing `kyber build` derivation). | C3 "one command does it all": a new `kyberdash` with an old plugin file would otherwise need a second command. `--if-installed` keeps update from adding a surface the owner never chose. | RECOMMENDED — architect technical call |
| D14 | Are there opt-outs? | **Yes:** `kyber-weave update --no-restart` (skip engine and tray reconciliation) and `squad … --no-capture-plugin` (skip the plugin step). | Matches the existing `--no-kyberdash` / `--no-menubar` style; defaults satisfy C1–C3. | RECOMMENDED — architect technical call |
| D15 | Does the Settings drawer need UI code for consent? | **No new logic.** The drawer's existing OpenCode switch already calls `POST /api/kyber/capture` → `runEnable` / `runDisable`; consent rides on those. The capture status API gains a consent field that the drawer renders as text (a render test only, if the drawer already lists status lines generically). | ADR 0033 display-layer rule: the decision lives in the capture engine, not the UI. | RECOMMENDED — architect technical call |
| AP1 | Approve-and-execute gate. | OPEN. | Presented by the conductor once `docs validate` and `docs drift` pass. | OPEN |

## 5. Task graph

File scopes are optimistic. Every task ends with the worker's own `task-gate`; a failing gate
goes back to the same worker. Skills are named, not agents.

| Task | Objective | Files / symbols (exact scope) | Depends on | Skills |
|---|---|---|---|---|
| T0 | Pin facts, no product code: (1) OpenCode 1.x loader rule for default vs named exports at the installed tag; (2) where 1.x lists skills (G2); (3) `chat.params` `model.limit.context` and `provider` shapes; (4) read-only check of the 35 quarantined rows (G4). Fixture + telemetry-inventory addendum line. | New `dash/src/capture/harnesses/opencode-plugin/fixtures/plugin-module-1x.json`; `docs/dash/telemetry-inventory.md` (addendum line) | — | test-dev, docs-dev |
| T1 | Test network guard + fix polluting tests (D10). | New `dash/tests/setup/network-guard.ts` (or extend `env-isolation.ts`), `dash/vitest.config.ts` (`setupFiles`), `dash/scripts/unreachable.mjs` (`TEST_ENTRIES` if new), `opencode-plugin/entry.test.ts` | — | test-dev |
| T2 | Rewrite the entry to the 1.x `PluginModule` (D1, D2) with consent read from the consent file plus env override (Q1, §3.4); one `session.context` per `chat.params`; never-fail-host; no work at import. `OpencodeHookPayload.contextLimit?`; mapper writes `opencode.model.context_limit`. | `opencode-plugin/entry.ts`, `entry.test.ts`, new `opencode-plugin/consent.ts` (path + read side, node built-ins only, bundled into the plugin) + `consent.test.ts`, `opencode-plugin/plugin.ts` (`OpencodeHookPayload`), `opencode-plugin/mapper.ts`, `mapper.test.ts` | T0, T1 | typescript-dev, test-dev |
| T3 | Bundle and embed (D3): build script, generated text module with marker, `build`/`build:cli`/SEA wiring, drift test, data-URL load test asserting `{ id, server }` and the three hooks. | New `dash/scripts/bundle-opencode-plugin.mjs`, new `opencode-plugin/generated/plugin-source.ts`, `dash/package.json` (scripts), `dash/scripts/unreachable.mjs`, new `opencode-plugin/bundle.test.ts` | T2 | typescript-dev, test-dev |
| T4 | KyberDash installer, verb, and consent wiring (D4, D15, Q2, §3.4): `install.ts` strict and preserve modes, `--config-dir`, `--if-installed`, `--dry-run`; consent file created `content:false` only if absent in preserve mode; `runEnable` for OpenCode installs the plugin (strict) and sets `content:true`; `runDisable` sets `content:false` and removes the plugin; `capture plugin install` verb; `ManagedHarnessWriter.filePlugin` + consent hook; status (CLI and API) reports plugin and consent state; Settings drawer renders it. | New `opencode-plugin/install.ts` + `install.test.ts`, `opencode-plugin/consent.ts` (write side; the read side lands in T2); `capture/harnesses/registry.ts`; `capture/index.ts` (`runEnable` ~`:834`, `runDisable` ~`:1255`); `capture/status.ts` (~`:365`); `server/capture-routes.ts` (`:82`, `:87`); `capture/harnesses/opencode.ts`; `cli/register.ts` (`registerKyberCommands`, `capture plugin`); Settings drawer status render test in `dash/web/src` (test only unless the drawer cannot show a status line generically); colocated tests | T3 | typescript-dev, react-dev, test-dev |
| T5 | Engine verb and status (D5, D12 dash side): `kyberdash engine ensure --if-running` (non-blocking, outcome line, exit codes); capture status engine-version line; `capture enable --harness opencode` ensures the engine. | New `dash/src/cli/engine.ts` (or the existing engine CLI module) registered in `cli/register.ts`; `capture/status.ts`; `capture/actions.ts` or `capture/index.ts` (enable path); `engine/ensure.ts` (read-only use); colocated tests | T4 | typescript-dev, test-dev |
| T6 | Evidence-driven measurability (D7). | `canon/measurability.ts` (`DimensionEvidence`, `evidencedAvailability`, OpenCode reasons, cache survey row), `canon/harnesses.ts` (`evidenceOf`), tests `measurability.test.ts`, `availability-evidence.test.ts`, `harnesses.test.ts` | — | typescript-dev, test-dev |
| T7 | Adapter: `opencode.model.context_limit` → `reported` window; skills source per the T0 pin (G2). | `canon/adapters/opencode-envelope.ts`, `canon/adapters/opencode.ts`, `canon/skills.ts` (only if T0 moves the source), tests `opencode-envelope.test.ts`, `opencode.test.ts`, `skills.test.ts` | T0 | typescript-dev, test-dev |
| T8 | Absent-versus-zero in the report (D8); reported window reaches the latest-session block; engine-start projection when the detector stamp is older (D13). | `analysis/report/build.ts` (`extractContext`, `buildLatestSession`), `analysis/context.ts` (observed-parts evidence), `cli/web.ts` (`runWebDashboard` start-up projection check, only if not already present), tests `build.test.ts`, `context.test.ts`; tray render test `dash/tray/ui/src/components/SessionPanel.test.tsx` (test only) | T7 | typescript-dev, react-dev, test-dev |
| T9 | Squad plugin step (D11, D14 Squad side): after a successful global install/update with OpenCode resolved, run the `kyberdash` verb through an injectable runner; locate `kyberdash`; dry-run pass-through; `--no-capture-plugin`; skip and warning lines; Squad exit code unaffected by the plugin step. | `src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs` (`RunUpdate`), `SquadInstallCommand.cs`, their settings classes, a new shared `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`, tests in `tests/KyberWeave.Tests/` (new `SquadCapturePluginStepTests.cs`) | T4 (consumes the verb contract) | csharp-dev, test-dev |
| T10 | Update restart and reconciliation (D12, D13, D14 update side): after commit, run `engine ensure --if-running`; tray step also when the recorded tray is older; plugin `--if-installed` step; "already on" path runs reconciliation; `--no-restart`; restart-OpenCode line. | `src/KyberWeave.Cli/Update/SelfUpdater.cs` (`RunCore`, `ShouldUpdateTray`, new reconcile method), `SelfUpdateHost.cs` (`SelfUpdateOptions.NoRestart`), `src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs` (flag), `tests/KyberWeave.Tests/UpdateCommandTests.cs` | T5 (consumes the verb contract) | csharp-dev, test-dev |
| T11 | Governed docs: telemetry-inventory OpenCode row; dash architecture (1.x entry, bundle, consent file, `context_limit`, managed writer file plugin, preserve mode, engine verb); dash runbook (one-command install/update, restart OpenCode, consent on/off, quarantine note); ADR 0031 amendment; `docs/cli-command-surface.md` (new verbs and flags); `docs/install.md` (what `kyber-weave update` now restarts); Kyber-Squad architecture (global OpenCode plugin step). | `docs/dash/telemetry-inventory.md`, `docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md`, `docs/cli-command-surface.md`, `docs/install.md`, `docs/kyber-squad/architecture.md` | T2–T10 | docs-dev |
| T12 | Validation, review, closeout, owner live gate. | No source; this plan (closeout mapping), `docs/plans/README.md` row | T1–T11 | docs-dev |

Dependency graph: `T0 → {T2, T7}`; `T1 → T2 → T3 → T4 → T5`; `T4 → T9`; `T5 → T10`;
`T7 → T8`; `T6` independent; `{T2..T10} → T11 → T12`. T0, T1, and T6 start together; T7–T8
run alongside T2–T5; T9 and T10 (C#) run alongside T5–T8 once their verb contracts exist.

## 6. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | Fixture + addendum (no executable test) | `npm --prefix dash run test -- opencode-plugin` (existing suites stay green) | `plugin-module-1x.json` quotes the 1.x types and loader rule with source and installed version; G2/G4 answers recorded | Fixture absent before T0; T0 returns BLOCKED if the loader rule cannot be quoted | Fixture landed with quotes, URLs, version; T2/T3 tests cite it |
| T1 | `dash/tests/setup/network-guard.test.ts` (new) + `entry.test.ts` | `npm --prefix dash run test -- network-guard entry` | In vitest a fetch to `127.0.0.1:4318` or `localhost:4318` rejects with the named guard error; other ports allowed; `entry.test.ts` has no guard hit | Guard test fails on the current tree; a probe shows `entry.test.ts` reaching the live endpoint | Guard test passes; full dash suite passes with zero guard hits |
| T2 | `entry.test.ts`, `mapper.test.ts` | `npm --prefix dash run test -- opencode-plugin/entry opencode-plugin/mapper` | Default export `{ id: 'kyberdash-opencode', server }`; `server()` returns exactly the three hooks; `chat.params` after `system.transform` + `tool.definition` emits one `session.context` with system, tools, model, agent, messageId, contextLimit; consent file `content:false` (or env `0`) drops only content fields; missing or unreadable consent file means off; env `1` forces on; a throwing hook never rejects; import starts no timer or fetch; mapper writes `opencode.model.context_limit` | Fails on the current tree: hooks map default, no `server`, env-only consent, no `contextLimit` | All pass unweakened against the T0 fixture |
| T3 | `opencode-plugin/bundle.test.ts` | `npm --prefix dash run test -- opencode-plugin/bundle` + `npm --prefix dash run build:cli` + `npm --prefix dash run check:reachable` | Committed source equals a fresh bundle; first line is the marker; only `node:` imports; data-URL import yields `{ id, server }` with the three hooks | Fails: no script, no generated module | Drift and load tests pass; build and reachability clean |
| T4 | `opencode-plugin/install.test.ts`, `capture/index.test.ts`, `capture/status.test.ts`, capture-routes tests | `npm --prefix dash run test -- opencode-plugin/install capture` | Temp HOME: preserve mode writes plugin + receipt + consent file with `content:false` when absent; an existing consent file (either value) is byte-identical after install/update; upgrades our unmodified older plugin; keeps an edited or foreign plugin with a warning and exit 0; never writes `opencode.jsonc`; `--if-installed` with nothing installed is a noop; `--config-dir` and `$OPENCODE_CONFIG_DIR` select the root; `--dry-run` writes nothing. `runEnable` for OpenCode (CLI and `POST /api/kyber/capture`) writes the JSONC key, installs the plugin (strict: conflict/drift exit 1), and sets `content:true` (creating the file if absent); `runDisable` sets `content:false` and removes the plugin and key; enable dry run changes nothing. Status (CLI and API) shows plugin and consent state; the Settings drawer shows the consent line from the API | Fails: no installer, no consent wiring, `filePlugin` not allowed on managed writers | All pass; Kilo installer tests unchanged and green |
| T5 | engine CLI test, `capture/status.test.ts`, enable-path test | `npm --prefix dash run test -- cli/engine capture/status capture/actions` | `engine ensure --if-running`: no live engine → starts nothing, outcome `not-running`, exit 0; same version → `attached`; older version → `replaced` (injected `ensureEngine` seams); unresponsive → exit 1 with the reason; returns without blocking. Status prints engine vs CLI version and remedy; OpenCode enable ensures the engine once | Fails: verb absent, no status line | Pass with injected seams; no real process started |
| T6 | `measurability.test.ts`, `availability-evidence.test.ts`, `harnesses.test.ts` | `npm --prefix dash run test -- measurability availability-evidence harnesses` | OpenCode with token evidence → `token_usage` measured, `cost` derived; cache-read evidence → cache measured; no evidence → `not_measurable` with the reworded reason; other harnesses unchanged | Evidence cases fail on the current tree | Pass; only the old reason-string assertions change wording |
| T7 | `opencode-envelope.test.ts`, `opencode.test.ts`, `skills.test.ts` | `npm --prefix dash run test -- opencode-envelope opencode skills` | A contract-`'2'` context span with `opencode.model.context_limit` yields a `reported` window; without it unchanged; skills per T0 pin, malformed → named not-measurable | Window test fails (attribute ignored) | Pass; existing envelope tests green |
| T8 | `build.test.ts`, `context.test.ts`, web start-up test, `SessionPanel.test.tsx` | `npm --prefix dash run test -- report/build analysis/context cli/web` + `npm --prefix dash/tray/ui run test -- SessionPanel` | No `system_prompt` part → bucket `unmeasurable` with a named reason, not `measured(0)`; observed part → `measured`; reported window → measured window and pressure; an engine starting over a store with an older detector stamp projects once; tray shows the reason text, not "0" | Build test fails on the current tree (`measured(0)`) | Pass; CLI/REST parity and display-layer guard green |
| T9 | `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs` (+ existing Squad CLI tests) | `dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStep\|FullyQualifiedName~SquadCliCommand"` | Global update/install with OpenCode resolved → runner called once with `kyber capture plugin install --harness opencode --preserve --config-dir <resolved root>`; `--dry-run` passes through; project scope, no OpenCode target, or `--no-capture-plugin` → not called; `kyberdash` absent → skip line, exit unchanged; runner non-zero → warning line, Squad exit unchanged and no Squad rollback; declined confirmation → not called | New tests fail: no step exists | Pass; existing Squad tests green unweakened |
| T10 | `tests/KyberWeave.Tests/UpdateCommandTests.cs` | `dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~UpdateCommand"` | After commit, runner order is `engine ensure --if-running` → tray step → `capture plugin install --harness opencode --preserve --if-installed`; "already on <version>" still runs the three reconciliation steps; tray relaunch runs when the recorded tray version is older even with no release change; `--no-restart` skips engine and tray steps; `--no-kyberdash` skips all three; a failed engine step is reported by name and does not roll back binaries; restart-OpenCode line printed when the plugin step reports an upgrade | New tests fail: no reconciliation, early return on "already on" | Pass; existing update tests green except the "already on" assertions, which change to expect reconciliation (pinned-assertion change, named here) |
| T11 | Docs gates | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` + `docs drift .` | The seven docs describe the shipped behaviour truthfully | Pre-edit docs describe manual install and env-only consent | Zero findings on both |
| T12 | Whole-change gates + review | §7 commands + `review gates` | All gates green; owner live gate recorded | Gate failure blocks closeout | Evidence recorded in closeout; archived per `KW-DOC-LIFECYCLE-003` |

No-test tasks: none. Changing an approved Test-contract row, including weakening an assertion
to reach green, is a scope change: the plan returns to Draft for reapproval.

## 7. Risks, boundaries, verification gates

**Risks.**

1. **1.x loader detail (G1).** Mitigated by D2 and the T0 pin.
2. **Hook payload drift across OpenCode releases.** Defensive hook bodies; T0 records the version.
3. **Double counting with the third-party OTel plugin.** The KyberDash context record carries
   no token counters, so tokens are counted once.
4. **Engine replacement during update** stops an older engine mid-run: ADR 0039's designed
   handoff; the receiver child restarts with the new engine.
5. **Squad and KyberDash disagree on the OpenCode root.** Squad passes its resolved root
   (`--config-dir`), and the verb's own fallback uses the same precedence.
6. **Owner sees no system prompt until they flip the Settings switch** (Q2: content off at
   install). Squad and update output, and `capture status`, say so in one line naming the
   Settings switch.
7. **Mixed versions** (new `kyber-weave`, old `kyberdash` lacking the verbs): the C# steps
   treat "unknown command" (non-zero exit) as a named skip, never a failed update.

**Out-of-scope boundaries.** §1.4.

**Verification gates (T12).**

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
npm --prefix dash run build:cli
npm --prefix dash/tray/ui run test
dotnet build -c Release
dotnet test tests/KyberWeave.Tests -c Release
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

Owner live gate: run `kyber-weave update` once; confirm with no other command that the engine
and tray report the new version and the stale rc.6 engine is gone. Run
`kyber-weave squad update --global` (OpenCode target); confirm the plugin and consent file
exist (consent `content:false`) and `opencode.jsonc` is unchanged. Turn OpenCode on in the
KyberDash Settings drawer and confirm consent flips to `content:true`. Restart OpenCode, run one turn, and confirm the
Context view shows system prompt and tool definitions, Skills shows the listing, the tray
shows token counts and a context window, and absent buckets read "not measurable". Edit the
plugin file, rerun `squad update --global`, and confirm the edit is kept with a warning.
Recorded `[NOT LIVE-VERIFIED]` if not performed.

## 8. Review and closeout (docs-dev)

Whole-change review after T1–T11. Closeout maps every H id and decision id below to its
canonical doc. ADR disposition: **ADR 0031 amended** (managed writer may own a plugin file;
preserve mode for Squad and update callers). **ADR 0039 note**: `kyber-weave update` now
drives ensure-or-attach after an upgrade (amend in place if docs-dev judges it a new
invariant). Other items take an ADR waiver as refinements of ADRs 0009, 0011, 0033, 0035.
Proposed todo (only if the owner accepts): plugin-sourced per-message tokens through `event`.
Archive per `KW-DOC-LIFECYCLE-003` by the finishing pull request.

## Design harvest

| ID | Design item (section of this plan) | Target component | Target doc type |
|---|---|---|---|
| H1 | OpenCode 1.x `PluginModule` entry, three hooks, one context event per model call (D1, D2, T2) | KyberDash | architecture |
| H2 | Bundle → embedded text module → drift guard (D3, T3) | KyberDash | architecture |
| H3 | Installer strict and preserve modes, root precedence, receipt, managed writer file plugin (D4, T4) | KyberDash | architecture; runbook |
| H4 | Consent file: created off and never overwritten by install/update; Settings enable/disable flips it; env override (Q1, Q2, D15, §3.4) | KyberDash | architecture; runbook; reference |
| H5 | `engine ensure --if-running` verb; status engine-version line (D5, D12, T5) | KyberDash | architecture; runbook; reference |
| H6 | `opencode.model.context_limit` as a reported window in contract `'2'` (D6, T7) | KyberDash | architecture; reference |
| H7 | Evidence-driven token, cost, and cache measurability (D7, T6) | KyberDash | architecture; reference |
| H8 | Absent-versus-zero in `buildContextReport`; start-up projection on an older detector stamp (D8, D13, T8) | KyberDash | architecture |
| H9 | Quarantined contract-`'2'` spans left in place (D9) | KyberDash | runbook |
| H10 | Test network guard for the live receiver (D10, T1) | KyberDash | runbook |
| H11 | Squad global install/update runs the KyberDash plugin step; non-overwrite; opt-out (C1, C4, D11, D14, T9) | KyberSquad | architecture; reference |
| H12 | `kyber-weave update` restarts engine and tray and upgrades an installed plugin in one command, including the "already on" path; `--no-restart` (C2, C3, D12, D13, D14, T10) | KyberDash | runbook; reference |
