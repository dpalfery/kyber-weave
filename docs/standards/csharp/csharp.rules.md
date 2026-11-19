---
id: standards/csharp/rules
title: "csharp rules"
doc-type: rule
status: current
technology: csharp
owner: dpalfery
last-reviewed: 2026-11-19
---

# csharp rules

## Interface first, unit tested

- Write the interface before the class. A class that does work depends on interfaces, never on a
  concrete service, so a unit test can hand it a fake.
- Everything outside the process — the file system, processes, the network, the clock, the
  environment, the database, the credential store, shipped content — is reached only through a
  seam: an interface in the Contracts project, one adapter in the Data or Kernel layer, and one fake
  in the test support project. No other code touches the resource directly.
- New behaviour ships with an in-memory unit test written against the interface. A test that
  needs a real disk, process or network is not a unit test.
- Code you can only test through a real resource is a design gap. Add or widen the seam and write
  the unit test against it; do not add a slow test to cover the gap.
- An integration test is the exception: it proves one adapter against the real resource, lives in
  the integration test project, and runs in CI, never in a developer's task gate.
- The Domain project holds data only. Logic goes in Application.

## Build and analyzers

- `TreatWarningsAsErrors` is on with `AnalysisMode=all`. A warning is a failure, and a new one
  is a change defect.
- Do not add to `NoWarn` in `Directory.Build.props` without a reason you can state. A
  suppression is a claim that the analyzer is wrong here; say why in the change.
- Public APIs carry XML doc comments. A public type or member without one is incomplete.
- The rule ids in this repository (`KW-*`) are permanent. Add a new id; never reuse or
  renumber one.
