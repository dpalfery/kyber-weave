---
id: adr/0074-cleanupcode-is-removed-from-the-squad
title: ReSharper cleanupcode is removed from the squad
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-12
component: KyberSquad
decided-by: []
supersedes: []
code-refs: []
---

# ADR 0074: ReSharper cleanupcode is removed from the squad

## Status

Accepted, 2026-11-12.

## Context

The squad's optional fix pass ran `dotnet jb cleanupcode` after `dotnet format`. On macOS 27.3
(build 26C5035r, Apple Silicon) with ReSharper command line tools 2026.2.1 it finishes its work, logs
`Close solution instance`, and then never exits.

The trace log shows why. The tool hosts two child processes over JetBrains Rd socket servers. The
MsBuild child shuts down cleanly. For the Roslyn worker the tool kills the peer before the server's
lifetime ends, the receiver thread goes back into `accept()`, and closing the listening socket while
another thread sits in `accept()` never returns on this machine.

Reproduced on .NET 8.0.33 and 10.0.14, with and without `--no-updates`, with a fresh `--caches-home`,
with `--no-buildin-settings`, on the .NET 11 release candidate, and on ReSharper tools 2025.3.5.3,
2026.1.5.3, 2026.2.4.1 and 2026.3.0. The tool starts the Roslyn worker whenever a project references
an analyzer package, and a `--properties` override cannot remove an explicit package reference, so
the worker cannot be switched off from the command line. The tool has no switch for it.

## Decision

`cleanupcode` is removed from the squad: the `resharper-clt` skill, the task gate, the developer
agents, the C# coding standard, the review documents and the `KyberWeave` cleanup profile. `dotnet
format` is the only formatter. A timeout around the tool is not accepted as its normal way to end,
and the pass was optional and gated nothing.

`inspectcode` hangs the same way and is removed too, by [ADR 0075](0075-inspectcode-is-removed-from-the-squad.md).

## Consequences

- Mechanical findings that only the cleanup profile fixed (for example redundant qualifiers) are no
  longer erased before review; the `format-style` gate and the build's analyzers report them.
- The todo about the cleanup profile rewriting untouched expression bodies is deleted with the profile.
- Revisit when a ReSharper release exits on its own on macOS: add it back with one self-exiting
  command and no timeout in the normal path.
