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

The seam register ([`docs/reference/seams.md`](../../reference/seams.md)) is the authority for
which seams exist and where each adapter and fake lives; where `AGENTS.md`'s "Seams" summary is
narrower, the register wins.

- Write the interface before the class. A class that does work depends on interfaces, not on a
  concrete service that touches an outside resource; the sealed framework types the register lists
  as exceptions may be used directly.
- Every outside resource — the disk, a child process, the environment, the console, the clock,
  identifiers (`Guid.NewGuid`, `Random`), a cross-process lease, an embedded resource,
  configuration, the network, the credential store and shipped content — is reached only through
  its seam, as the register lists it: one
  interface, its adapters where the resource lives, and its fake in the shared test support
  project. Only an adapter, or a composition root wiring one at startup, touches the resource.
- New behaviour ships with an in-memory unit test written against the interface.
- Code you can only test through a real resource is a design gap: add or widen the seam and
  write the unit test against it, rather than a slow test.
- There are three kinds of test. A unit test runs in memory. An integration test proves an adapter,
  or a whole flow, against the real resource (real disk, real program); it runs in CI and never in a
  developer's task gate or the review council's gates. A content test reads shipped content through
  the corpus seam and runs with the unit tests. `AGENTS.md` names the project for each.
- The Domain project holds data only. Logic goes in Application.

## Build and analyzers

- `TreatWarningsAsErrors` is on with `AnalysisMode=all`. A warning is a failure, and a new one
  is a change defect.
- Do not add to `NoWarn` in `Directory.Build.props` without a reason you can state. A
  suppression is a claim that the analyzer is wrong here; say why in the change.
- Public APIs carry XML doc comments. A public type or member without one is incomplete.
- The rule ids in this repository (`KW-*`) are permanent. Add a new id; never reuse or
  renumber one.
