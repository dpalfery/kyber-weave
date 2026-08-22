---
id: adr/0001-coding-standards-and-configuration-registry
title: Decoupled Coding Standards and Derived Configuration Registry
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# ADR 0001: Decoupled Coding Standards and Derived Configuration Registry

## Status

Accepted

## Context

Coding standards are inherently project-specific and opinionated, whereas agent definitions and skills are designed to be portable across diverse repositories. Previously, coding standards (such as C# architectural layer rules, PEP 8 requirements, or React guidelines) were embedded directly inside portable agent prompts and review skill references.

This created significant architectural problems:
1. **Loss of Host Autonomy**: A host repository could not declare or enforce its own coding standards without forking or overriding portable agent definitions.
2. **Pollution of Portable Agents**: Portable artifacts carried repository-specific mandates (e.g. mandatory FluentMigrator, strict folder numberings) that were invalid in different host contexts.
3. **Lack of Governed Document Taxonomy**: The documentation ontology had no distinct, schema-validated document type for technology-scoped coding standards.

## Decision

We decouple project-specific coding standards from portable agents through three core mechanisms:

1. **`coding-standard` Document Type**:
   - Introduced `doc-type: coding-standard` as a closed-set vocabulary member in the documentation ontology.
   - Introduced the `technology` frontmatter key, which is mandatory for `coding-standard` documents, rejected on all other doc-types, and strictly validated against `ontology.technologies` declared in `.kyber-weave/kyber-weave.yml` (`KW-DOC-SPEC-007`).
   - Standard documents reside at `<docs-root>/standards/<technology>/README.md`.

2. **Derived Configuration Registry ("Config Reg")**:
   - A machine-readable Markdown comment block (`<!-- KYBER_WEAVE_CONFIG_REG_START -->` / `<!-- KYBER_WEAVE_CONFIG_REG_END -->`) rendered into the repository root `AGENTS.md` and `CLAUDE.md`.
   - The registry block is dynamically derived from `docs-root` and `ontology.technologies` during `kyber-weave docs init .`, ensuring zero drift and eliminating stale duplicated paths in configuration files (`KW-CONFIG-REG-001`, `KW-CONFIG-REG-002`).

3. **Standard Authority & Lifecycle Rule**:
   - Only coding standards with `status: current` represent authoritative host policies that outrank portable agent defaults. Standards with `status: draft` are non-authoritative proposals/templates.

## Alternatives Considered

- **Hardcoding standards in agent system prompts**: Rejected because it destroys agent portability across projects and requires modifying agent definitions whenever project rules change.
- **Layering standards on `doc-type: rule`**: Rejected because rules govern the entire repository regardless of language, whereas coding standards govern specific language stacks across all repository components.
- **Storing static path lists in `.kyber-weave.yml`**: Rejected during implementation because static paths become stale when `docs-root` moves, requiring manual repair of derived values.

## Consequences

- Project-specific standards are cleanly separated from portable agent capabilities.
- Autonomous discovery allows portable agents to inspect the Configuration Registry to discover host-specific standards dynamically.
- Strict schema validation ensures that declared technologies, standards directories, and registry blocks never diverge.
