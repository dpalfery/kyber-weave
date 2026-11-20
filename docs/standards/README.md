---
id: standards/index
title: Coding standards
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-11-08
---

# Coding standards

How code is written in this repository, one folder per technology. A standard is
project-specific; the agents and skills that read it are not, which is why they resolve it
through the configuration registry rather than carrying their own. Only standards with
`status: current` outrank portable agent defaults; draft standards do not. What belongs in a
standard rather than a rule is in
[Rules and standards](../documentation-ontology.md#rules-and-standards).

Each folder holds four files named after it: `<name>.standards.md` (the standard, six
headings), `<name>.rules.md` (this repository's hard rules for the technology),
`<name>.skills.md` (its procedures and commands) and `<name>.arbiter-rules.yml` (machine
rules; empty when there are none). `shared-rules.md` beside the folders holds the rules that
apply to every technology. A rules or skills file is found by its frontmatter, not its name:
it needs `doc-type: rule` or `doc-type: skill` and the folder's `technology` (or `role`) line.
Without them `docs validate` reports `KW-DOC-SPEC-015`, and the tool and the task prompt cannot
carry the file even though it is on disk; `kyber-weave docs standards migrate` sets exactly
those two lines in place and leaves every other line and the body alone. Nothing ships for
testing alone: cross-technology testing rules live
in `shared-rules.md` and each standard's **Testing** heading, because `test` and `sql` went with
the per-technology agents the roster removal deleted.

This repository declares [C#](csharp/csharp.standards.md),
[test](test/test.standards.md), [TypeScript](typescript/typescript.standards.md),
[Rust](rust/rust.standards.md), and [Tauri](tauri/tauri.standards.md) standards, a
[Go](go/go.standards.md) standard for Go code that does not exist yet, and a
[GitHub Actions](github-actions/github-actions.standards.md) standard for the workflows under
`.github/workflows/`. Rust and Tauri cover the KyberDash tray crate; Tauri covers only the
Tauri layer and defers language rules to Rust. The authoritative list is the Config Reg block
in the repository root [`AGENTS.md`](../../AGENTS.md), which is regenerated on every
`docs init` run — this file is not.

## Declaring a technology

Add it to `ontology.technologies` in
[`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml) and re-run
`kyber-weave docs init .`. That one list creates the technology's folder, publishes its
`<name-coding-standard>` property in the registry, and legalizes the `technology` value in the
standard's frontmatter — so the three cannot disagree.

To bootstrap a repository with the full suite of 14 Kyber Squad technology templates
(`aws`, `azure`, `csharp`, `data-access-layer`, `github-actions`, `go`, `maui`, `pulumi`,
`python`, `react`, `rust`, `sql-server`, `tauri`, `typescript`) plus the four
role folders and four platform folders, name them with `--standards`:
`kyber-weave docs init . --standards csharp,reviewer,github`. `--kyber-standards` installs
every technology template. Do not run either on this repository: it would declare the
technologies this repository does not use.

A technology name is a slug: lowercase letters, digits and single hyphens.

A template change reaches new hosts only: a folder already installed is never overwritten by a
`docs init` run. An existing host sees what its copy is missing, or differs by, with
`kyber-weave docs standards diff [name]`, which prints the difference per folder and writes
nothing; installing the missing files is then the host's own decision.

## A standard is read through the tool, not as a file

Every code-writing worker — the three developer tiers, `docs-dev`, `devops` and
`platform-researcher` — reaches the standard for its stack with the
**`docs_get_coding_standard_for_technology`** MCP tool, naming
the technology and, when it helps, the topic it is about to work on:

```
docs_get_coding_standard_for_technology(technology: "csharp", topic: "error handling")
```

The tool returns this repository's bundle for that technology — the rule ids first, then the
shared rules, the parent's rules and skills when the standard names a parent, the technology's
rules, its skills, and the standard's sections that answer the topic, inside a character
budget. Reading the folder's files directly is allowed **only when the tool is missing**.
A cut answer is not the fallback: an excerpt trimmed to fit the budget says so and says the
cut is expected, and asking again with a larger `charBudget` or a narrower topic is the move.
Opening the files instead re-creates the whole cost the size limit and the tool exist to
prevent, which is why the instruction sits in the same workflow step that names the tool, in
the skill beside the agent as well as in the agent itself.

**The registry property names stay in the instruction.** A worker asks for
`<csharp-coding-standard>`, not a bare technology name, because the tool accepts either form and
the property is what the root `AGENTS.md` declares. Two behaviours depend on the name being
there: an undeclared technology, and a technology declared with no standard scaffolded yet,
both return the worker's own question to put to the human rather than a confident answer from
somewhere else. The property name is how a worker knows which question to ask.

The tool's own contract, including its topic match and its budget, is in the
[DocGraph MCP runbook](../docgraph/mcp-runbook.md#docs_get_coding_standard_for_technologytechnology-topic--null-charbudget--12000-projectpath--null).

## What belongs in a standard

The decisions this repository has made and would otherwise re-argue in review — not a summary
of the language's own documentation, and not rules the analyzers already enforce silently.

A rule that holds regardless of language belongs in [`rules/`](../rules/README.md) instead.

Every standard ends with a **`## Completion`** section: the build, the targeted test command and
the format check a finishing worker runs, what runs when, and why. That knowledge used to sit in
the portable agent files, which sent this repository's commands to every host on every round trip
and let them drift per project. It lives in the standard instead, reached by topic only when a
worker is finishing — [the test standard](test/README.md#completion) is the worked example. A
standard has exactly one such section, and no separate `## Commands` section beside it: two
sections answering "how do I finish" drift apart the moment one is edited and the other is not.

Every factual statement in a standard with `status: current` must be checkable in this repository
when it is written: a gate command, a setting, a file, or a version. A claim from a template that
is false here is dropped or reworded. A prescriptive rule that existing code does not fully meet
may stay as a rule for new and changed code, provided the standard lists the existing exceptions
with their location, as the [Rust standard](rust/README.md#existing-exceptions) does.

Review rules for the council live in [`REVIEW.md`](../../REVIEW.md), not here. A standard says how
code is written; `REVIEW.md` says what a reviewer checks. A standard may link to `REVIEW.md`, but
it does not restate its rules.
