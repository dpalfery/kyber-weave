---
id: standards/csharp/rules
title: "csharp rules"
doc-type: rule
status: current
technology: csharp
owner: dpalfery
last-reviewed: 2026-11-16
---

# csharp rules

## Build and analyzers

- `TreatWarningsAsErrors` is on with `AnalysisMode=all`. A warning is a failure, and a new one
  is a change defect.
- Do not add to `NoWarn` in `Directory.Build.props` without a reason you can state. A
  suppression is a claim that the analyzer is wrong here; say why in the change.
- Public APIs carry XML doc comments. A public type or member without one is incomplete.
- The rule ids in this repository (`KW-*`) are permanent. Add a new id; never reuse or
  renumber one.

## Scope

- A change under `dash/` follows the display-layer rule: the dashboard and tray display one
  engine's data and hold no feature logic, scheduler or job of their own.
