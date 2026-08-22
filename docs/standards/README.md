---
id: standards/index
title: Coding standards
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# Coding standards

How code is written in this repository, one directory per technology. A standard is
project-specific; the agents and skills that read it are not, which is why they resolve it
through the configuration registry rather than carrying their own. Only standards with
`status: current` outrank portable agent defaults; draft standards do not.

This repository declares C# and test standards. The authoritative list is the Config
Reg block in the repository root [`AGENTS.md`](../../AGENTS.md), which is regenerated on
every `docs init` run — this file is not.

## Declaring a technology

Add it to `ontology.technologies` in
[`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml) and re-run
`kyber-weave docs init .`. That one list creates the technology's folder, publishes its
`<name-coding-standard>` property in the registry, and legalizes the `technology` value in the
standard's frontmatter — so the three cannot disagree.

To bootstrap a repository with the full suite of 10 Kyber Squad coding standards templates
(`csharp`, `test`, `react`, `python`, `pulumi`, `maui`, `data-access-layer`, `sql`, `azure`,
`github-actions`), run `kyber-weave docs init . --kyber-standards`.

A technology name is a slug: lowercase letters, digits and single hyphens.

## What belongs in a standard

The decisions this repository has made and would otherwise re-argue in review — not a summary
of the language's own documentation, and not rules the analyzers already enforce silently.

A rule that holds regardless of language belongs in [`rules/`](../rules/README.md) instead.
