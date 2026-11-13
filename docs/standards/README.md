---
id: standards/index
title: Coding standards
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-11-08
---

# Coding standards

How code is written in this repository, one directory per technology. A standard is
project-specific; the agents and skills that read it are not, which is why they resolve it
through the configuration registry rather than carrying their own. Only standards with
`status: current` outrank portable agent defaults; draft standards do not.

This repository declares [C#](csharp/README.md), [test](test/README.md),
[TypeScript](typescript/README.md), [Rust](rust/README.md), and [Tauri](tauri/README.md)
standards, a [Go](go/README.md) standard for Go code that does not exist yet, and a
[GitHub Actions](github-actions/README.md) standard for the workflows under `.github/workflows/`.
Rust and Tauri cover the KyberDash tray crate; Tauri covers only the Tauri layer and defers
language rules to Rust. The authoritative list is the Config Reg block in the repository root
[`AGENTS.md`](../../AGENTS.md), which is regenerated on every `docs init` run — this file is not.

## Declaring a technology

Add it to `ontology.technologies` in
[`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml) and re-run
`kyber-weave docs init .`. That one list creates the technology's folder, publishes its
`<name-coding-standard>` property in the registry, and legalizes the `technology` value in the
standard's frontmatter — so the three cannot disagree.

To bootstrap a repository with the full suite of 14 Kyber Squad coding standards templates
(`csharp`, `test`, `typescript`, `rust`, `tauri`, `go`, `react`, `python`, `pulumi`, `maui`,
`data-access-layer`, `sql`, `azure`, `github-actions`), run `kyber-weave docs init . --kyber-standards`.
Do not run that on this repository: it would declare the technologies this repository does not use.

A technology name is a slug: lowercase letters, digits and single hyphens.

## A standard is read through the tool, not as a file

Every code-writing worker — the thirteen development agents and their skills — reaches the
standard for its stack with the **`docs_get_coding_standard_for_technology`** MCP tool, naming
the technology and, when it helps, the topic it is about to work on:

```
docs_get_coding_standard_for_technology(technology: "csharp", topic: "error handling")
```

The tool returns this repository's standard for that technology — its rule ids first, then the
sections that answer the topic, inside a character budget. Reading
`docs/standards/<technology>/README.md` directly is allowed **only when the tool is missing**.
It is not the fallback for a cut answer: an excerpt trimmed to fit the budget says so and says
the cut is expected, and asking again with a larger `charBudget` or a narrower topic is the
move. Opening the file instead re-creates the whole cost the size limit and the tool exist to
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
