---
id: adr/0004-solution-level-static-analysis-and-noise-suppression
title: Solution-Level Static Analysis Configuration and Clean Code Policy
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# ADR 0004: Solution-Level Static Analysis Configuration and Clean Code Policy

## Status

Accepted

## Context

Static analysis across the `KyberWeave.sln` codebase surfaced hundreds of diagnostics consisting of three distinct groups:
1. **Actionable Code Quality Deficiencies**: Closure lifetime captures, unsafe initialization outside using blocks, redundant type arguments, and dead assignments.
2. **Modern C# Idiom Opportunities**: Collection expressions `[...]`, primary constructors, pattern matching, UTF-8 literals `"...\"u8`, and `init`-only immutability.
3. **Framework Reflection False Positives**: Spectre.Console CLI settings and YamlDotNet model properties populated dynamically via reflection, DTO positional record contracts, and test method naming standards embedding uppercase rule IDs (e.g. `KW-DOC-SPEC-001`).

Scattering `#pragma warning disable` or `[SuppressMessage]` attributes across production code degrades readability and obscures legitimate issues.

## Decision

We establish a unified solution-level static analysis and clean code policy:

1. **Centralized DotSettings Configuration**:
   - Suppress framework-induced reflection false positives, DTO positional property access, and test method uppercase rule names centrally in root `KyberWeave.sln.DotSettings`.
   - Zero `#pragma` or `[SuppressMessage]` clutter is permitted in production source files.

2. **Strict Compiler & Analyzer Enforcement**:
   - Maintain `TreatWarningsAsErrors=true` and `AnalysisMode=all` in `Directory.Build.props` with zero suppressions added to `<NoWarn>`.
   - All actionable warnings are resolved in source code rather than suppressed.

3. **C# Coding Standard Compliance**:
   - Modernize idioms in strict adherence to `<csharp-coding-standard>`:
     - **Explicit typing**: Variable declarations must be explicitly typed (e.g., `string[] items = ["a", "b"];`), never `var items = [...]`.
     - **Primary constructors**: Adopted for immutable services, fixtures, and linters with direct field assignments.
     - **UTF-8 literals**: Static test payloads and mock responses use zero-allocation `"...\"u8` literals.
     - **Immutability**: DTOs and configuration sections use `{ get; init; }` or get-only auto-properties `{ get; }`.
     - **File-scoped namespaces** and **Allman braces** preserved across all source files.

## Alternatives Considered

- **Inline `#pragma warning disable`**: Rejected because it pollutes domain models and creates noise that hides real regressions.
- **Global `<NoWarn>` in `Directory.Build.props`**: Rejected because disabling compiler analyzers solution-wide undermines type safety and code quality gates.

## Consequences

- The entire solution builds with zero warnings under `TreatWarningsAsErrors`.
- Static analysis gates in CI and the review council run with high signal-to-noise ratio.
- Production and test code maintain consistent, modern, readable C# idioms.
