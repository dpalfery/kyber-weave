---
id: plans/2026-10-25-opencode-capture-deploy-kyberdash
title: "OpenCode capture that actually deploys: loadable plugin, one-command install and update, consent file, engine restart, honest measurability"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-26
documentation-impact: extends-component
impact-components: [KyberDash, KyberSquad]
development-mode: test-first
---

# OpenCode capture that actually deploys: loadable plugin, one-command install and update, consent file, engine restart, honest measurability

**Status: Complete — archived 2026-10-26 (revision R1 applied; owner chose to complete now
and to run the manual gate after the merge and rc.8). T0–T12 delivered on PR 435. The owner
live gate (§7) is `[NOT LIVE-VERIFIED]`: it needs rc.8, because `kyber-weave update` installs
released builds only and the machine's engine is still rc.6. Residual items are listed in the
Closeout mapping section below and in [docs/todo](../../todo/README.md). Validation evidence is
recorded in the closeout commit.**

**Development mode:** `test-first` (default; no opt-out supplied).
**Autonomy:** `supervised`.

## 1. Problem and goal

### 1.1 Defect (bug-crusher escalation, investigator verdict NEEDS_ARCHITECT)

Owner report: "opencode is not sending or showing telemetry data in kyberdash, no system
prompt, no tools, no skills, and the tray is not showing token counts. We designed a solution
with hooks but it does not seem to be working or it is not deployed."

The hook design from [the archived context-capture plan](2026-10-23-opencode-context-capture-kyberdash.md)
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
3. **Even if copied, the installed OpenCode would not load it (corrected in R1).** The
   runtime on the owner's PATH is OpenCode CLI 2.1.4 (`/opt/homebrew/Cellar/opencode/2.1.4`).
   Its loader reads only `.default`, which must match `{ id, effect: fn }` or
   `{ id, setup: fn }`; anything else fails with "Plugin must export a default definition
   with an id and an effect or setup function" (T0 fixture `plugin-module-1x.json`,
   `loader_default_vs_named_exports`). The entry's default export is a plain map keyed by
   invented kinds (`session.start`, …), so it is rejected. The correct `{ id, setup }`
   object exists in the entry, but only as the named export `KyberdashOpencodePlugin`, which
   2.1.4 never reads. (The `@opencode-ai/plugin 1.17.3` package in `~/.config/opencode` is
   types only; no 1.x runtime is installed. The original plan text took those types as the
   runtime contract; R1 corrects that.)
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
   `canon.db` holds 41 spans quarantined as "unknown opencode contract version" (35 at
   planning, 6 more since; T0 count).

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
   reported no context window". In 2.1.4 the `context` hook input carries only a model
   reference (`model.id` plus provider id); the runtime resolves the window from its provider
   catalog (`providers…models[modelID].limit.context`, binary byte search, R1). D6 covers how
   KyberDash gets a window.
8. **Test pollution (code-confirmed).** `KyberdashOpencodePlugin.setup` calls
   `createEntryHandlers()` with no transport options, so the default endpoint
   `http://127.0.0.1:4318/v1/traces` and the global `fetch` are used. `entry.test.ts:131`,
   `:141`, and `:153` call `setup` without `fetchFn`, so a developer machine with a live
   receiver receives contract-`'2'` spans from the test run. `dash/tests/setup/env-isolation.ts`
   isolates environment variables but not the network. T0 confirmed it: all 41 quarantined
   rows carry the hardcoded test session id `opencode-session-7f3a`, and rows keep arriving.

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

- No per-message token capture through the plugin. Tokens
  keep arriving through OpenCode's own OpenTelemetry export (and the owner's third-party
  `@devtheops/opencode-plugin-otel`); this plan only stops declaring them unmeasurable. A todo
  is proposed in closeout.
- No lifecycle span emission (such spans carry no model markers and would only add non-model
  quarantine rows).
- No OpenCode 1.x runtime support in this plan (no 1.x runtime exists on the owner's machine,
  so its loader cannot be verified; D2). Proposed as a todo in closeout.
- The Squad and update paths never edit `opencode.jsonc` or the owner's `plugin` array (C4).
  The plugin is installed by file into local discovery. Only an explicit
  `kyberdash kyber capture enable --harness opencode` still writes `experimental.openTelemetry`,
  with its existing drift refusal.
- No restart of the owner's OpenCode process; both commands print that OpenCode must be
  restarted to load a new or upgraded plugin.
- No automatic relink or deletion of the 41 quarantined test-pollution spans (D9).
- No envelope contract bump (D6).
- No display logic in the tray or web dashboard (ADR 0033).
- No new runtime dependency (the bundle uses the esbuild tsup already brings).

## 2. Investigation findings

Discovery: `codegraph 10, docs 1, direct reads 17 (repo) + 5 (owner machine, read-only), greps 4`.
R1 (2026-10-26) added: 1 direct read of the T0 fixture `plugin-module-1x.json` (JSON, not
indexed) and 6 read-only byte searches of the installed `/opt/homebrew/Cellar/opencode/2.1.4/bin/opencode`
(self-gathered; no index covers a vendor binary). They found the loader error string (2
hits), zero hits for `chat.params` / `tool.definition` / `system.transform` /
`experimental.chat.system.transform`, 9 `session.hook("context", …)` registrations by
built-in plugins reading `model.id`, `tools`, and `system[n].text`, and the window resolved
from the provider catalog (`providers…models[modelID].limit.context`). No `opencode plugin
check` command exists in 2.1.4.

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
3. A proven 2.1.4 hook surface for this data: OpenCode's own built-in plugins register
   `session.hook("context", cb)` and read `cb`'s input `model.id`, `tools` (a record keyed by
   tool id), and `system[n].text` (binary byte search, R1). The entry's existing
   `KyberdashOpencodePlugin = { id, setup }` already registers this hook; it is only exported
   under the wrong name.
4. ADR 0039 `ensureEngine`: replaces an engine of another version and starts the new one
   detached, with test seams.
5. Tray update already quits and relaunches the tray (`installMacos`, `installWindows`).
6. `SelfUpdater` already runs `kyberdash` as a child through an injectable `_runProcess`
   seam, which the new restart and plugin-sync steps reuse.
7. Squad already resolves OpenCode's real global root (`SquadGlobalRoots`) and has
   preserve-local-edits semantics (`ReplaceManaged` defaults off).
