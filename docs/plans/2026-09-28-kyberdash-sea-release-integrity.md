---
id: plans/2026-09-28-kyberdash-sea-release-integrity
title: KyberDash released-binary integrity (issue 157)
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-28
development-mode: test-first
keywords:
  - kyberdash
  - sea
  - release
  - tray
  - bridge
---

# KyberDash released-binary integrity (issue 157)

**Status: Draft**

Input: [GitHub issue #157](https://github.com/dpalfery/kyber-weave/issues/157), a bug filed
against `0.1.7-rc.14` on macOS `darwin-arm64`.

`development-mode: test-first`. The conductor relayed that the user did not opt out, so the
default stands. Every implementation task below has a Test-contract row.

This plan is not executable. Decisions D1–D3 in the [decision ledger](#decision-ledger-draft-only)
are open, and the Test contract has not been approved. The tasks and contract rows are written
against the recommended options. A different answer changes the rows named in each decision.

## Problem and goal

Issue #157 reports three defects in the released `kyberdash` single-executable (SEA):

1. `kyberdash web` serves the "not built" page instead of the web dashboard.
2. `kyberdash menubar` records `"kyberdashPath": "kyberdash"` in `~/.kyberdash/tray.json`, a
   bare name the tray will not spawn.
3. A `kyberdash web` server started before the first `dash refresh` keeps answering
   `GET /api/kyber/report` with `"harnesses": []` until it is restarted.

Goal: fix issue #157. After this plan, a released `kyberdash web` serves the built web
dashboard. A released `kyberdash menubar` records the absolute path of the running executable.
A `kyberdash web` server that started before `canon.db` existed reports harnesses once a refresh
has written them, with no restart. The tray popover shows them on its next report poll.

## Approved decisions

| Id | Decision | Approval provenance |
|---|---|---|
| P1 | The artifact is a plan, not a spec. | Conductor relayed the user's explicit choice of a plan on 2026-09-28. |
| P2 | `development-mode` is `test-first`. | Default. The conductor relayed on 2026-09-28 that the user did not opt out. |

No other decision is approved. D1–D3 are open.

## Decision ledger (Draft only)

| Id | Question | Options (recommended first) | Recommendation and rationale | Depends on | Status |
|---|---|---|---|---|---|
| D1 | How does the released binary carry the web dashboard? | **A** — Embed the built SPA in the SEA blob as one asset (`web.json`). `kyberdash web` serves it from memory. The archive layout, `install.sh`, and the self-updater stay unchanged. / **B** — Ship the built `dist/dash` tree beside the binary in `kyberdash-<rid>.tar.gz` and `.zip`. Extend `install.sh`, `SelfUpdater`, and the update loop to place and replace that directory next to `kyberdash`. / **C** — Keep the archive binary-only and have `kyberdash web` download the SPA on first use. | **A.** The binary stays one self-contained file, as [ADR 0020](../adr/0020-kyberdash-one-time-fork.md) decision 4 states. Binary and UI cannot drift apart across an update. Neither shell nor C# installer changes. B puts a `dash/` directory into `~/.local/bin`. It needs a non-atomic two-part replace in both installers and a new archive contract that an ADR would have to record. C adds runtime network access and a second checksum path. | — | OPEN |
| D2 | What should the web server's store handle recover from? | **A** — Only the mechanism found. When `canon.db` was absent at start, keep checking and open it read-only once it exists. / **B** — A, plus reopening when `canon.db` is replaced (device or inode change). / **C** — Leave the bridge. Make the tray restart `kyberdash web` after its first successful refresh. | **A.** It fixes the mechanism the code shows (see [Defect 3](#defect-3--stale-bridge-view-partially-confirmed)). No code path deletes or renames `canon.db`, so B guards a case nothing produces today. C fixes only the tray and leaves a hand-started `kyberdash web` broken. | — | OPEN |
| D3 | How do existing `tray.json` records holding a bare `kyberdashPath` get repaired? | **A** — No migration. The next `kyber-weave update` rewrites the record, because it hands off to `kyberdash menubar --update`. So does `kyberdash menubar --force`. Until then, the tray already falls through to `~/.local/bin/kyberdash` and `PATH`. / **B** — Also make a plain `kyberdash menubar` rewrite `kyberdashPath` when the recorded value is not an absolute existing file, without reinstalling the tray. | **A.** The tray already rejects the bare value and falls through (`cli.rs:181-213`), so an `install.sh` install keeps working meanwhile. B adds a new write path to `installTray`'s already-installed branch for a record the next update rewrites anyway. | — | OPEN |

Rows affected by each answer:

- **D1-B** replaces T5–T10 with installer, updater, and `ReleaseTests` install-fact tasks. It adds an ADR to closeout. **D1-C** is not planned, so the plan would need re-authoring.
- **D2-B** adds one assertion to T3 (a replaced file is re-read) and widens T4. **D2-C** removes T3/T4. It adds a `tauri-dev` RED/GREEN pair on `dash/tray/src-tauri/src/runtime.rs` and the tray gates.
- **D3-B** adds one assertion to T1 and one to T2, both in `dash/src/install/menubar.ts` and `menubar.test.ts`. That makes T2 overlap with nothing else.

## Investigation findings

Discovery used the Kyber-Weave MCP `docs_explore`, which was available. It reported that no
CodeGraph index was readable for code joins. CodeGraph `codegraph_explore` was used for code
(`.codegraph/` exists). `scripts/*.sh`, `.github/workflows/release.yml`, and ranges that
CodeGraph did not return were read directly. No agent-invocation tool exists in this harness,
so the external-source checks below are **self-gathered** rather than delegated to
`research-agent`. Nothing under `docs/archive/` was used as guidance. The two archived plans
named by the conductor were used only as format exemplars.

### Defect 1 — dashboard assets missing: CONFIRMED

Root cause: the release never builds the SPA and never puts it in the artifact, and a SEA
binary has no filesystem location where `resolveDashDir()` could find it.

- `.github/workflows/release.yml:259-271` runs `npm ci`, `npm version`, and
  `npx tsup --config tsup.sea.config.ts`. Nothing runs `build:web` or Vite.
  `dash/package.json:19` defines `build:web` (`cd web && npm install … && npm run build`), and
  `dash/web/vite.config.ts:15-18` writes to `dash/dist/dash`.
- `release.yml:324-335` embeds two SEA assets: `main.js` and `package.json`.
  `release.yml:374-379` archives only `kyberdash${EXE}` and `THIRD_PARTY_NOTICES.md`. The
  issue quotes line 378 correctly.
- `dash/tsup.sea.config.ts:28-36` defines `import.meta.url` as
  `pathToFileURL(process.execPath)`. In the SEA, `HERE` (`dash/src/cli/web.ts:19`) is
  therefore the directory holding the binary, for example `~/.local/bin`.
  `resolveDashDir()` (`web.ts:25-35`) probes `KYBERDASH_DASH_DIR`, then `<bin-dir>/dash`, then
  `<bin-dir>/../../dist/dash`. A released install has none of them. `web.ts:139-143` then
  serves `NOT_BUILT_PAGE`, and `web.ts:200-202` prints the hint the issue quotes.
- Running `build:web` alone would not fix this. The output would stay in the runner's
  checkout, because neither the blob nor the archive carries it.
- `scripts/release-local.sh:253-274` and `:303` copy the same steps. They are the local copy
  of the job, which the update loop drives. `ReleaseTests.LocalKyberDashBuildMatchesTheReleaseJob`
  (`tests/KyberWeave.Tests/ReleaseTests.cs:587-634`) pins the shared strings between the job
  and the script.
- `scripts/install.sh:711` and `:719-733` extract the archive and install only the named
  binary. `SelfUpdater` (`src/KyberWeave.Cli/Update/SelfUpdater.cs:149-158`) stages one
  `kyberdash` binary. A sibling directory (D1-B) would need both changed. An embedded asset
  (D1-A) needs neither.
- The SEA can already read embedded assets. `dash/src/sea-shim.cjs:16,22,29` uses
  `require('node:sea').getAsset`. The ESM bundle imports `node:` builtins from its `data:` URL
  scope (`tsup.sea.config.ts:30-31`), so `import … from 'node:sea'` resolves there as well.
- CI does not build the web dashboard anywhere. A grep of `.github/workflows` for
  `build:web` and `dist/dash` returns nothing.

### Defect 2 — bare CLI path in tray.json: CONFIRMED

Root cause: `process.isSea` does not exist, so the SEA branch never runs. The fallback
`process.argv[1]` in a SEA is the unexpanded `argv[0]` the OS passed. For a shell `PATH`
lookup, that value is the bare name `kyberdash`.

- `dash/src/install/node-deps.ts:137-144` tests `typeof process.isSea === 'function'`, which is
  always false, and returns `process.argv[1] ?? process.execPath`. `nodeInstallDeps()`
  (`:116`) records that value, and `recordInstall` (`dash/src/install/menubar.ts:304-317`)
  writes it to `tray.json` as `kyberdashPath`.
- Node exposes `isSea()` on the `node:sea` module, added in v21.7.0 and v20.12.0. It does not
  expose it on `process`, and the process API documents no `isSea`. The issue guessed
  `node:module`. That is wrong: the correct module is `node:sea`. The build pins Node 24.21.0
  (`release.yml:247`, `dash/.nvmrc:1`).
- In a SEA, Node's `FixupArgsForSEA` repeats `argv[0]` at position 1 as a stand-in for the
  missing entry-point path. The embedding entry point then deliberately does not expand
  `process.argv[1]`. `process.execPath` is an absolute path with symlinks resolved.
- Impact is narrower than the issue implies. `install.sh --with-menubar` runs
  `"${1%/}/kyberdash" menubar --force` (`install.sh:382`). `SelfUpdater` runs the absolute
  `kyberdash` path (`SelfUpdater.cs:300-305`). Both pass an absolute `argv[0]`, so they record
  an absolute path even today. The bare value comes from a user typing `kyberdash menubar` in
  a shell. This is inferred and listed under [Gaps](#gaps).
- The tray does not trust the bare value. The absolute-and-file check is in `resolve_from`
  (`dash/tray/src-tauri/src/cli.rs:192`), not in `recorded_cli_path` (`cli.rs:439-447`) as the
  issue says. A rejected record falls through to `~/.local/bin/kyberdash` (`cli.rs:181-201`,
  `:424`) and then to `PATH` (`cli.rs:203-213`). So an `install.sh` install into the default
  directory keeps working. An install elsewhere, whose directory is not on the login `PATH`,
  loses the CLI. **No tray change is needed for this issue**, so `tauri-dev` has no task unless
  D2-C is chosen.
- An already-installed tray short-circuits `kyberdash menubar` (`menubar.ts:81-84`) without
  rewriting the record. `--update` and `--force` rewrite it (D3).

### Defect 3 — stale bridge view: PARTIALLY CONFIRMED

Root cause: the bridge remembers that there is no database. When `canon.db` does not exist as
the web server starts, `KyberBridge.openDb` returns `undefined` and nothing ever tries again.
The tray always starts the web server before its first refresh. The issue's read-only
stale-snapshot explanation is not the mechanism.

- `dash/src/server/bridge.ts:576-579` returns `undefined` when the file is absent. The
  constructor (`:555-574`) calls it once, and `canonDb` is never reassigned except by `close()`
  (`:609-614`). Every query guards on `hasTable(this.canonDb, …)` (`:594-604`), so an absent
  handle yields empty lists for the life of the process. `this.canonDb` is referenced 70 times
  in `bridge.ts`.
- `/api/kyber/report` harnesses come from `bridge.listHarnessRollups()`
  (`dash/src/analysis/report/build.ts:307-311`, `bridge.ts:1796-1810`). That call returns `[]`
  while the handle is absent.
- `runWebDashboard` builds one bridge per server (`dash/src/cli/web.ts:109`). Only `web.ts` and
  `cli/report.ts:166` construct bridges, and `report.ts` injects a handle it opened after an
  existence check.
- Ordering makes this deterministic on a fresh machine, not a race. `Runtime::start`
  (`dash/tray/src-tauri/src/runtime.rs:205-223`) calls `ensure_server()`, which spawns
  `kyberdash web --no-open` (`supervisor.rs:129`), at line 216. It calls `refresh_startup()`,
  which runs `kyberdash dash refresh` (`scheduler.rs:28`), at line 218. The refresh's
  `CanonStore` constructor is what creates the file (`dash/src/canon/store.ts:925-933`).
- The tray re-polls the report every 15 s while the popover is open and every 60 s otherwise
  (`dash/tray/src-tauri/src/lib.rs:331-339`, `api.rs:30`). A fixed bridge is therefore picked
  up with no tray change.
- The read-only snapshot hypothesis was checked against the code:
  - The bridge uses only `prepare().all()` and `.get()`, never `.iterate()`. `.iterate()`
    appears only in `store.ts:1536,1539,2291`, on the writer. So the bridge leaves no cursor
    open between requests.
  - Node's `StatementSync.all()` and `.get()` reset the statement on entry (commit
    `851228cd60`).
  - A Deno report records that `get()` on a multi-row SELECT leaves no lock in Node.
  - No code renames or unlinks `canon.db`. A grep of `dash/src` for
    `rename`/`unlink`/`rm` finds only cache and lock files.
  - A missing `-wal` or `-shm` file is not the cause either. The bridge never opens at all,
    and the writer creates the file in WAL mode (`store.ts:933`).
- The issue's "before the background refresh finishes" is therefore imprecise. The stale
  window is "before `canon.db` exists". A server started after the file exists sees later
  commits. T3 pins that claim with a characterization test, because the SQLite primary
  sources could not be fetched from this environment.

### Documentation state

- `docs/dash/runbook.md:215-218` says the deployed CLI is staged at
  `~/Library/Application Support/io.github.dpalfery.kyberdash/local-bin/kyberdash`. That path
  appears only in an archived plan's manual deployment. The released path is `install.sh`'s
  `~/.local/bin/kyberdash`. T11 corrects it, because it describes `kyberdashPath`.
- `docs/dash/runbook.md:47-48` lists `kyberdash web` as part of the released binary but never
  says where its UI comes from.
- `docs/distribution.md:273-280` ("KyberDash in the loop") describes the build and the values
  `LocalKyberDashBuildMatchesTheReleaseJob` pins.
- `docs/dash/architecture.md`'s Surface Layer section describes the server, the bridge, and
  the tray's CLI resolution.
- `docs/install.md:91` describes the archive naming. It needs no change under D1-A.

### External sources (self-gathered)

| Fact | Source |
|---|---|
| `sea.isSea()`, `sea.getAsset()`, `getAssetAsBlob`, `getRawAsset` added v21.7.0 / v20.12.0. `sea.getAssetKeys()` added v24.8.0. `assets` config semantics. SEA main runs as CommonJS only. `__filename` equals `process.execPath` in the injected main. | <https://nodejs.org/docs/latest-v24.x/api/single-executable-applications.html> |
| `process.argv[0]` is `process.execPath`. `process.argv0` is the original `argv[0]`. `process.execPath` is absolute with symlinks resolved. No `process.isSea` is documented. | <https://nodejs.org/docs/latest-v24.x/api/process.html> |
| `FixupArgsForSEA` comment: "Repeats argv[0] at position 1 on argv as a replacement for the missing entry point file path." | <https://github.com/nodejs/node/blob/v24.21.0/src/node_sea.cc> |
| The SEA entry calls `prepareMainThreadExecution(false, true)` with the comment "Don't expand process.argv[1] because in a single-executable application … the user main script isn't necessarily provided via the command line." | <https://github.com/nodejs/node/blob/v24.21.0/lib/internal/main/embedding.js> |
| `DatabaseSync` `readOnly`: "If the database does not exist, opening it will fail." `node:sqlite` is Stability 1.2 (release candidate) since v24.15.0. | <https://nodejs.org/docs/latest-v24.x/api/sqlite.html> |
| `StatementSync::All` and `::Get` reset the statement on entry (`stmt->ResetStatement()`). | <https://github.com/nodejs/node/commit/851228cd60> |
| Secondary: Node's `stmt.get()` on a multi-row SELECT does not leave the database locked (contrasted with Deno). | <https://github.com/denoland/deno/issues/28295> |
| Cited but **not fetched** (egress blocked): WAL snapshot isolation, implicit transactions ending when the last statement finishes, read-only WAL opening. | <https://www.sqlite.org/isolation.html>, <https://www.sqlite.org/lang_transaction.html>, <https://www.sqlite.org/wal.html> |

## Design (recommended options)

**Shared SEA seam.** A new module, `dash/src/sea.ts`, is the only importer of `node:sea`. It
uses a static ESM import so a test can mock the builtin. It exports `runningAsSea()` and
`embeddedTextAsset(key)`. The second returns `undefined` when the process is not a SEA or the
key is absent, because `getAsset` throws on a missing key.

**Defect 2.** `resolveKyberdashPath()` returns `process.execPath` when `runningAsSea()` is
true. Otherwise it returns `path.resolve(process.argv[1])`, falling back to
`process.execPath`. The result is always absolute.

**Defect 1 (D1-A).**

- The release build runs `npm --prefix web ci --no-audit --no-fund` and
  `npm --prefix web run build` after tsup.
- It then packs `dist/dash` with `node scripts/pack-sea-web.mjs dist/dash dist-sea/web.json`
  and adds `"web.json"` to the SEA `assets` map.
- The asset format is `{"format":"kyberdash-web/1","files":{"<posix relative path>":"<base64>"}}`,
  with keys sorted so the blob is reproducible. The packer fails when `index.html` is missing.
- `web.ts` replaces `resolveDashDir()` with a static-source resolution. The order is
  `KYBERDASH_DASH_DIR` (an explicit override still wins), then the embedded `web.json` when
  running as a SEA, then the two existing directory candidates.
- The embedded source is parsed once, at server start. It serves by normalized POSIX key with
  the existing content types and SPA fallback. A traversal attempt can only miss the map and
  fall back to `index.html`.
- `index.html` still goes through `applyHtmlBrand`.
- A SEA binary with no embedded dashboard gets a page and hint that do not tell the user to
  run `npm` in a source tree.
- A smoke script, `dash/scripts/sea-web-smoke.mjs <binary>`, starts
  `<binary> web --no-open --port 0` with a temporary `HOME` and a non-existent
  `KYBER_CANON_DB`. It reads the `kyberdash.web.listening` line and fetches `/`. It fails
  unless the body is the SPA index rather than the not-built page. It always kills the child,
  and it is bounded to 20 s.
- `release-local.sh` runs the smoke after its `--version` check. `release.yml` runs it on
  native RIDs beside the existing `--version` smoke.

**Defect 3 (D2-A).**

- `KyberBridge` gets a private accessor that opens `canonPath` read-only on first use once the
  file exists.
- The accessor applies only when the bridge owns the path, meaning no injected `canonDb` and
  not `:memory:`. It never reopens after `close()`.
- A failed open warns once per bridge instance. An absent file is silent.
- All 70 `this.canonDb` reads go through the accessor, captured once per method. The injected
  `store` and `canonDb` paths are unchanged.

## Test contract

`development-mode: test-first`. Vitest commands run from `dash/`. A RED run must fail for the
stated reason before the paired GREEN task edits product code.

**`node:sea` mocking.** T1 and T5 mock the `node:sea` builtin with `vi.mock('node:sea', …)`.
If Vitest cannot intercept that builtin, `test-dev` may mock the `dash/src/sea.ts` seam
instead. In that case the RED failure is the missing seam module, and the record must say so.
This alternative is pre-approved as part of this contract. It is not a weakening.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/install/node-deps.test.ts` (new) | `cd dash && npx vitest run src/install/node-deps.test.ts` | `nodeInstallDeps().kyberdashPath`: (a) with `isSea()` true and `process.argv[1] = 'kyberdash'`, equals `process.execPath`; (b) with `isSea()` true and `argv[1] = './kyberdash'`, equals `process.execPath`; (c) with `isSea()` false and an absolute `argv[1]`, equals that path; (d) is `path.isAbsolute` in every case. `process.argv` is restored after each case. | (a) and (b) fail on the unchanged `node-deps.ts` with the received value `'kyberdash'` or `'./kyberdash'`, because `process.isSea` is undefined. | Same file passes. Assertions unchanged. |
| T2 | as T1 | as T1 | T2 implements T1's behavior (`dash/src/sea.ts`, `resolveKyberdashPath`). | T1's RED run, recorded before `node-deps.ts` is edited. | T1 GREEN. `npm --prefix dash run typecheck`, `lint`, `test`, and `check:reachable` exit 0. |
| T3 | `dash/src/server/bridge-late-store.test.ts` (new) | `cd dash && npx vitest run src/server/bridge-late-store.test.ts` | Temp directory, own `canonPath`. (a) A `KyberBridge` constructed while `canon.db` is absent returns `[]` from `listHarnessRollups()` and `listSessions()`. After a separate `CanonStore(canonPath)` performs `upsertHarnessRollup` and `upsertSession`, the same bridge returns them. (b) `handleKyberRequest` `GET /api/kyber/report`, driven through the `call` pattern of `report-route.test.ts` with that real bridge, returns `harnesses: []` before the store exists and a non-empty `harnesses` array containing the upserted harness after. (c) After `bridge.close()`, the bridge returns `[]` and does not reopen even though the file exists. (d) Characterization: a bridge constructed after `canon.db` exists, which has already answered one query, sees rows a separate `CanonStore` commits afterwards. | (a) and (b) fail on the unchanged `bridge.ts`, because the post-store assertions receive `[]`. (c) passes or fails, whichever it does. (d) **must pass** on the unchanged tree. If (d) fails, the stale-snapshot mechanism is real too. Stop: the plan returns to Draft. | All four pass. The existing `kyber-bridge.test.ts` "returns empty structures when files do not exist" stays green. |
| T4 | as T3 | as T3, then `cd dash && npx vitest run src/server` | T4 implements T3's behavior in `KyberBridge`. | T3's RED run, recorded before `bridge.ts` is edited. | T3 GREEN. All `src/server` tests pass. The four dash gates exit 0. |
| T5 | `dash/src/cli/web-embedded.test.ts` (new); `dash/scripts/pack-sea-web.test.ts` (new); `dash/scripts/sea-web-smoke.test.ts` (new) | `cd dash && npx vitest run src/cli/web-embedded.test.ts scripts/pack-sea-web.test.ts scripts/sea-web-smoke.test.ts` | **web-embedded.** `isSea()` is true, `KYBERDASH_DASH_DIR` is unset, and `getAsset('web.json', 'utf8')` returns a `kyberdash-web/1` fixture. The fixture holds an `index.html` with a unique sentinel and `assets/app-abc123.js`. `runWebDashboard({ port: 0, open: false, kyberBridge: stub })`: `GET /` returns 200 HTML with the sentinel and the brand applied. `GET /assets/app-abc123.js` returns the JS body with a JavaScript content type. `GET /deep/client/route` returns the index. `GET /..%2f..%2fpackage.json` returns the index and never another file. With `isSea()` true and no `web.json`, the stdout hint contains no `npm install`. With `KYBERDASH_DASH_DIR` set to a directory index, that index wins. **pack-sea-web.** `packWebDirectory(dir)` emits the `kyberdash-web/1` shape with sorted POSIX keys. Decoding round-trips every byte. The CLI exits non-zero without `index.html`. **sea-web-smoke.** Run against a stand-in "binary" (a Node script that prints the listening line and serves the not-built page), it exits non-zero naming the not-built page. Run against a stand-in serving an SPA index, it exits 0. Against one that never prints the line, it exits non-zero within its bound. | web-embedded fails on the unchanged `web.ts`: `GET /` returns the not-built page or an unrelated local `dist/dash` index, without the sentinel. The other two fail on import, because `scripts/pack-sea-web.mjs` and `scripts/sea-web-smoke.mjs` do not exist. | All three files pass. Assertions unchanged. |
| T6 | as T5 | as T5, then `cd dash && npx vitest run src/cli` | T6 implements T5's behavior (`web.ts`, both scripts). It consumes `dash/src/sea.ts` from T2. | T5's RED run, recorded before `web.ts` or `dash/scripts/` is edited. | T5 GREEN. `web-dashboard.test.ts` and `web-listening.test.ts` stay green. The four dash gates exit 0. |
| T7 | `tests/KyberWeave.Tests/ReleaseTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests` | **Existing fact extended.** `LocalKyberDashBuildMatchesTheReleaseJob`'s shared list adds `npm --prefix web ci`, `npm --prefix web run build`, `scripts/pack-sea-web.mjs`, `"web.json":`, and `scripts/sea-web-smoke.mjs`. **New fact** `BuildKyberDashEmbedsTheWebDashboard`. It checks both the job and `release-local.sh`. The first `tsup --config tsup.sea.config.ts` precedes `npm --prefix web run build`, which precedes `scripts/pack-sea-web.mjs`, which precedes `--experimental-sea-config`. `scripts/sea-web-smoke.mjs` appears after the `NODE_SEA_BLOB` injection. The archive commands still name exactly `kyberdash` and `THIRD_PARTY_NOTICES.md`: the archive contract that `install.sh` and `SelfUpdater` rely on is unchanged. | Both facts fail on the unchanged `release.yml` and `release-local.sh`, naming the first missing value. Pre-existing `ReleaseTests` facts still pass. | The same filter passes after T8 and T9. No assertion is removed or loosened. |
| T8 | as T7 | as T7 | GREEN half of T7 for `.github/workflows/release.yml`. | T7's RED run. | T7's job-side assertions pass. A workflow lint, if the conductor's gate set has one, passes. |
| T9 | as T7 | as T7 | GREEN half of T7 for `scripts/release-local.sh`. | T7's RED run. **Integration RED:** after T6 and before T9, `./scripts/release-local.sh` builds a binary. Running `node dash/scripts/sea-web-smoke.mjs <that binary>` fails naming the not-built page. Record the output. | T7 GREEN, jointly with T8. The smoke passes inside `release-local.sh`. |
| T10 | No new unit test. Integration: `scripts/update-loop.sh` | `./scripts/update-loop.sh` | The loop builds `kyberdash` through `release-local.sh`, which now builds, embeds, and smoke-tests the dashboard. Every existing case still passes: `kyberdash-replaced`, `kyberdash-opt-out`, `tray-opt-out`, `tray-delegated`, `kyberdash-floor`, `recovery-manifest`, `recovery-cache`, and the self-update and Squad cases. | No separate RED. T9's integration RED is the evidence. | Exit 0. The log shows the smoke passing for the built binary. Needs `node` and `npm` on `PATH`. |
| T11 | No product test | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | The canonical pages describe the shipped behavior (see [Documentation impact](#documentation-impact)). | No product RED. | Both commands exit 0 with zero findings. `docs validate . --merge-ready` stays expected to fail with `KW-DOC-LIFECYCLE-003` until T12. |
| T12 | No test. Closeout. | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | This plan is archived. The index no longer lists it as active. | No RED. | Both exit 0. |

Changing an assertion in this table, including weakening one to reach green, returns the plan
to Draft and needs the conductor to relay reapproval.

## Tasks

No skill in the inventory is scoped to Node TypeScript under `dash/src` or to POSIX shell.
`react-dev` is scoped to `.tsx`/`.jsx` UI, and none of this plan's product code is React. The
conductor assigns those tasks directly, as the exemplar plans did for shell. `test-dev` owns
every RED task.

### T1 — RED: absolute CLI path from a SEA

- **Objective:** Add the T1 contract tests before any edit to `node-deps.ts`.
- **Files:** `dash/src/install/node-deps.test.ts` (new).
- **Acceptance:** Test-contract row T1.
- **Depends on:** none. Needs D3 answered only if D3-B is chosen.
- **Owner / skill:** `test-dev`.

### T2 — GREEN: shared SEA seam and `resolveKyberdashPath`

- **Objective:** Create `dash/src/sea.ts` with `runningAsSea()` and `embeddedTextAsset(key)`,
  using a static `node:sea` import. Make `resolveKyberdashPath()` (`node-deps.ts:137-144`) use
  it and return an absolute path. If `@types/node` lacks a `node:sea` declaration, add a
  minimal ambient declaration beside `sea.ts`.
- **Files:** `dash/src/sea.ts` (new), `dash/src/install/node-deps.ts`.
- **Acceptance:** Test-contract row T2. `tray.json` writers are unchanged (`menubar.ts`
  untouched unless D3-B).
- **Depends on:** T1 RED evidence.
- **Owner / skill:** conductor-assigned TypeScript. `test-dev` re-runs T1.

### T3 — RED: bridge sees a store created after it

- **Objective:** Add the T3 contract tests, including characterization case (d).
- **Files:** `dash/src/server/bridge-late-store.test.ts` (new). Reads `CanonStore` and
  `handleKyberRequest`. Edits no existing test file.
- **Acceptance:** Test-contract row T3. If (d) fails, stop and return the plan to Draft.
- **Depends on:** D2 answered.
- **Owner / skill:** `test-dev`.

### T4 — GREEN: lazy read-only open in `KyberBridge`

- **Objective:** Add the private store accessor and a `closed` flag. Route every
  `this.canonDb` read through the accessor, captured once per method. Warn once on a failed
  open. Do not change public method signatures.
- **Files:** `dash/src/server/bridge.ts` only.
- **Acceptance:** Test-contract row T4. `kyber-bridge.test.ts`, `kyber-api.test.ts`,
  `report-route.test.ts`, and `report-api-parity.test.ts` stay green.
- **Depends on:** T3 RED evidence.
- **Owner / skill:** conductor-assigned TypeScript. `test-dev` re-runs T3.

### T5 — RED: embedded dashboard, packer, and smoke

- **Objective:** Add the three T5 contract test files.
- **Files:** `dash/src/cli/web-embedded.test.ts`, `dash/scripts/pack-sea-web.test.ts`,
  `dash/scripts/sea-web-smoke.test.ts` (all new).
- **Acceptance:** Test-contract row T5.
- **Depends on:** D1 answered (A).
- **Owner / skill:** `test-dev`.

### T6 — GREEN: serve the embedded dashboard

- **Objective:** Implement the static-source resolution and embedded source in `web.ts`, with
  the SEA-aware not-built page and hint. Add `dash/scripts/pack-sea-web.mjs`, exporting
  `packWebDirectory`, and `dash/scripts/sea-web-smoke.mjs`.
- **Files:** `dash/src/cli/web.ts`, `dash/scripts/pack-sea-web.mjs` (new),
  `dash/scripts/sea-web-smoke.mjs` (new). Consumes `dash/src/sea.ts` without editing it.
- **Acceptance:** Test-contract row T6. The loopback and origin guards in `runWebDashboard`
  are unchanged.
- **Depends on:** T5 RED evidence, and T2 (`dash/src/sea.ts`).
- **Owner / skill:** conductor-assigned TypeScript. `test-dev` re-runs T5.

### T7 — RED: the release job embeds the dashboard

- **Objective:** Extend `LocalKyberDashBuildMatchesTheReleaseJob` and add
  `BuildKyberDashEmbedsTheWebDashboard` per the Test contract.
- **Files:** `tests/KyberWeave.Tests/ReleaseTests.cs` only.
- **Acceptance:** Test-contract row T7. Existing `ReleaseTests` stay green.
- **Depends on:** D1 answered (A).
- **Owner / skill:** `test-dev`.

### T8 — GREEN: release.yml builds, embeds, and smoke-tests the dashboard

- **Objective:** In `build-kyberdash`, add the web build and pack commands to the
  "Bundle dash CLI" step after tsup, and fail if `dist-sea/web.json` is missing. Add
  `"web.json": "${BUNDLE_DIR}/web.json"` to the SEA config `assets`. Run
  `node scripts/sea-web-smoke.mjs "${FINAL_BIN}"` inside the existing native-RID guard. Keep
  the archive commands unchanged, and update the job's comments.
- **Files:** `.github/workflows/release.yml` only.
- **Acceptance:** Test-contract row T8. Action pins are unchanged, so
  `ReleaseTests`' commit-SHA pin fact still passes.
- **Depends on:** T7 RED evidence, and T6 (the scripts it calls).
- **Owner / skill:** `github-devops`.

### T9 — GREEN: release-local.sh mirrors the job

- **Objective:** Make the same three changes in `build_kyberdash`. Inside the `cd "$dash"`
  subshell, run the web build and pack through `quietly`. Add `"web.json"` to the heredoc. Run
  the smoke after the `--version` check. Capture the integration RED first (Test-contract T9).
- **Files:** `scripts/release-local.sh` only. `scripts/update-loop.sh` is unchanged.
- **Acceptance:** Test-contract row T9. The `recovery-manifest` and `recovery-cache` cases are
  unaffected, because the stand-in `npx` still fails at tsup before the web build.
- **Depends on:** T7 RED evidence, and T6.
- **Owner / skill:** conductor-assigned shell. `test-dev` re-runs T7.

### T10 — Release-loop verification

- **Objective:** Run the local release loop on the integrated tree.
- **Files:** none edited.
- **Acceptance:** Test-contract row T10.
- **Depends on:** T2, T4, T8, T9.
- **Owner / skill:** conductor-assigned. The runner needs `node`, `npm`, `curl`, and the
  .NET SDK.

### T11 — Canonical documentation

- **Objective:** Update the pages in [Documentation impact](#documentation-impact) to
  describe shipped behavior.
- **Files:** `docs/dash/runbook.md`, `docs/distribution.md`, `docs/dash/architecture.md`.
  Check `docs/install.md` and leave it unchanged unless it contradicts D1-A.
- **Acceptance:** Test-contract row T11.
- **Depends on:** T10.
- **Owner / skill:** `docs-dev` (`kyber-weave-docs` skill).

### T12 — docs-dev closeout

- **Objective:** After review approval, record evidence, archive this plan under
  `docs/archive/plans/`, and move its index row to Archived Plans. Harvest any durable rule
  that T11 did not already carry. No ADR is expected under D1-A.
- **Files:** this file, `docs/plans/README.md`.
- **Acceptance:** Test-contract row T12.
- **Depends on:** review approval, T11.
- **Owner / skill:** `docs-dev` (`kyber-weave-docs` skill).

## Dependency graph and MAX_CONCURRENCY

```text
T1 ─> T2 ──────────────┐
T3 ─> T4 ──────────────┤
T5 ─┐                  ├─> T10 ─> T11 ─> review ─> T12
    ├─> T6 ─┬─> T8 ────┤
T2 ─┘       └─> T9 ────┘
T7 ───────────> T8, T9
```

`MAX_CONCURRENCY: 4`

Audit:

- T1, T3, T5, and T7 are RED tasks. They write disjoint new or single files and consume
  nothing, so they may run together once D1–D3 are answered. That wave is the peak of 4.
- T2 and T4 then run in parallel. `node-deps.ts` and `sea.ts` are disjoint from `bridge.ts`.
- T6 waits for T2 because it imports the `sea.ts` that T2 creates.
- T8 and T9 run in parallel. `release.yml` and `release-local.sh` are disjoint. Both call the
  scripts T6 creates, and both satisfy T7.
- T10 needs every product change. T11 documents what T10 proved. Review follows, and T12
  closes out.
- Each file has exactly one writer across the plan.
- No `dash/tray/**` file and no `install.sh` or C# updater file is written.

## Acceptance criteria

1. A `kyberdash` built by `release-local.sh`, and by `release.yml` on native RIDs, answers
   `GET /` from `kyberdash web` with the built SPA index rather than the not-built page. Its
   hashed assets are served with correct content types.
2. `kyberdash menubar` run from a SEA records `kyberdashPath` as the absolute
   `process.execPath`, whether the binary was invoked by bare name, by relative path, or by
   absolute path.
3. A `kyberdash web` server started before `canon.db` exists returns non-empty `harnesses` from
   `GET /api/kyber/report` once a refresh has written rollups, without a restart. The tray
   shows them on its next poll (at most 60 s).
4. `kyberdash-<rid>` archive contents, `scripts/install.sh`, `SelfUpdater`, and every
   `dash/tray/**` file are unchanged (D1-A, D2-A).
5. Every gate in [Verification gates](#verification-gates) passes.

## Verification gates

Before review, in addition to each Test-contract command:

- `npm --prefix dash run typecheck`
- `npm --prefix dash run lint`
- `npm --prefix dash run test`
- `npm --prefix dash run check:reachable`
- `dotnet build KyberWeave.sln -c Release` and
  `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests`.
  `ReleaseTests.cs` changes, so the whitespace and style `dotnet format` gates apply too.
- `./scripts/update-loop.sh`. It is required because this plan changes how `kyberdash` is
  built. See [distribution](../distribution.md#verifying-a-release-locally) and
  [KyberDash in the loop](../distribution.md#kyberdash-in-the-loop).
- `dotnet run --project src/KyberWeave.Cli -- docs validate .`
- `dotnet run --project src/KyberWeave.Cli -- docs drift .`

Tray gates (`npm --prefix dash/tray/ui run typecheck`, `npm --prefix dash/tray/ui run test`,
and `cargo fmt --check && cargo clippy -- -D warnings && cargo test` in
`dash/tray/src-tauri`) are **not required**, because no tray file changes. They become required
if D2-C is chosen.

The `install.sh` `ReleaseTests` install facts run inside the `ReleaseTests` filter above. They
must stay green, but this plan adds none. The repository gate suite (`review gates`) runs at
review.

## Documentation impact

| Page | Change |
|---|---|
| [dash/runbook.md](../dash/runbook.md) | "Installing the released binary": the web dashboard is embedded in the released binary. `kyberdash web` needs no `npm`, and `KYBERDASH_DASH_DIR` still overrides it. The tray section: replace the `local-bin` staging sentence with the shipped rule, which is that `kyberdashPath` records the absolute path of the `kyberdash` that ran `menubar` (typically `~/.local/bin/kyberdash`). State that `kyber-weave update` and `kyberdash menubar --force` rewrite an older record. Troubleshooting: a web server started before the first refresh now picks up the store. |
| [distribution.md](../distribution.md) | "KyberDash in the loop": `release-local.sh` and the job build the web dashboard, pack it as the `web.json` SEA asset, and smoke-test `kyberdash web`. `LocalKyberDashBuildMatchesTheReleaseJob` pins those values too, and `BuildKyberDashEmbedsTheWebDashboard` pins the order and the unchanged archive contents. |
| [dash/architecture.md](../dash/architecture.md) | Surface Layer: in a release, the web server serves the SPA from the embedded SEA asset. In a checkout it serves `dist/dash`. `KyberBridge` opens `canon.db` read-only on first use once the file exists. |
| [install.md](../install.md) | None expected under D1-A. The archive layout does not change. T11 confirms this. |
| ADR | None expected under D1-A. [ADR 0020](../adr/0020-kyberdash-one-time-fork.md) decision 4 ("self-contained Node SEA binaries") stays true. D1-B would need an ADR. |

## Risks

- **Blob size.** The embedded SPA grows the blob by its base64 size, roughly 4/3 of the built
  bundle. That bundle was not built here (see Gaps). Against a Node binary of roughly 100 MB
  the growth is small, but it is not measured.
- **Web build in the release path.** The job and the loop now also need `dash/web`'s
  `npm ci` and Vite build to succeed on every runner. `win-x64` is built on Ubuntu, and the
  web build is platform-neutral.
- **Mocking `node:sea`.** Whether Vitest can mock the builtin is unverified. The contract
  pre-approves mocking the seam instead.
- **The `node:sea` import.** A top-level `import … from 'node:sea'` must resolve in the
  regular `dist/cli.js` build too. `node:sea` exists on every Node the `engines` floor
  (`>=22.13.0`) allows.
- **`process.execPath` resolves symlinks.** A symlinked install records the link target. This
  is unchanged in kind from other installers, and it is out of scope.
- **Warn-once on a failed open.** It hides repeated failures after the first. The first
  failure is still logged with its cause.
- **Merge gate.** `docs validate . --merge-ready` fails with `KW-DOC-LIFECYCLE-003` while this
  file sits in `docs/plans/`. That is the merge gate, not a defect of this Draft.

## Out of scope (follow-ups, not authorized)

- Reopening `canon.db` after it is replaced (D2-B) if D2-A is chosen.
- Tray startup ordering (web before refresh) and any `dash/tray/**` change.
- The developer script `build:web`, which uses `npm install` and so can rewrite
  `dash/web/package-lock.json`. The release path uses `npm ci`.
- Cross-RID SEA smoke tests. `win-x64` and cross-built macOS or Linux binaries remain
  unexecuted, as today.
- Logging when the tray rejects a recorded `kyberdashPath`. `resolve_from` already lists the
  probed paths in the setup state.
- Recording a symlink path rather than its resolved target.

Each item becomes a todo only if the user accepts it.

## Gaps

1. The SQLite primary sources (isolation, implicit transactions, read-only WAL) could not be
   fetched, because egress to `sqlite.org` is blocked. T3 case (d) pins the relied-on behavior
   instead.
2. Whether Node v24.21.0's `StatementSync.get()` also resets the statement on exit was not seen
   in source, because the fetch truncated. The evidence is secondary (Deno #28295) plus
   T3(d).
3. Whether `@types/node@^22.19.17` declares `node:sea` is unknown, because `node_modules` is
   not installed in this checkout. T2 adds an ambient declaration if needed.
4. Whether Vitest's `vi.mock` intercepts the `node:sea` builtin is unknown. The contract
   pre-approves the seam alternative.
5. The size of the built SPA is unknown, because `dash/dist/dash` is absent here.
6. That `dash/web/package-lock.json` passes `npm ci` on the release runners was not run here.
7. That the reporter's bare `kyberdashPath` came from a shell `kyberdash menubar` is inferred.
   `install.sh --with-menubar` and `SelfUpdater` pass absolute paths.
8. `docs validate` and `docs drift` were not run for this Draft. See the handoff.

## Review

Review follows the repository review council after T1–T10 are green. The reviewer checks:

- **Defect 2.** `node:sea`'s `isSea()` is the SEA test and the recorded path is absolute.
- **Defect 1.**
  - The embedded source cannot serve anything outside its map.
  - `KYBERDASH_DASH_DIR` still wins.
  - The archive still holds only the binary and notices.
  - `release.yml` and `release-local.sh` stay in parity.
- **Defect 3.**
  - The bridge never reopens after `close()`.
  - It never writes, because the handle is read-only.
  - The injected-handle path used by `kyberdash report` is unchanged.
  - T3(d) passed before T4.

## docs-dev closeout

T12 is the closeout. `docs-dev`, using `kyber-weave-docs`:

1. Verifies the Test-contract evidence.
2. Confirms the three canonical pages carry the shipped rules.
3. Archives this plan under `docs/archive/plans/` and updates [the plan index](README.md) so the
   Active Plans section no longer links it.
4. Re-runs `docs validate . --merge-ready` and `docs drift .`.
