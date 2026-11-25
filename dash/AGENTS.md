# Working under `dash/`

KyberDash is first-party code ([ADR 0020](../../docs/adr/0020-kyberdash-one-time-fork.md)).
Judge every edit here on its merits, under the repository's own gates. Do not merge from
CodeBurn or any other upstream; the only trace of it left is `dash/LICENSE` and
`dash/THIRD_PARTY_NOTICES.md`.

## THE RULE

> **THE TRAY AND THE WEB DASHBOARD ARE DISPLAY LAYERS. NEITHER CONTAINS ISOLATED FEATURE
> LOGIC, SCHEDULERS OR JOBS. ALL LOGIC AND JOBS (REFRESH SCHEDULING, CLEAN, IMPORT, PAUSE)
> LIVE IN THE SHARED KYBERDASH C# APPLICATION (`dash/server/`, INCLUDING THE ENGINE, THE
> `kyberdash` CLI, AND THE `kyberdash web` SERVER) THAT BOTH SURFACES SHARE. BOTH SURFACES USE
> ONE DATASTORE (`canon.db`, resolved by `KyberDash.Domain.CanonDbPath.Resolve`) AND ONE API
> LAYER (`/api/kyber/*`). NO AGENT OR DESIGN MAY VIOLATE THIS.**

Full text, with the boundary cases and the agent checklist:
[`docs/rules/kyberdash-display-layer.md`](../../docs/rules/kyberdash-display-layer.md).
Decided by [ADR 0033](../../docs/adr/0033-kyberdash-surfaces-are-display-layers.md) and
[ADR 0084](../../docs/adr/0084-kyberdash-csharp-engine.md).
Architecture: [`docs/dash/architecture.md`](../../docs/dash/architecture.md).

## KyberDash never estimates

> KyberDash never estimates the data it collects and displays: a value the harness did not report is shown as not measured; the fix is capturing the real value. This rule applies to KyberDash (`dash/**` and `docs/dash/**`) only. One exception: token counts of text may use the model's own published tokenizer or, when the model publishes none, the closest standard local tokenizer, always labelled with the tokenizer used and whether it is the model's own; tokenization never calls an API. Every other estimate stays banned.

An estimate is any figure the harness did not report that is not exact arithmetic on reported
values: a split of a session total across calls or turns, a count from text length, a heuristic
floor, or an invented band. A cost computed from published rates is a calculation. Full text:
[`docs/rules/honest-unobservability.md`](../docs/rules/honest-unobservability.md#hard-rule-kyberdash-never-estimates).

## What lives where

| Directory | Role |
|---|---|
| `dash/server/**` | The C# Clean Architecture solution (`dash/server/KyberDash.sln`): the domain/contracts/data/application engine plus the `kyberdash` CLI, `kyberdash-api` HTTP/SSE host, and `kyberdash-mcp` stdio host — provider parsers, `CanonStoreRepository` and incremental projection, analyses, refresh and import, clean, in-process `JobSchedulerService`, OTLP receiver, and `/api/kyber/*` server |
| `dash/server.tests/**` | The engine's test projects (`KyberDash.Domain.Tests`, `KyberDash.Data.Tests`, `KyberDash.Application.Tests`, `KyberDash.Presentation.Tests`), including the architecture-boundary tests |
| `dash/web/**` | A display layer. Renders and requests over `/api/kyber/*`; holds no logic |
| `dash/tray/**` | A display layer. A Tauri 2 Rust shell plus a React popover; owns pixels and lifecycle, not behaviour |

Feature behaviour belongs in `dash/server/`, reached over `/api/kyber/*`. A surface may render,
invoke actions through `/api/kyber/*`, open links, and keep display preferences. It must never
schedule, run a job, spawn a CLI child for one, host the receiver, or compute a domain result
(cost, coverage, ranking) in the browser or tray. See `dash/server/**` and
`tests/KyberWeave.RepoPolicy.Tests/` for process spawn allow-listing and .NET gates.

## Gates

From the repository root, run the Node/UI gates and the .NET engine gates directly — there is no
process wrapper: KyberDash runs its jobs and OTLP receiver in-process, so nothing spawns children
that need reaping:

```bash
dotnet build dash/server/KyberDash.sln -c Release
dotnet test dash/server/KyberDash.sln -c Release   # add --filter "FullyQualifiedName~<Class>" for one suite
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test:related -- <files>
npm --prefix dash run check:reachable
```

`<files>` are relative to `dash/` (`web/src/foo.ts`, not `dash/web/src/foo.ts`). A repo-root path matches
nothing and passes with zero tests, so check the run reports a non-zero test count.

`npm --prefix dash run test` is the full `dash/` TypeScript test suite. The review council's `ts-test` gate and
CI run it; no dev agent does.

Editing `dash/tray/**` also means the tray's own gates — see
[`dash/tray/AGENTS.md`](tray/AGENTS.md) for the Rust and popover-UI commands. Run those directly
from the repository root too.

## Tests that start processes

These rules hold for every test under `dash/`
([ADR 0047](../docs/adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint.md),
[test standard](../docs/standards/test/README.md#tests-that-start-processes)):

- **Playwright runs only with the isolated config**, from the repository root:
  `npx --prefix dash playwright test --config dash/playwright.isolated.config.ts <spec>`.
  Never run `dash/playwright.config.ts` by hand, and never omit `--config`.
- **Never start the dashboard manually to run a test** (`kyberdash web`, `npm run dev`). The isolated
  e2e boots seed an isolated state directory via `E2eSeederService` in `dash/server/KyberDash.Application/Services/` and launch
  a loopback-bound `kyberdash-api` server over ephemeral ports, torn down in `finally`.
- **C# process spawns stay bounded and allowlisted.** Every self-spawn in `dash/server/**` uses a
  bounded `ProcessRunner` / `ProcessStartInfo` call governed by
  `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json` (`ProcessSpawnSitesTests`).
  Any new Node `child_process` spawn, `Worker`, or Playwright `webServer` under `dash/` must likewise
  use bounded timeouts and clean teardown.