8. `SessionDigest` already sums `totalInput` / `totalCacheRead` per harness.

### 2.2 GAPS

- **G1 — loader rule. RESOLVED by T0 for 2.1.4:** only `.default` is read, and it must be
  `{ id, effect }` or `{ id, setup }`. The 1.x rule stays unpinned (no 1.x runtime present),
  so 1.x is out of scope (D2).
- **G2 — skills location. RESOLVED by T0 for 2.1.4:** an `<available_skills>` block in the
  system prompt text, one `<skill>` per skill with `<id>`, `<name>`, `<description>`; a
  mid-session change re-emits the block with "The available skills have changed…". The
  `skill` tool description lists no skills.
- **G3 — validation toolchain.** No process tool here; the conductor runs `docs validate` /
  `docs drift` before presenting AP2.
- **G4 — quarantined spans. RESOLVED by T0:** 41 rows, all test session
  `opencode-session-7f3a`; still arriving until T1 lands.
- **G6 — whether the 2.1.4 `setup` context exposes a provider/model catalog** from which the
  plugin could read the context window. Not pinned. D6 makes this optional: T2 pins it from
  the binary in its RED step; if absent, KyberDash uses its own catalog window.
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

OpenCode 2.1.4 loads kyberdash-opencode.js → default { id: 'kyberdash-opencode', setup }
   setup(ctx): await ctx.session.hook("context", cb)          (the only registration; no v1 hooks exist in 2.1.4)
   cb(input) per request assembly → one session.context event:
       sessionId (input.sessionID), model (input.model.id), provider (provider id on the ref),
       agent (input.agent), contextLimit (only if ctx exposes the catalog, D6),
       + systemPrompt (input.system[].text joined), toolsText (JSON of input.tools),
         skills (<id> values parsed from the <available_skills> block) — only when consent allows
   cb never mutates input (system/tools/messages are mutable in 2.1.4; KyberDash only reads)
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
each `context` hook call (cheap, synchronous, failure means off), so a change takes effect without
reinstalling. `KYBERDASH_OPENCODE_CONTEXT` stays as an optional override (`0` off, `1` on).
Content still follows the 14-day content purge.

## Decisions

