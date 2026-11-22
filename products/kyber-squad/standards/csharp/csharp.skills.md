---
id: standards/csharp/skills
title: "csharp skills"
doc-type: skill
status: draft
technology: csharp
owner: unassigned
last-reviewed: 2026-11-22
---

# csharp skills

## What this folder covers

The standard holds the decisions; this file holds the commands.

- Layout, web API surface, build files, BFF: `standards` Naming and layout and Coding standards.
- File upload, observability, Azure AI, MSBuild correctness: Coding standards.
- Build speed: Commands. Security headers, authorization, rate limiting, logging: Security and
  data handling.

## Commands

- `dotnet build -c Release` builds with analyzers; `dotnet watch` runs with hot reload. Run only
  the tests covering your change; a bare `dotnet test` is the council and CI gate.
- Rebuilds with nothing changed: `dotnet build -v:diagnostic -fl -flp:logfile=diag.log`, then
  search for `Building target '<T>' completely` and `Input file ... is newer than output file`.
- Slow build: `dotnet build -fl -flp:logfile=perf.log;performancesummary` for target and task
  timings, or `dotnet build /bl:build.binlog` and open it in the MSBuild Structured Log Viewer.
- Disable the Visual Studio fast up-to-date check (`DisableFastUpToDateCheck`) only while
  diagnosing a discrepancy.
- No spans: check `AddSource` matches the source name, the OTLP endpoint is reachable, and
  sampling is not dropping them.

## Library documentation

Confirm the current API of any library you configure against its own documentation, and
follow the versions the standard pins. Do not rely on memory for a library's surface.

## Hand-off

When another technology is involved, report its input as an open item naming the table and
columns (data access), the endpoint contract (client UI), or the pipeline command
(delivery).
