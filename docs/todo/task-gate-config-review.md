---
id: todo/task-gate-config-review
title: Review the task-gate configuration choices
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
---

# Review the task-gate configuration choices

> [!NOTE]
> **Narrowed 2026-11-08, still open.** The first question is answered in the configuration: both
> agents now have gates that match the files they edit, so their aliased .NET gates no longer stand
> alone. The second question — `dotnet format --include` with project files on Linux and Windows —
> has **not** been answered: the check has run only on macOS, and the throwaway workflow on
> `ubuntu-latest` and `windows-latest` that would settle it has not run in this tree. This todo
> stays open for that one question.

## Question 1: which agents run the .NET gate set — answered 2026-11-08

`dal-dev`, `maui-dev`, `pulumi-dev`, `sql-database-architect` and `github-devops` shared
`csharp-dev`'s format and build gates by YAML alias. For the last two the include globs
(`**/*.cs`, `**/*.csproj`, `**/*.props`, `**/*.targets`) rarely matched, so their gates were
skipped (`KW-REVIEW-026`): a `.sql` migration, a workflow or a `Dockerfile` selected nothing.

The answer recorded in `.kyber-weave/kyber-weave.yml` is that this was **not** the intended result:

- `sql-database-architect` keeps the .NET gates — a migration can change what compiles — and gains
  `sql-format` (`sqlfluff lint "{changed}"` over `**/*.sql`) and `review-config`.
- `github-devops` keeps the .NET gates, and gains `workflow-lint` (`actionlint` over
  `.github/workflows/*.yml`), `shellcheck` (over `scripts/*.sh`) and `review-config`.
- `docs-dev` gains `review-config` alone, so a configuration-only task is checked at all.

The shipped catalogue reaches the same shape for an installing repository, from the other
direction: the `.NET` gates are written for `csharp-dev`, `dal-dev`, `maui-dev` and `pulumi-dev`,
and the three plumbing gates for `github-devops` and `docs-dev` — each of those two written only
when the host declares the `github-actions` technology, which is the declaration that covers
workflow, shell and configuration files. See the [review council
architecture](../code-review/architecture.md#the-shipped-gate-catalogue) and
[ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) for the catalogue's
`full-suite` half of the same repair.

## Question 2: `dotnet format --include` with project files — open

Checked 2026-10-20 with SDK 10 on **macOS only**: `dotnet format whitespace`, `style` and
`analyzers` each accept a `.csproj` and a `.props` path in `--include` and exit 0. A nonexistent
path also exits 0, so the flag is lenient: a project file in the include list is accepted but
formats nothing, because `dotnet format` rewrites C# documents. The `.csproj`, `.props` and
`.targets` globs therefore only serve to make such a change select a gate.

What remains is the Linux and Windows confirmation, and exit codes alone cannot supply it: an exit
0 from a lenient flag is the case that needs recording. The check is a throwaway workflow on a
`scratch/` branch — triggered on push, never merged — that runs the three verbs with a `.csproj`
and a `.props` in `--include` on `ubuntu-latest` and `windows-latest` and records what each verb
actually accepted and formatted per OS.

**This todo is complete when** the second question is answered by that evidence. The first
question is answered above and needs no further work.

Related: the [SQL, workflow and shell gate todo](../archive/todo/task-gates-workflow-and-shell-edits.md),
closed 2026-11-08, and [mapping a task to its test project](task-gate-test-project-mapping.md).
