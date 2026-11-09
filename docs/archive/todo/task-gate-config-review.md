---
id: todo/task-gate-config-review
title: Review the task-gate configuration choices
doc-type: todo
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Review the task-gate configuration choices

> [!NOTE]
> **Closed 2026-11-08.** Both questions are answered. The task-gate configuration choices are recorded
> in `.kyber-weave/kyber-weave.yml` and the shipped catalogue, and `dotnet format --include` with project
> and props paths was measured on Linux and Windows as well as macOS: it is accepted and formats nothing
> on all three. Nothing is left open here; the section below records both answers and the evidence.

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

### Answered 2026-11-08 on Linux and Windows

The throwaway workflow ran on branch `scratch/t41-format-include` (run `42512232098`, never merged and
deleted afterwards) on `ubuntu-latest` and `windows-latest` with SDK `10.0.403` from `global.json`. It
ran each of `whitespace`, `style` and `analyzers` against four include kinds — a `.csproj`, a `.props`, a
path that does not exist, and a `**/*.csproj` glob — and recorded the exit code, the tool's own output,
and the count of files the run actually left modified.

**All twelve combinations per platform, twenty-four in total, exited 0 and modified zero files.** The
output was the same shape every time: `Formatting code files in workspace '…KyberWeave.sln'.` followed by
`Format complete in <n>ms`. Nothing distinguished a real `.csproj` path from a path that does not exist,
on either platform.

So the macOS result generalises: the flag is lenient on all three platforms, and a project or props
path in `--include` is **accepted but formats nothing**. What the `.csproj`, `.props` and `.targets`
globs buy is selection — a change to one of those files makes a format gate run rather than select
nothing — not coverage. They cannot be relied on to format a project file, and an exit 0 from a format
gate never proves a project file was checked.

The count of modified files is what makes this evidence rather than a restatement of the exit code, and
it is why the probe measured it: a lenient flag that exits 0 while doing nothing is exactly the case an
exit code cannot see.

Related: the [SQL, workflow and shell gate todo](../archive/todo/task-gates-workflow-and-shell-edits.md),
closed 2026-11-08, and [mapping a task to its test project](../archive/todo/task-gate-test-project-mapping.md),
closed 2026-11-08.

## Decisions

| ID | Question | Answer / decision | Where it is recorded |
|---|---|---|---|
| TGCR1 | Which agents run the .NET gate set? | `sql-database-architect` keeps it and gains `sql-format` and `review-config`, because a migration can change what compiles; `github-devops` keeps it and gains `workflow-lint`, `shellcheck` and `review-config`; `docs-dev` gains `review-config` alone, so a configuration-only task is checked at all. The shipped catalogue reaches the same shape from the other side, writing the plumbing gates only for a host that declares `github-actions`. | [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml) `review.task-gates`, and [the shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) |
| TGCR2 | Does `dotnet format --include` with a project or props path check anything? | No. It is accepted and exits 0 on Linux, Windows and macOS for all three verbs while formatting nothing, and a path that does not exist is accepted the same way. The project globs buy selection, never coverage. | [The build is the first reviewer](../../standards/csharp/README.md#the-build-is-the-first-reviewer) |
| TGCR3 | How was that measured, given an exit code cannot answer it? | By counting the files each run actually left modified, on `ubuntu-latest` and `windows-latest`, for three verbs against four include kinds. Twenty-four runs, every one exit 0 with zero files modified, all reporting `Format complete`. | This note's second question, which records the platform, the run and the result |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DH1 | ReviewCouncil | standard | [The build is the first reviewer](../../standards/csharp/README.md#the-build-is-the-first-reviewer) | A format gate's exit 0 does not prove a project file was checked, because the flag accepts such a path and formats nothing; the project globs in a gate's `include` buy selection rather than coverage. Measured on all three platforms. |
| DH2 | ReviewCouncil | architecture | [The shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) | The catalogue writes a host's .NET gates for the four C# authoring roles and the plumbing gates only when the host declares `github-actions`, so a host that declares no workflow, shell or configuration files receives none of them. |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| TGCR1, DH2 | [The shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) | ReviewCouncil | 2026-11-08 | archived |
| TGCR2, TGCR3, DH1 | [The build is the first reviewer](../../standards/csharp/README.md#the-build-is-the-first-reviewer) | ReviewCouncil | 2026-11-08 | archived |
