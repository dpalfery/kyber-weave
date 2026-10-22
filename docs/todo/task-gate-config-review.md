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
