---
id: todo/analyzer-debt-ca1307-1308-1716
title: Fix pre-existing CA analyzer debt (61 errors on main)
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-08-29
---

# Fix pre-existing CA analyzer debt

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
