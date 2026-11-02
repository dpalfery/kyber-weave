---
id: standards/index
title: Coding standards
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-10-27
---

# Coding standards

How code is written in this repository, one directory per technology. A standard is
project-specific; the agents and skills that read it are not, which is why they resolve it
through the configuration registry rather than carrying their own. Only standards with
`status: current` outrank portable agent defaults; draft standards do not.

This repository declares [C#](csharp/README.md), [test](test/README.md),
[TypeScript](typescript/README.md), [Rust](rust/README.md), and [Tauri](tauri/README.md)
standards, and a [Go](go/README.md) standard for Go code that does not exist yet. Rust and Tauri
cover the KyberDash tray crate; Tauri covers only the Tauri layer and defers language rules to
Rust. The authoritative list is the Config Reg block in the repository root
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

## What belongs in a standard

The decisions this repository has made and would otherwise re-argue in review — not a summary
of the language's own documentation, and not rules the analyzers already enforce silently.

A rule that holds regardless of language belongs in [`rules/`](../rules/README.md) instead.

Every factual statement in a standard with `status: current` must be checkable in this repository
when it is written: a gate command, a setting, a file, or a version. A claim from a template that
is false here is dropped or reworded. A prescriptive rule that existing code does not fully meet
may stay as a rule for new and changed code, provided the standard lists the existing exceptions
with their location, as the [Rust standard](rust/README.md#existing-exceptions) does.

Review rules for the council live in [`REVIEW.md`](../../REVIEW.md), not here. A standard says how
code is written; `REVIEW.md` says what a reviewer checks. A standard may link to `REVIEW.md`, but
it does not restate its rules.