Permanent table; preserved intact across all lifecycle transitions.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| C1, C2, C3, C4 | Owner constraints for install, update, restart, and non-overwrite. | Fixed constraints as stated in §1.2. | Relayed verbatim-intent by the conductor. They supersede archived D7 ("no installer") and drive D11–D14. | ANSWERED — owner requirement, relayed by conductor 2026-10-25 |
| Q1 | How does the owner give consent for OpenCode content (system prompt, tool definitions, skills)? | **A KyberDash-owned config file that the installer writes and the plugin reads** (§3.4). The `KYBERDASH_OPENCODE_CONTEXT` env var stays only as an optional override. | An installer cannot set OpenCode's process env, and Desktop does not inherit shell variables. The file is preserved once written (C4). | ANSWERED — owner YES, relayed by conductor 2026-10-25 |
| Q2 | When the installer first writes the consent file, is content on? | **Off.** Install/update (Squad, `kyber-weave update`, preserve mode) creates the consent file with `content: false` only if none exists and never overwrites an existing one. Turning OpenCode capture **on in the KyberDash Settings drawer** (the same path as `capture enable --harness opencode` / `runEnable`) sets `content: true`; turning it off (`runDisable`) sets `content: false`. | Keeps the archived D5 "off by default, explicit opt-in" stance; the opt-in is the existing Settings switch, so it needs no new verb or env var. The architect's earlier recommendation (on at install) was not taken. | ANSWERED — owner decision, relayed by conductor 2026-10-25 |
| D1 | What plugin shape does the bundle export? | **R1: `export default { id: 'kyberdash-opencode', setup }`**, the shape the installed 2.1.4 loader accepts. `setup(ctx)` registers only `ctx.session.hook("context", cb)` and reads (never mutates) `sessionID`, `model.id`, the provider id, `agent`, `system[].text`, and `tools`. It is the entry's existing `KyberdashOpencodePlugin` logic, moved to `default`. The invented dotted-kinds default map and the `registerOpencodePlugin` host bridge are removed from the entry. *(Superseded original text: "1.x `PluginModule` `{ id, server }` with `experimental.chat.system.transform`, `tool.definition`, `chat.params`" — rejected by the 2.1.4 loader per T0; those hooks do not exist in the 2.1.4 binary, byte search R1.)* | T0 fixture `loader_default_vs_named_exports` quotes the 2.1.4 schema `{ id, effect } \| { id, setup }` and its error text. A byte search of the 2.1.4 binary (R1) finds 9 `session.hook("context", …)` registrations by OpenCode's own built-in plugins, reading `model.id`, `tools`, and `system[n].text`, and zero occurrences of `chat.params`, `tool.definition`, or `system.transform`. `setup` was chosen over `effect` because `effect` requires the Effect runtime types the bundle must not import; the loader wraps `setup` itself. | RECOMMENDED — architect technical call, R1 2026-10-26 (re-verified against the installed 2.1.4 binary) |
| D2 | Which named exports may the bundle carry, and is OpenCode 1.x supported? | **R1: only `default`; no 1.x support in this plan.** A combined default `{ id, setup, server }` was considered and rejected. | 2.1.4 ignores named exports, so `default` only is safe there. A combined object would rely on the 2.1.4 schema ignoring an extra `server` key. That behaviour is not verifiable from the binary, and if it is strict it would break the only runtime the owner has. No 1.x runtime exists on the machine to test a `server` shape against. 1.x support is proposed as a closeout todo. | RECOMMENDED — architect technical call, R1 2026-10-26 |
| D3 | How is the plugin built and shipped? | **esbuild bundle → committed generated text module → embedded in npm and SEA builds**, guarded by a drift test. | Mirrors the Kilo "source as text" pattern; no new dependency; a runtime file asset would not exist inside the SEA. | RECOMMENDED — architect technical call |
| D4 | How does KyberDash install and own the plugin? | **New `opencode-plugin/install.ts`** mirroring `kilo-plugin/install.ts` (marker, receipt `~/.kyberdash/opencode-plugin-receipt.json`, dry run, uninstall, state), with two modes: *strict* (`capture enable`, refuses conflict/drift with exit 1 as Kilo does) and *preserve* (Squad and update: keeps a foreign or edited file, warns, exits 0; consent file created `content: false` only if absent). `ManagedHarnessWriter` gains optional `filePlugin` plus an optional consent hook, so `capture enable` (strict install + consent on), `capture disable` (consent off + uninstall), `capture status`, and the Settings drawer cover the OpenCode plugin and its consent. Target: `<OpenCode root>/plugins/kyberdash-opencode.js`, root = `--config-dir` when given, else `$OPENCODE_CONFIG_DIR`, else `~/.config/opencode` (same precedence as `SquadGlobalRoots`). | Preserve mode is C4. One owner (KyberDash) per ADR 0031; Squad passes its resolved root so both agree on the path. | RECOMMENDED — architect technical call |
| D5 | What do `capture status` / `capture enable` report about the engine? | **Status reports the running engine's version against the CLI's own and the remedy (`kyber-weave update`); `capture enable --harness opencode` calls `ensureEngine`.** | Complements D12; uses ADR 0039 semantics. | RECOMMENDED — architect technical call |
| D6 | How does an OpenCode session get a context window, and is there a contract bump? | **R1: no bump.** Optional `opencode.model.context_limit` inside contract `'2'`, read as `reported`, sent **only if** T2 pins a catalog lookup on the 2.1.4 `setup` context (G6). Otherwise the plugin omits it, and KyberDash resolves the window from its own model catalog by `model.id` with source `catalog` (an existing `ContextLimitSource`), so the tray shows a window labelled as catalog-derived rather than "source reported no context window". | The 2.1.4 `context` input carries only a model reference; OpenCode itself resolves `limit.context` from its provider catalog. Additive attribute: an rc.7 engine ignores it; a bump would quarantine every rc.7-era span again. | RECOMMENDED — architect technical call, R1 2026-10-26 |
| D7 | How does measurability become evidence-driven for OpenCode? | **`DimensionEvidence` gains observed token, cache-read, and cost evidence from `SessionDigest`; `evidencedAvailability` returns `measured` (tokens, cache) / `derived` (cost) when the harness's sessions carry counters.** Static OpenCode reasons are reworded to the real condition. | Same pattern as the existing tool and pressure overrides (ADR 0009/0011). Tests pinning the old reason strings are updated in T6 (pinned-assertion tripwire acknowledged). | RECOMMENDED — architect technical call |
| D8 | Where is absent-versus-zero decided? | **In `buildContextReport`**: a bucket whose part was never observed is `unmeasurable` with a named reason; the analysis context block carries the observed-parts evidence. Tray gets render tests only. | ADR 0033. Old rows need a re-projection; D13 runs it. | RECOMMENDED — architect technical call |
| D9 | What happens to the quarantined contract-`'2'` spans (41 per T0)? | **Leave them in quarantine.** | Confirmed test pollution (T0: all carry `opencode-session-7f3a`); relinking would create junk sessions. Quarantine is evidence (ADR 0035); the owner can wipe the `opencode` harness. | RECOMMENDED — architect technical call |
| D10 | How is the test network isolated? | **A vitest setup guard that fails closed on any fetch to `127.0.0.1:4318` / `localhost:4318`, plus `entry.test.ts` injecting `fetchFn`.** | Protects every future plugin test. | RECOMMENDED — architect technical call |
| D11 | How does Squad install the plugin (C1, C4)? | **After a successful Squad install or update whose resolved targets include OpenCode at global scope, the Squad command runs `kyberdash kyber capture plugin install --harness opencode --preserve --config-dir <resolved OpenCode global root>`** through an injectable process seam. A dry run passes `--dry-run`. `kyberdash` is found beside `kyber-weave` in the install directory, then on `PATH`; absent → a logged skip. A plugin-step failure is reported as a warning line and does not fail or roll back the Squad deployment. Project-scope Squad runs do not install it (the plugin is global capture). | One owner of the file (KyberDash); Squad keeps its own receipts for its own files. Rejected: rendering the plugin as a Squad pack resource — two owners of one file, and the bundle lives in the dash build, not the Squad pack. | RECOMMENDED — architect technical call |
| D12 | How does `kyber-weave update` restart KyberDash (C2, C3)? | **New non-blocking verb `kyberdash engine ensure --if-running`** (calls `ensureEngine`; replaces a live engine of another version; starts nothing when no engine is running; exits 0 with a one-line outcome). `SelfUpdater` runs it after the binaries commit, then the tray step. The tray step also runs when the release did not change but the recorded tray version is older than the installed `kyberdash`. The "already on <version>" path no longer returns before these reconciliation steps. | `kyberdash web --no-open` cannot be used: it becomes the engine in the foreground. Tray relaunch already exists in `menubar --update`. Running the reconciliation on "already on" is what clears today's stale rc.6 engine with one command. | RECOMMENDED — architect technical call |
| D13 | Does `kyber-weave update` also upgrade the plugin and re-project? | **Yes, only when already installed:** it runs `capture plugin install --harness opencode --preserve --if-installed`; the restarted engine runs one projection pass at start when its detector version is newer than the store's stamp (existing `kyber build` derivation). | C3 "one command does it all": a new `kyberdash` with an old plugin file would otherwise need a second command. `--if-installed` keeps update from adding a surface the owner never chose. | RECOMMENDED — architect technical call |
| D14 | Are there opt-outs? | **Yes:** `kyber-weave update --no-restart` (skip engine and tray reconciliation) and `squad … --no-capture-plugin` (skip the plugin step). | Matches the existing `--no-kyberdash` / `--no-menubar` style; defaults satisfy C1–C3. | RECOMMENDED — architect technical call |
| D15 | Does the Settings drawer need UI code for consent? | **No new logic.** The drawer's existing OpenCode switch already calls `POST /api/kyber/capture` → `runEnable` / `runDisable`; consent rides on those. The capture status API gains a consent field that the drawer renders as text (a render test only, if the drawer already lists status lines generically). | ADR 0033 display-layer rule: the decision lives in the capture engine, not the UI. | RECOMMENDED — architect technical call |
| AP1 | Approve-and-execute gate. | Approved; execution began with T0. | The conductor reported T0 executed "on the approved plan" (2026-10-26). Superseded by R1, which changed approved Test-contract rows. | Approved per conductor relay 2026-10-26; superseded by R1 |
| R1 | T0 found the installed runtime is OpenCode 2.1.4, which rejects D1's `{ id, server }`. How does the plan change? | D1, D2, D6 revised (above). Test-contract rows T2, T3, T7 rewritten for the 2.1.4 `setup` / `context` hook shape and `<available_skills>` parsing. §1.1 finding 3, §2.1, §2.2, §3.2 corrected. Quarantine count 35 → 41. T0 stays complete. | Test-first contract rule: changing an approved Test-contract row is a scope change, so the plan returns to Draft and needs re-approval (AP2). The C#, measurability, report, installer, and update work (T1, T4–T6, T8–T12) is unchanged in scope; T4–T5 file scopes are unchanged. | Architect revision 2026-10-26 |
| AP2 | Re-approve and execute after R1. | Closed by owner direction 2026-10-26: complete the plan now; the manual live gate is run after merge and rc.8. | Recorded in the Closeout mapping section. | CLOSED 2026-10-26 — owner direction relayed by the orchestrator |

