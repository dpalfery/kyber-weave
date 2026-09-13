---
id: archive/todo/analyzer-debt-ca1307-1308-1716
title: Fix pre-existing CA analyzer debt
doc-type: todo
component: CI Pipelines
status: archived
owner: dpalfery
last-reviewed: 2026-09-10
---

# Fix pre-existing CA analyzer debt

**Status:** Archived
**Archive Date:** 2026-09-10

## Closure

Both halves are resolved, and the second half was not the one this todo described.

The `src/KyberWeave.Core/` debt recorded below — 61 errors under
`TreatWarningsAsErrors` — was fixed separately; `src/` now builds at zero warnings
and zero errors under `AnalysisMode=all`.

What remained was in `tests/KyberWeave.Tests/`, and it was invisible because that
project carried `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` with no stated
reason. That exemption silenced nothing and enforced nothing: 272 analyzer diagnostics
printed on every build and none of them could fail one. Resolved by fixing what was
genuinely wrong and suppressing only what was not, each with a reason:

| Rule | Count | Disposition |
|---|---|---|
| CA1307 | 25 | Fixed — `StringComparison.Ordinal` added, matching the convention the suite already used |
| CA1308 | 11 | 1 fixed (extension matching became an `OrdinalIgnoreCase` comparison); 10 suppressed at the site — lowercase hex is the `SHA256SUMS.txt` wire format, and elsewhere the case variance is the subject under test |
| CA1707 | 192 | Suppressed project-wide — xUnit `Method_Condition_Expectation` naming; the rule targets public API identifiers and test methods are not API |
| CA1823 | 1 | Fixed — the dead `UnshippedUpstreamSurfaces` field became `windows` / `gnome` rows on the merge-boundary theory it had been staged for |
| CA1725 | 1 | Fixed — `FakeProcessExecutor.Run` parameter renamed to match `IProcessExecutor` |
| CA1711 | 1 | Suppressed at the site — xUnit `[CollectionDefinition]` classes are named `<Name>Collection` by convention |
| xUnit2031 | 2 | Fixed — `Assert.Single(collection, predicate)` instead of filtering with `Where` first |

`TreatWarningsAsErrors` is now on for the test project, so this class of debt cannot
silently re-accumulate.

## Original finding

`dotnet build KyberWeave.sln -c Release` fails on `main` with 61 errors under
`TreatWarningsAsErrors` + `AnalysisMode=all`, before any KyberDash changes:

- **CA1307** `string.{Replace,IndexOf,Contains}` missing `StringComparison`
- **CA1308** `ToLowerInvariant` → `ToUpperInvariant`
- **CA1716** reserved keyword `step` / `like` as parameter name
- **CA1305/CA1310** locale-sensitive `StringBuilder.Append` / `StartsWith` / `EndsWith`

All are in `src/KyberWeave.Core/` (Squad rendering, Docs analysis, parsing).
Verified: stashing the KyberDash working tree and building `main` still yields
`61 Error(s), 0 Warning(s)`. KyberDash's own `ts-*` gates and `kyber` vitest
suite (466 tests) are green; `MergeBoundaryTests` + `ReleaseTests` pass with
`-p:EnableNETAnalyzers=false`.

## Why not fixed in KyberDash

- The debt is pre-existing on `main` and not introduced by `docs/archive/specs/kyberdash`
  ( KyberDash touches only `dash/kyber/**`, `dash/src/menubar-json.ts` etc.).
- Fixing 61 files in the spec's scope would widen the diff and risk unrelated
  regressions; the spec's closeout is docs-only and the `build` gate is a
  repo-hygiene prerequisite, not a spec acceptance criterion.

## Acceptance

- [ ] `dotnet build KyberWeave.sln -c Release --no-restore` → `0 Error(s)`
- [ ] `dotnet run --project src/KyberWeave.Cli -- review gates .` → all blocking
      gates pass (including `build`)
- [ ] No `NoWarn` widening without stated reason (`Directory.Build.props`)

## Related

- Review finding in KyberDash end-of-run council (2026-08-29, `07a45a53`):
  "CHANGES_REQUESTED — .NET build gate is broken (61 analyzer errors)".
- `docs/archive/specs/kyberdash/tasks.md` task 13 closeout was refused on that gate;
  lint blocker `dash/kyber/web/components/SchemaView.tsx:3` unused `DerivedCaveat`
  was fixed in the same session ( `npm run lint` now `0 errors, 157 warnings` ).
