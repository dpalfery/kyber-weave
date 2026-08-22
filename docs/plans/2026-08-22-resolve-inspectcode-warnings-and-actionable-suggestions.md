---
id: plans/2026-08-22-resolve-inspectcode-warnings-and-actionable-suggestions
title: Resolve InspectCode Warnings and Actionable Suggestions
doc-type: plan
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-08-22
---

# Resolve InspectCode Warnings and Actionable Suggestions

**Status:** Complete  
**Date:** 2026-08-22  
**Goal:** Resolve all 34 InspectCode static analysis warnings (`AssignNullToNotNullAttribute`, `AccessToDisposedClosure`, `AccessToModifiedClosure`) and high-value actionable suggestions (accessibility narrowing, collection expressions, domain model immutability, get-only auto-properties) across `KyberWeave.sln`.

---

## 1. Problem / Motivation

Static analysis of `KyberWeave.sln` via JetBrains ReSharper Command Line Tools (`dotnet jb inspectcode`) identified 34 warnings and 109 non-style suggestions:
1. **30 `AssignNullToNotNullAttribute` Warnings**: In `tests/KyberWeave.Tests/SquadDeploymentStateTests.cs`, nullable `Record.Exception(...)` results were passed directly to `Assert.IsAssignableFrom<T>(...)` without declaring `Exception?`.
2. **4 Closure & Variable Lifetime Warnings**: In `tests/KyberWeave.Tests/DocumentationAnalysisScaleTests.cs` and `tests/KyberWeave.Tests/SquadReleaseClientTests.cs`, outer scope variables disposed in `using` or `finally` blocks were captured in async lambdas.
3. **1 Accessibility Narrowing**: In `src/KyberWeave.Core/Review/DuplicateDetector.cs`, `Normalize` was declared `internal static` but only used locally.
4. **14 Immutability Improvements**: In `src/KyberWeave.Core/Docs/Model/DocumentModel.cs` (`DocumentFrontmatter`) and `src/KyberWeave.Core/Configuration/ReviewYamlSection.cs`, properties were converted to `init`-only.
5. **6 Get-Only Auto-Properties**: In `src/KyberWeave.Core/Diagnostics/Diagnostic.cs`, `Claim.cs`, `GlossaryModels.cs`, `ReviewModels.cs`, and `SquadDeploymentModels.cs`, properties initialized in constructors were simplified to `{ get; }`.

---

## 2. Approved Decisions

- **D1:** Fix all 34 warnings so that InspectCode returns 0 warnings solution-wide.
- **D2:** Apply all safe, actionable suggestions without disrupting reflection-based framework bindings (Spectre.Console CLI option binding).
- **D3:** Strict compliance with repository coding standards: explicit types (no `var` with collection expressions), file-scoped namespaces, and Allman braces.
- **D4:** Preserve 100% test pass rate and ensure documentation validation (`docs validate .`) remains clean.

---

## 3. Tasks & Outcomes

| # | Task | Target Files | Outcome |
|---|---|---|---|
| 1 | Fix `AssignNullToNotNullAttribute` warnings in tests | `tests/KyberWeave.Tests/SquadDeploymentStateTests.cs` | Resolved all 30 warnings by using `Exception?` for `Record.Exception` results |
| 2 | Fix closure lifetime warnings | `tests/KyberWeave.Tests/DocumentationAnalysisScaleTests.cs`, `tests/KyberWeave.Tests/SquadReleaseClientTests.cs` | Resolved 4 warnings using `SamplerState` helper class and proper callback scoping |
| 3 | Apply accessibility narrowing & cleanups | `src/KyberWeave.Core/Review/DuplicateDetector.cs` | Changed `Normalize` to `private static` |
| 4 | Adopt `init`-only immutability for frontmatter & review sections | `src/KyberWeave.Core/Docs/Model/DocumentModel.cs`, `src/KyberWeave.Core/Configuration/ReviewYamlSection.cs` | 14 properties converted to `{ get; init; }` |
| 5 | Adopt get-only auto-properties on core models | `src/KyberWeave.Core/Diagnostics/Diagnostic.cs`, `src/KyberWeave.Core/Docs/Analysis/Claims/Claim.cs`, `src/KyberWeave.Core/Docs/Analysis/Glossary/GlossaryModels.cs`, `src/KyberWeave.Core/Docs/Analysis/Review/ReviewModels.cs`, `src/KyberWeave.Core/Squad/Deployment/SquadDeploymentModels.cs` | 6 model properties converted to `{ get; }` |
| 6 | Verification & Gates | Build, test suite (1,548 tests), InspectCode, `docs validate .`, `docs drift .` | All gates passing cleanly, 0 InspectCode warnings |