## 5. Task graph

File scopes are optimistic. Every task ends with the worker's own `task-gate`; a failing gate
goes back to the same worker. Skills are named, not agents.

| Task | Objective | Files / symbols (exact scope) | Depends on | Skills |
|---|---|---|---|---|
| T0 | **DONE 2026-10-26.** Pin facts, no product code: (1) loader rule for default vs named exports at the installed runtime (found: 2.1.4, `{ id, effect \| setup }` default only); (2) where skills are listed (found: `<available_skills>` in the system prompt); (3) `chat.params` shapes (1.x types only; superseded for 2.1.4 by R1); (4) read-only check of the quarantined rows (found: 41, all test pollution). Fixture + telemetry-inventory addendum line. | New `dash/src/capture/harnesses/opencode-plugin/fixtures/plugin-module-1x.json`; `docs/dash/telemetry-inventory.md` (addendum line) | — | test-dev, docs-dev |
| T1 | Test network guard + fix polluting tests (D10). | New `dash/tests/setup/network-guard.ts` (or extend `env-isolation.ts`), `dash/vitest.config.ts` (`setupFiles`), `dash/scripts/unreachable.mjs` (`TEST_ENTRIES` if new), `opencode-plugin/entry.test.ts` | — | test-dev |
| T2 | (R1) Make the entry's default export the 2.1.4 shape `{ id: 'kyberdash-opencode', setup }` (D1, D2): `setup` registers only `ctx.session.hook("context", cb)`; `cb` maps the 2.1.4 input (`sessionID`, `model.id`, provider id on the ref, `agent`, `system[].text` joined, `tools` serialized, skill ids parsed from `<available_skills>`) to one `session.context` event; reads, never mutates; consent from the consent file plus env override (Q1, §3.4); never-fail-host; no work at import; remove the dotted-kinds default map and the `registerOpencodePlugin` bridge from the entry. RED step first pins G6 (catalog lookup on the `setup` context) from the 2.1.4 binary into the T0 fixture; `contextLimit` is sent only if it exists (D6). | `opencode-plugin/entry.ts`, `entry.test.ts`, `opencode-plugin/fixtures/plugin-module-1x.json` (append the 2.1.4 `context` input shape and the G6 answer), new `opencode-plugin/skills-block.ts` (pure `<available_skills>` parser, shared with T7) + `skills-block.test.ts`, new `opencode-plugin/consent.ts` (path + read side, node built-ins only, bundled into the plugin) + `consent.test.ts`, `opencode-plugin/plugin.ts` (`OpencodeHookPayload`), `opencode-plugin/mapper.ts`, `mapper.test.ts` | T0, T1 | typescript-dev, test-dev |
| T3 | Bundle and embed (D3): build script, generated text module with marker, `build`/`build:cli`/SEA wiring, drift test, data-URL load test asserting the 2.1.4 default shape `{ id, setup }` (R1) and that `setup` registers exactly one `context` hook on a fake context. | New `dash/scripts/bundle-opencode-plugin.mjs`, new `opencode-plugin/generated/plugin-source.ts`, `dash/package.json` (scripts), `dash/scripts/unreachable.mjs`, new `opencode-plugin/bundle.test.ts` | T2 | typescript-dev, test-dev |
| T4 | KyberDash installer, verb, and consent wiring (D4, D15, Q2, §3.4): `install.ts` strict and preserve modes, `--config-dir`, `--if-installed`, `--dry-run`; consent file created `content:false` only if absent in preserve mode; `runEnable` for OpenCode installs the plugin (strict) and sets `content:true`; `runDisable` sets `content:false` and removes the plugin; `capture plugin install` verb; `ManagedHarnessWriter.filePlugin` + consent hook; status (CLI and API) reports plugin and consent state; Settings drawer renders it. | New `opencode-plugin/install.ts` + `install.test.ts`, `opencode-plugin/consent.ts` (write side; the read side lands in T2); `capture/harnesses/registry.ts`; `capture/index.ts` (`runEnable` ~`:834`, `runDisable` ~`:1255`); `capture/status.ts` (~`:365`); `server/capture-routes.ts` (`:82`, `:87`); `capture/harnesses/opencode.ts`; `cli/register.ts` (`registerKyberCommands`, `capture plugin`); Settings drawer status render test in `dash/web/src` (test only unless the drawer cannot show a status line generically); colocated tests | T3 | typescript-dev, react-dev, test-dev |
| T5 | Engine verb and status (D5, D12 dash side): `kyberdash engine ensure --if-running` (non-blocking, outcome line, exit codes); capture status engine-version line; `capture enable --harness opencode` ensures the engine. | New `dash/src/cli/engine.ts` (or the existing engine CLI module) registered in `cli/register.ts`; `capture/status.ts`; `capture/actions.ts` or `capture/index.ts` (enable path); `engine/ensure.ts` (read-only use); colocated tests | T4 | typescript-dev, test-dev |
| T6 | Evidence-driven measurability (D7). | `canon/measurability.ts` (`DimensionEvidence`, `evidencedAvailability`, OpenCode reasons, cache survey row), `canon/harnesses.ts` (`evidenceOf`), tests `measurability.test.ts`, `availability-evidence.test.ts`, `harnesses.test.ts` | — | typescript-dev, test-dev |
| T7 | (R1) Adapter and skills: `opencode.model.context_limit` → `reported` window when present; otherwise the OpenCode record gets a `catalog` window from KyberDash's model catalog by model id (D6); skills: when `opencode.context.skills` is absent or empty, parse the `<available_skills>` block from the captured system prompt (shared parser from T2), then fall back to `readSystemPromptListing`; malformed or absent block → named not-measurable reason. | `canon/adapters/opencode-envelope.ts`, `canon/adapters/opencode.ts`, `canon/skills.ts`, the catalog-window helper the other adapters already use (no new catalog), tests `opencode-envelope.test.ts`, `opencode.test.ts`, `skills.test.ts` | T0, T2 (consumes `skills-block.ts`) | typescript-dev, test-dev |
| T8 | Absent-versus-zero in the report (D8); reported window reaches the latest-session block; engine-start projection when the detector stamp is older (D13). | `analysis/report/build.ts` (`extractContext`, `buildLatestSession`), `analysis/context.ts` (observed-parts evidence), `cli/web.ts` (`runWebDashboard` start-up projection check, only if not already present), tests `build.test.ts`, `context.test.ts`; tray render test `dash/tray/ui/src/components/SessionPanel.test.tsx` (test only) | T7 | typescript-dev, react-dev, test-dev |
| T9 | Squad plugin step (D11, D14 Squad side): after a successful global install/update with OpenCode resolved, run the `kyberdash` verb through an injectable runner; locate `kyberdash`; dry-run pass-through; `--no-capture-plugin`; skip and warning lines; Squad exit code unaffected by the plugin step. | `src/KyberWeave.Cli/Commands/Squad/SquadUpdateCommand.cs` (`RunUpdate`), `SquadInstallCommand.cs`, their settings classes, a new shared `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`, tests in `tests/KyberWeave.Tests/` (new `SquadCapturePluginStepTests.cs`) | T4 (consumes the verb contract) | csharp-dev, test-dev |
| T10 | Update restart and reconciliation (D12, D13, D14 update side): after commit, run `engine ensure --if-running`; tray step also when the recorded tray is older; plugin `--if-installed` step; "already on" path runs reconciliation; `--no-restart`; restart-OpenCode line. | `src/KyberWeave.Cli/Update/SelfUpdater.cs` (`RunCore`, `ShouldUpdateTray`, new reconcile method), `SelfUpdateHost.cs` (`SelfUpdateOptions.NoRestart`), `src/KyberWeave.Cli/Commands/Update/UpdateCommand.cs` (flag), `tests/KyberWeave.Tests/UpdateCommandTests.cs` | T5 (consumes the verb contract) | csharp-dev, test-dev |
| T11 | Governed docs: telemetry-inventory OpenCode row; dash architecture (2.1.4 `{ id, setup }` entry, skills block, catalog window, bundle, consent file, `context_limit`, managed writer file plugin, preserve mode, engine verb); dash runbook (one-command install/update, restart OpenCode, consent on/off, quarantine note); ADR 0031 amendment; `docs/cli-command-surface.md` (new verbs and flags); `docs/install.md` (what `kyber-weave update` now restarts); Kyber-Squad architecture (global OpenCode plugin step). | `docs/dash/telemetry-inventory.md`, `docs/dash/architecture.md`, `docs/dash/runbook.md`, `docs/adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md`, `docs/cli-command-surface.md`, `docs/install.md`, `docs/kyber-squad/architecture.md` | T2–T10 | docs-dev |
| T12 | Validation, review, closeout, owner live gate. | No source; this plan (closeout mapping), `docs/plans/README.md` row | T1–T11 | docs-dev |

