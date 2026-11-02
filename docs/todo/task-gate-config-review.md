---
id: todo/task-gate-config-review
title: Review the task-gate configuration choices
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-20
---

# Review the task-gate configuration choices

Two choices in `review.task-gates` were made without full confirmation.

**Which agents run the .NET gate set.** `dal-dev`, `maui-dev`, `pulumi-dev`,
`sql-database-architect` and `github-devops` share `csharp-dev`'s format and build gates by
YAML alias. `sql-database-architect` mostly edits `.sql` and `github-devops` mostly edits
workflows and Dockerfiles, so for them the include globs (`**/*.cs`, `**/*.csproj`,
`**/*.props`, `**/*.targets`) rarely match and the gates are skipped (`KW-REVIEW-026`). Confirm
that is the intended result, or give those two agents gates of their own.

**`dotnet format --include` with project files.** Checked 2026-10-20 with SDK 10 on macOS:
`dotnet format whitespace`, `style` and `analyzers` each accept a `.csproj` and a `.props` path
in `--include` and exit 0. A nonexistent path also exits 0, so the flag is lenient: a project
file in the include list is accepted but formats nothing, because `dotnet format` rewrites C#
documents. The `.csproj`, `.props` and `.targets` globs therefore only serve to make such a
change select the build gate. This was not run on Linux or Windows.

This todo is complete when both questions are answered in `.kyber-weave/kyber-weave.yml` and the
`--include` behaviour is confirmed on Linux and Windows.

**2026-10-27: the shipped catalogue answers the first question for installing
repositories.** The csharp family writes .NET task gates for `csharp-dev`, `dal-dev`,
`maui-dev` and `pulumi-dev` — format (`format-whitespace`, `format-style`), lint
(`format-analyzers`) and `build` — and `test-dev` gets a copy of every written format
and lint gate (see [Review council
architecture](../code-review/architecture.md#the-shipped-gate-catalogue)). That is the
intended agent set for a newly initialized repository: those four agents share the .NET
gate set by catalogue construction, and no other agent receives it.

What stays open is this repository's own configuration: whether its
`sql-database-architect` and `github-devops` gate sets — which alias `csharp-dev`'s
format and build gates — are the intended result for agents that mostly edit `.sql`,
workflows and Dockerfiles, and the Linux/Windows confirmation of `dotnet format
--include` with project files.
