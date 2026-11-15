---
id: adr/0075-inspectcode-is-removed-from-the-squad
title: ReSharper InspectCode is removed from the squad
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: KyberSquad
decided-by: []
supersedes: []
code-refs: []
---

# ADR 0075: ReSharper InspectCode is removed from the squad

## Status

Accepted, 2026-11-13.

## Context

[ADR 0074](0074-cleanupcode-is-removed-from-the-squad.md) removed `cleanupcode` because the tool
finishes its work and then never exits. `inspectcode` shows the same behaviour. On macOS 27.3 with
ReSharper command line tools 2026.2.1 it writes a complete report in about two minutes and then
never exits: a helper process's socket is closed while another thread still sits in `accept()`.

This repository survived only because its `inspectcode` gate ran under `scripts/report-gate.sh`,
which stops the tool once the report is complete. A host repository that uses the squad has no such
script, so the portable `resharper-clt` skill's bare command would hang there. A timeout around the
tool is not an acceptable normal path.

## Decision

`inspectcode` is removed from the squad and from this repository:

- the `resharper-clt` skill, and every instruction in the developer agents, the `task-gate` skill, the
  review skill and its C# reference that named it;
- the `inspectcode` review gate in `.kyber-weave/kyber-weave.yml`, with its `requires-paths`;
- `scripts/report-gate.sh` and its tests, which had no other user;
- the pinned `jetbrains.resharper.globaltools` entry (the only tool in `.config/dotnet-tools.json`, so the
  manifest goes too) and the `dotnet tool restore` steps in the cloud-agent install scripts.

The `static-analysis-triage` lens stays. It triages machine-produced analyzer output, and on .NET that is
now the compiler and analyzer warnings from the build gate (`TreatWarningsAsErrors` where the host sets it)
and the diagnostics of a `dotnet format --verify-no-changes` gate where the host declares one. No
replacement gate is added.

`KyberWeave.sln.DotSettings` is kept: its severity and `var` settings still configure Rider and ReSharper
in the editor. No gate reads it.

## Consequences

- Inspections the compiler and the analyzers do not raise (for example the unused-member family) are no
  longer reported at review. The Roslyn analyzers under `AnalysisMode=all` and `dotnet format` remain.
- A host repository can no longer be told to run a tool that hangs.
- Revisit when a ReSharper release exits on its own on macOS: add it back with one self-exiting command
  and no timeout in the normal path.
