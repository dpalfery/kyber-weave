---
id: standards/index
title: Coding standards
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-16
---

# Coding standards

How code is written in this repository, one directory per technology. A standard is
project-specific; the agents and skills that read it are not, which is why they resolve it
through the configuration registry rather than carrying their own.

This repository declares one technology, because it is C# and nothing else. The authoritative
list is the Config Reg block in the repository root [`AGENTS.md`](../../AGENTS.md), which is
regenerated on every `docs init` run — this file is not.

## Declaring a technology

Add it to `ontology.technologies` in
[`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml) and re-run
`kyber-weave docs init .`. That one list creates the technology's folder, publishes its
`<name-coding-standard>` property in the registry, and legalizes the `technology` value in the
standard's frontmatter — so the three cannot disagree.

A technology name is a slug: lowercase letters, digits and single hyphens.

## What belongs in a standard

The decisions this repository has made and would otherwise re-argue in review — not a summary
of the language's own documentation, and not rules the analyzers already enforce silently.

A rule that holds regardless of language belongs in [`rules/`](../rules/README.md) instead.
