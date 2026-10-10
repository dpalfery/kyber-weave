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

None of the .NET gates in the root `AGENTS.md` reach this tree. Run:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
npm --prefix dash run check:reachable
```

Editing `dash/tray/**` also means the tray's own gates — see
[`dash/tray/AGENTS.md`](tray/AGENTS.md) for the Rust and popover-UI commands.
