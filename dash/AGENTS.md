# Working under `dash/`

KyberDash is first-party code ([ADR 0020](../../docs/adr/0020-kyberdash-one-time-fork.md)).
Judge every edit here on its merits, under the repository's own gates. Do not merge from
CodeBurn or any other upstream; the only trace of it left is `dash/LICENSE` and
`dash/THIRD_PARTY_NOTICES.md`.

## THE RULE

> **THE TRAY AND THE WEB DASHBOARD ARE DISPLAY LAYERS. NEITHER CONTAINS ISOLATED FEATURE
> LOGIC, SCHEDULERS OR JOBS. ALL LOGIC AND JOBS (REFRESH SCHEDULING, CLEAN, IMPORT, PAUSE)
> LIVE IN THE SHARED KYBERDASH APPLICATION (THE CLI ENGINE AND THE `kyberdash web` SERVER
> BUILT FROM IT) THAT BOTH SURFACES SHARE. BOTH SURFACES USE ONE DATASTORE (canon.db,
> resolved by dash/src/canon/paths.ts resolveCanonDbPath) AND ONE API LAYER
> (/api/kyber/*). NO AGENT OR DESIGN MAY VIOLATE THIS.**

Full text, with the boundary cases and the agent checklist:
[`docs/rules/kyberdash-display-layer.md`](../../docs/rules/kyberdash-display-layer.md).
Decided by [ADR 0033](../../docs/adr/0033-kyberdash-surfaces-are-display-layers.md).
Architecture: [`docs/dash/architecture.md`](../../docs/dash/architecture.md).

## What lives where

| Directory | Role |
|---|---|
| `dash/src/**` | The engine: parsers, the canonical store and projection, analyses, refresh and import, clean, the jobs hosts, the OTLP receiver, the server, the CLI commands |
| `dash/web/**` | A display layer. Renders and requests over `/api/kyber/*`; holds no logic |
| `dash/tray/**` | A display layer. A Tauri 2 Rust shell plus a React popover; owns pixels and lifecycle, not behaviour |

Feature behaviour belongs in `dash/src`, reached over the API. A surface may render, invoke
actions through `/api/kyber/*`, open links, and keep display preferences. It must never
schedule, run a job, spawn a CLI child for one, host the receiver, take a non-allowlisted
value import from `dash/src`, or compute a domain result (cost, coverage, ranking) in the
browser. `dash/src/architecture/display-layer.test.ts` scans for exactly those and fails
with the offending file, line and construct.

## Gates

None of the .NET gates in the root `AGENTS.md` reach this tree. From the repository root, run
each through the process watchdog:

```bash
scripts/proc-guard.sh run -- npm --prefix dash run typecheck
scripts/proc-guard.sh run -- npm --prefix dash run lint
scripts/proc-guard.sh run -- npm --prefix dash run test
scripts/proc-guard.sh run -- npm --prefix dash run check:reachable
```

Editing `dash/tray/**` also means the tray's own gates — see
[`dash/tray/AGENTS.md`](tray/AGENTS.md) for the Rust and popover-UI commands. Run those through
`scripts/proc-guard.sh run --` too.

## Tests that start processes

A dashboard test that starts a dashboard can start another, and that is how a run fork-bombed
`node` in 2026-10. These rules hold for every test under `dash/`
([ADR 0047](../docs/adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint.md),
[test standard](../docs/standards/test/README.md#tests-that-start-processes)):

- **Playwright runs only with the isolated config**, from the repository root:
  `scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts <spec>`.
  Never run `dash/playwright.config.ts` by hand, and never omit `--config`.
- **Never start the dashboard to run a test** (`kyberdash web`, `npm run dev -- web`). The e2e
  boots use `dash/e2e/boot-dashboard.ts`, which injects inert spawners.
- **Spawns are fake by default.** Inject a recording `spawnFn` into `createNodeSpawner`,
  `DetachedLauncher` or the other self-spawn seams. Under test `KYBERDASH_SPAWN_GUARD=deny` refuses
  any spawn that gets through, and the test that caused it fails.
- **A test that needs a real child** calls `allowRealSpawns(reason)` from
  `dash/tests/setup/spawn-guard.ts`, with a reason that states the bound.
- **Every self-spawn goes through `guardedSpawn`** in `dash/src/process/spawn-guard.ts`. Do not
  import `child_process` spawn for a self-spawn anywhere else; `dash/tests/spawn-sites.test.ts`
  fails the build if you do.

**Adding a spawn site.** A new `child_process` spawn, exec or fork call, a `new Worker`, or a
Playwright `webServer` under `dash/` fails `dash/tests/spawn-sites.test.ts` until it has an entry
in `dash/tests/spawn-sites.allowlist.ts` with `file`, `pattern`, `bound` and `note`. The `bound`
must say how the site is limited: a timeout, a tree kill, a child closed on exit, an `afterEach`
kill, an injected fake, or a bounded external tool. A site with no limit is written "NO TIMEOUT"
and listed as a follow-up; that is a residual, not a bound. Add the entry in the same change as
the site. An entry covers one file and one API name, so a second site of that kind in the same
file still needs its own bound stated in review.