Dependency graph (R1): T0 done; `T1 → T2 → T3 → T4 → T5`; `T2 → T7 → T8`; `T4 → T9`;
`T5 → T10`; `T6` independent; `{T2..T10} → T11 → T12`. T1 and T6 start together; T7–T8 run
alongside T3–T5; T9 and T10 (C#) run alongside T5–T8 once their verb contracts exist.

## 6. Test contract (test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | Fixture + addendum (no executable test) | `npm --prefix dash run test -- opencode-plugin` (existing suites stay green) | `plugin-module-1x.json` quotes the installed runtime's loader rule with source and version; G2/G4 answers recorded | Fixture absent before T0; T0 returns BLOCKED if the loader rule cannot be quoted | DONE 2026-10-26: fixture landed (2.1.4 loader, skills block, chat.params 1.x types, G4 = 41 test rows); its loader finding triggered R1 |
| T1 | `dash/tests/setup/network-guard.test.ts` (new) + `entry.test.ts` | `npm --prefix dash run test -- network-guard entry` | In vitest a fetch to `127.0.0.1:4318` or `localhost:4318` rejects with the named guard error; other ports allowed; `entry.test.ts` has no guard hit | Guard test fails on the current tree; a probe shows `entry.test.ts` reaching the live endpoint | Guard test passes; full dash suite passes with zero guard hits |
| T2 (R1) | `entry.test.ts`, `skills-block.test.ts`, `consent.test.ts`, `mapper.test.ts` | `npm --prefix dash run test -- opencode-plugin/entry opencode-plugin/skills-block opencode-plugin/consent opencode-plugin/mapper` | The default export has `id === 'kyberdash-opencode'` and a function `setup`, and has no other keys, so it satisfies the 2.1.4 schema `{ id, setup }` quoted in the T0 fixture. `setup` on a fake 2.1.4 context registers exactly one `session.hook("context", …)`. A `context` input shaped like the fixture (`sessionID`, `model.id`, provider id, `agent`, `system[{text}]`, `tools{id:{description,input}}`) emits exactly one `session.context` event with sessionId, model, agent, systemPrompt (system texts joined), toolsText (JSON of the tools), skills (the `<id>` values from an `<available_skills>` block in the system text). The input object is deep-equal before and after the callback. Consent file `content:false` (or env `0`) drops only systemPrompt, toolsText, and skills; a missing or unreadable file means off; env `1` forces on. A callback that throws, or a `setup` whose `session.hook` rejects, never rejects to the host. Importing the module starts no timer or fetch. `contextLimit` and the `opencode.model.context_limit` mapper attribute appear only when the fixture's G6 answer gives a catalog lookup; otherwise the test asserts they are absent. The skills parser returns ids in order, handles the "available skills have changed" re-emission, and returns a named failure (not `[]`) for a malformed block | Fails on the current tree: the default export is the dotted-kinds map, consent is env-only, no skills parser | All pass unweakened against the T0 fixture shapes |
| T3 | `opencode-plugin/bundle.test.ts` | `npm --prefix dash run test -- opencode-plugin/bundle` + `npm --prefix dash run build:cli` + `npm --prefix dash run check:reachable` | Committed source equals a fresh bundle; first line is the marker; only `node:` imports; data-URL import's `.default` has exactly the keys `id` and `setup` (`setup` a function), and its `setup` registers one `context` hook on a fake context; no named export is required for loading (R1) | Fails: no script, no generated module | Drift and load tests pass; build and reachability clean. Owner live gate (§7) adds the real 2.1.4 load check |
| T4 | `opencode-plugin/install.test.ts`, `capture/index.test.ts`, `capture/status.test.ts`, capture-routes tests | `npm --prefix dash run test -- opencode-plugin/install capture` | Temp HOME: preserve mode writes plugin + receipt + consent file with `content:false` when absent; an existing consent file (either value) is byte-identical after install/update; upgrades our unmodified older plugin; keeps an edited or foreign plugin with a warning and exit 0; never writes `opencode.jsonc`; `--if-installed` with nothing installed is a noop; `--config-dir` and `$OPENCODE_CONFIG_DIR` select the root; `--dry-run` writes nothing. `runEnable` for OpenCode (CLI and `POST /api/kyber/capture`) writes the JSONC key, installs the plugin (strict: conflict/drift exit 1), and sets `content:true` (creating the file if absent); `runDisable` sets `content:false` and removes the plugin and key; enable dry run changes nothing. Status (CLI and API) shows plugin and consent state; the Settings drawer shows the consent line from the API | Fails: no installer, no consent wiring, `filePlugin` not allowed on managed writers | All pass; Kilo installer tests unchanged and green |
| T5 | engine CLI test, `capture/status.test.ts`, enable-path test | `npm --prefix dash run test -- cli/engine capture/status capture/actions` | `engine ensure --if-running`: no live engine → starts nothing, outcome `not-running`, exit 0; same version → `attached`; older version → `replaced` (injected `ensureEngine` seams); unresponsive → exit 1 with the reason; returns without blocking. Status prints engine vs CLI version and remedy; OpenCode enable ensures the engine once | Fails: verb absent, no status line | Pass with injected seams; no real process started |
| T6 | `measurability.test.ts`, `availability-evidence.test.ts`, `harnesses.test.ts` | `npm --prefix dash run test -- measurability availability-evidence harnesses` | OpenCode with token evidence → `token_usage` measured, `cost` derived; cache-read evidence → cache measured; no evidence → `not_measurable` with the reworded reason; other harnesses unchanged | Evidence cases fail on the current tree | Pass; only the old reason-string assertions change wording |
| T7 | `opencode-envelope.test.ts`, `opencode.test.ts`, `skills.test.ts` | `npm --prefix dash run test -- opencode-envelope opencode skills` | (R1) A contract-`'2'` context span with `opencode.model.context_limit` yields a `reported` window; without it, a known model id yields a `catalog` window and an unknown one yields no window with its named reason; a span whose `opencode.context.skills` is absent but whose system prompt holds an `<available_skills>` block yields a measurable listing of those ids; no block → `readSystemPromptListing` fallback; malformed block → named not-measurable, never an empty measured list | Window and block-parsing tests fail on the current tree | Pass; existing envelope and skills tests green |
| T8 | `build.test.ts`, `context.test.ts`, web start-up test, `SessionPanel.test.tsx` | `npm --prefix dash run test -- report/build analysis/context cli/web` + `npm --prefix dash/tray/ui run test -- SessionPanel` | No `system_prompt` part → bucket `unmeasurable` with a named reason, not `measured(0)`; observed part → `measured`; reported window → measured window and pressure; an engine starting over a store with an older detector stamp projects once; tray shows the reason text, not "0" | Build test fails on the current tree (`measured(0)`) | Pass; CLI/REST parity and display-layer guard green |
| T9 | `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs` (+ existing Squad CLI tests) | `dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStep\|FullyQualifiedName~SquadCliCommand"` | Global update/install with OpenCode resolved → runner called once with `kyber capture plugin install --harness opencode --preserve --config-dir <resolved root>`; `--dry-run` passes through; project scope, no OpenCode target, or `--no-capture-plugin` → not called; `kyberdash` absent → skip line, exit unchanged; runner non-zero → warning line, Squad exit unchanged and no Squad rollback; declined confirmation → not called | New tests fail: no step exists | Pass; existing Squad tests green unweakened |
| T10 | `tests/KyberWeave.Tests/UpdateCommandTests.cs` | `dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~UpdateCommand"` | After commit, runner order is `engine ensure --if-running` → tray step → `capture plugin install --harness opencode --preserve --if-installed`; "already on <version>" still runs the three reconciliation steps; tray relaunch runs when the recorded tray version is older even with no release change; `--no-restart` skips engine and tray steps; `--no-kyberdash` skips all three; a failed engine step is reported by name and does not roll back binaries; restart-OpenCode line printed when the plugin step reports an upgrade | New tests fail: no reconciliation, early return on "already on" | Pass; existing update tests green except the "already on" assertions, which change to expect reconciliation (pinned-assertion change, named here) |
| T11 | Docs gates | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` + `docs drift .` | The seven docs describe the shipped behaviour truthfully | Pre-edit docs describe manual install and env-only consent | Zero findings on both |
| T12 | Whole-change gates + review | §7 commands + `review gates` | All gates green; owner live gate recorded | Gate failure blocks closeout | Evidence recorded in closeout; archived per `KW-DOC-LIFECYCLE-003` |

No-test tasks: none. Changing an approved Test-contract row, including weakening an assertion
to reach green, is a scope change: the plan returns to Draft for reapproval.

## 7. Risks, boundaries, verification gates

**Risks.**

1. **OpenCode 1.x users get no capture** (R1, D2). Their 1.x loader is unverified; a combined
   shape could break 2.1.4. Closeout proposes a todo.
2. **Hook payload drift across OpenCode releases.** Defensive callback (optional chaining,
   try/catch, absent stays absent); the T0 fixture records 2.1.4. Unit tests run against fakes,
   so only the owner live gate proves the real load (CI has no OpenCode binary).
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
exist (consent `content:false`) and `opencode.jsonc` is unchanged. Start OpenCode 2.1.4 and confirm it logs no "Plugin must export a default definition"
error for `kyberdash-opencode.js` (the real-loader check CI cannot run). Turn OpenCode on in the
KyberDash Settings drawer and confirm consent flips to `content:true`. Restart OpenCode, run one turn, and confirm the
Context view shows system prompt and tool definitions, Skills shows the listing, the tray
shows token counts and a context window, and absent buckets read "not measurable". Edit the
plugin file, rerun `squad update --global`, and confirm the edit is kept with a warning.
**Owner live gate status: `[NOT LIVE-VERIFIED]`.** Not performed at closeout (2026-10-26).
It needs release candidate rc.8: `kyber-weave update` installs only released builds, and the
owner's machine still runs the rc.6 engine. The owner runs it after merge and rc.8 with this
script, and bugs found there come back as issues:

1. `kyber-weave update` (no other command). Confirm the engine and tray report the new version
   and the rc.6 engine is gone.
2. `kyber-weave squad update --global` (OpenCode target). Confirm
   `~/.config/opencode/plugins/kyberdash-opencode.js` exists, `~/.kyberdash/opencode-plugin.json`
   holds `"content": false`, and `opencode.jsonc` is unchanged.
3. Open KyberDash, click Settings, and turn OpenCode on. Confirm the consent file now holds
   `"content": true`.
4. Restart OpenCode (2.1.4). Confirm its log has no "Plugin must export a default definition"
   error for `kyberdash-opencode.js`.
5. Run one OpenCode turn, then open the session in KyberDash. Confirm the Context view shows the
   system prompt and tool definitions, the Skills view shows the listing, and the tray shows token
   counts and a context window. Any bucket that was not sent must read "not measurable", never 0.
6. Edit `kyberdash-opencode.js` by hand, then run `kyber-weave squad update --global` again.
   Confirm the output keeps the edited file with a warning, and the file is unchanged.

## 8. Review and closeout (docs-dev)

Whole-change review after T1–T11. Closeout maps every H id and decision id below to its
canonical doc. ADR disposition: **ADR 0031 amended** (managed writer may own a plugin file;
preserve mode for Squad and update callers). **ADR 0039 note**: `kyber-weave update` now
drives ensure-or-attach after an upgrade (amend in place if docs-dev judges it a new
invariant). Other items take an ADR waiver as refinements of ADRs 0009, 0011, 0033, 0035.
Proposed todos (only if the owner accepts): OpenCode 1.x runtime support, verified against a real 1.x install (R1, D2); and plugin-sourced per-message tokens (the 2.1.4 hook for them still to be pinned).
Archive per `KW-DOC-LIFECYCLE-003` by the finishing pull request.

**Closeout record (2026-10-26).** Done in this closeout:

- Verified each harvest id against its governed document (see the Closeout mapping section).
  The F3 change is in the docs and in code: preserve mode keeps an older KyberDash plugin with no
  matching receipt as `kept-edited` (`dash/src/capture/harnesses/opencode-plugin/install.ts`,
  preserve branch, `kept-edited` when `found.proven` is false); strict mode still upgrades.
- Set the Decisions id cell `C1–C4` to `C1, C2, C3, C4` so each id matches the decision-id
  pattern. The row content is unchanged.
- Closed AP2 by owner direction on 2026-10-26.
- Corrected the stale detector generation in `docs/dash/architecture.md` to `DETECTOR_VERSION` 5,
  which matches `dash/src/canon/store.ts`.
- Residual todos: [OpenCode 1.x runtime support](../../todo/opencode-1x-runtime-support.md),
  [per-message OpenCode tokens](../../todo/opencode-plugin-per-message-tokens.md), and
  [C# formatter churn split](../../todo/csharp-formatter-churn-split.md). The esbuild devDependency
  declared in `9541bc4` needed no todo: section 8 does not list one.

**ADR disposition.** ADR 0031 is amended in place (preserve mode and the managed writer's file
plugin). ADR 0039 is amended in place (2026-10-26 note: `kyber-weave update` is a new caller of
ensure-or-attach, with no invariant changed). The other decisions refine ADRs 0009, 0011, 0033, and
0035 and take this waiver: no ADR — ADR waiver: refinements of existing KyberDash architecture (the
display-layer rule, contract-versioned envelope, quarantine evidence, and capture key ownership) that
add no new boundary, dependency, schema, or external surface beyond the plugin file that ADR 0031
already covers.

## Design harvest

| ID | Design item (section of this plan) | Target component | Target doc type |
|---|---|---|---|
| H1 | OpenCode 2.1.4 default `{ id, setup }` entry, single `session.hook("context")` registration, read-only, one context event per request; 1.x out of scope (D1, D2, R1, T2) | KyberDash | architecture |
| H13 | Skills from the `<available_skills>` system-prompt block; catalog window fallback when no reported window (G2, D6, R1, T7) | KyberDash | architecture; reference |
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

## Closeout mapping

Closed 2026-10-26 by the docs-dev closeout. Every Decisions id and every Design harvest id maps
to a live governed document (`docs/` path shown relative to this file) or carries a Documentation
waiver with its reason. Each mapped document was re-verified against the shipped source and carries
`last-reviewed` 2026-10-26. The documentation-impact of each item is the component of the mapped
document; both impact components, KyberDash and KyberSquad, are covered.

| ID | Destination | Documentation-impact | Status |
|---|---|---|---|
| C1 | [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) (global scope, OpenCode capture plugin); [kyber-squad/architecture.md](../../kyber-squad/architecture.md) (OpenCode plugin step) | KyberSquad | archived |
| C2, C3 | [install.md](../../install.md#updating) (one command restarts engine and tray); [dash/runbook.md](../../dash/runbook.md) (engine check) | Distribution, KyberDash | archived |
| C4 | [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) (preserve mode keeps edited or foreign files); [install.md](../../install.md#updating) (edited plugin file kept with a warning) | KyberSquad, Distribution | archived |
| Q1 | [dash/architecture.md](../../dash/architecture.md) (consent file, `~/.kyberdash/opencode-plugin.json`); [dash/runbook.md](../../dash/runbook.md) (content consent) | KyberDash | archived |
| Q2 | [dash/architecture.md](../../dash/architecture.md) (consent file: created off only when absent, Settings switch sets it on); [dash/runbook.md](../../dash/runbook.md) (content consent) | KyberDash | archived |
| D1 | [dash/architecture.md](../../dash/architecture.md) (OpenCode 2.1.4 entry, one default export `{ id, setup }`) | KyberDash | archived |
| D2 | [dash/architecture.md](../../dash/architecture.md) (entry: 1.x unsupported); [todo: OpenCode 1.x runtime support](../../todo/opencode-1x-runtime-support.md) | KyberDash | archived |
| D3 | [dash/architecture.md](../../dash/architecture.md) (bundle and delivery, generated text module, drift test) | KyberDash | archived |
| D4 | [dash/architecture.md](../../dash/architecture.md) (installer strict and preserve modes); [ADR 0031](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) (amended: managed writer owns a plugin file) | KyberDash | archived |
| D5 | [dash/architecture.md](../../dash/architecture.md) (engine ensure and projection); [dash/runbook.md](../../dash/runbook.md) (engine check and engine version in status) | KyberDash | archived |
| D6 | [dash/architecture.md](../../dash/architecture.md) (context window: reported attribute, catalog fallback, contract `'2'` unchanged) | KyberDash | archived |
| D7 | [dash/architecture.md](../../dash/architecture.md) (measurability: observed token, cache, and cost evidence) | KyberDash | archived |
| D8 | [dash/architecture.md](../../dash/architecture.md) (absent versus zero in `buildContextReport`) | KyberDash | archived |
| D9 | [dash/runbook.md](../../dash/runbook.md) (quarantined spans from before this change, left in place) | KyberDash | archived |
| D10 | [dash/runbook.md](../../dash/runbook.md) (tests: network guard refuses the live receiver port) | KyberDash | archived |
| D11 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) (OpenCode plugin step, `--preserve --config-dir`); [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) (global scope) | KyberSquad | archived |
| D12 | [install.md](../../install.md#updating) (engine and tray reconciliation, including "already on"); [ADR 0039](../../adr/0039-single-instance-engine.md) (amended 2026-10-26) | KyberDash, Distribution | archived |
| D13 | [install.md](../../install.md#updating) (plugin upgraded when installed; re-projection on engine start); [dash/runbook.md](../../dash/runbook.md) (re-projection after an upgrade) | KyberDash, Distribution | archived |
| D14 | [cli-command-surface.md](../../cli-command-surface.md) (`kyber-weave update --no-restart`, `squad --no-capture-plugin`) | KyberDash, KyberSquad | archived |
| D15 | [dash/architecture.md](../../dash/architecture.md) (consent rides the existing Settings switch); [dash/runbook.md](../../dash/runbook.md) (Settings drawer, consent on and off) | KyberDash | archived |
| R1 | Documentation waiver: R1 is the plan's own revision record of the Test contract. Its surviving invariants (the 2.1.4 default shape, no 1.x support, consent off at install) are captured by the D1, D2, and Q2 rows above. | none | archived |
| AP1 | Documentation waiver: approval-gate record for the approve-and-execute step, transient and superseded by R1. No surviving invariant. | none | archived |
| AP2 | Documentation waiver: approval record closed by owner direction on 2026-10-26; the owner's decision to complete now and run the live gate after rc.8 is a closeout record, with no surviving invariant beyond the live gate in section 7. | none | archived |
| H1 | [dash/architecture.md](../../dash/architecture.md) (OpenCode plugin span/turn envelope, entry R1) | KyberDash | archived |
| H2 | [dash/architecture.md](../../dash/architecture.md) (bundle and delivery) | KyberDash | archived |
| H3 | [dash/architecture.md](../../dash/architecture.md) (installer: strict and preserve modes, root precedence, receipt, file plugin; F3 preserve keeps an older plugin with no receipt as `kept-edited`); [dash/runbook.md](../../dash/runbook.md) (OpenCode capture plugin, install and update) | KyberDash | archived |
| H4 | [dash/architecture.md](../../dash/architecture.md) (consent file); [dash/runbook.md](../../dash/runbook.md) (content consent, on and off) | KyberDash | archived |
| H5 | [dash/architecture.md](../../dash/architecture.md) (`engine ensure --if-running`); [dash/runbook.md](../../dash/runbook.md) (engine check, engine version in status); [cli-command-surface.md](../../cli-command-surface.md) (engine ensure verb) | KyberDash | archived |
| H6 | [dash/architecture.md](../../dash/architecture.md) (context window: `opencode.model.context_limit` reported, catalog fallback) | KyberDash | archived |
| H7 | [dash/architecture.md](../../dash/architecture.md) (measurability, D7) | KyberDash | archived |
| H8 | [dash/architecture.md](../../dash/architecture.md) (absent versus zero; start-up projection on an older detector stamp) | KyberDash | archived |
| H9 | [dash/runbook.md](../../dash/runbook.md) (quarantined spans, `opencode-session-7f3a`, not relinked) | KyberDash | archived |
| H10 | [dash/runbook.md](../../dash/runbook.md) (tests: network guard) | KyberDash | archived |
| H11 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) (OpenCode plugin step); [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md) (global scope) | KyberSquad | archived |
| H12 | [install.md](../../install.md#updating) (one-command update, restarts, plugin upgrade); [dash/runbook.md](../../dash/runbook.md) (install and update: one command) | KyberDash, Distribution | archived |
| H13 | [dash/architecture.md](../../dash/architecture.md) (skills from the `<available_skills>` block; catalog window fallback) | KyberDash | archived |

**ADR disposition.** ADR 0031 amended in place (2026-10-26). ADR 0039 amended in place (2026-10-26,
`kyber-weave update` as a new caller of ensure-or-attach). Every other decision takes this waiver:
no ADR — ADR waiver: refinements of existing KyberDash architecture (display-layer rule, contract
versioned envelope, quarantine evidence, capture key ownership) that add no new boundary, dependency,
schema, or external surface beyond the plugin file that ADR 0031 already covers.

**Owner live gate.** `[NOT LIVE-VERIFIED]` (section 7; needs release candidate rc.8).
