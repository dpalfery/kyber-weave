---
name: build-commands
description: Build, run, and test commands for the host .NET solution. Use when you need to build, run the API, or run tests.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Build & Run Commands

Commands below are the portable defaults. Prefer the path declared as **<csharp-coding-standard>** when the host names different projects.

## Build (with analyzers)

```bash
dotnet build -c Release
```

## Run API

```bash
dotnet run
```

Hot reload during development:

```bash
dotnet watch
```

## Run tests

```bash
dotnet test
```

The `dotnet test` line is the gate the review council and CI run. For your own run, filter to the
tests covering your change: `dotnet test --filter "FullyQualifiedName~<TestClass>"`.

Migrations are owned by `dal-dev`. See **<data-access-layer-coding-standard>**.
