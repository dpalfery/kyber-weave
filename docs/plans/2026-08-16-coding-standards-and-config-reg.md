---
id: plans/2026-08-16-coding-standards-and-config-reg
title: Coding Standards and the Configuration Registry
doc-type: plan
status: current
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-16
---

# Coding Standards and the Configuration Registry

**Status:** In progress
**Date:** 2026-08-16
**Goal:** Separate project-specific coding standards from portable agents and skills. Standards become governed documents under `<docs-root>/standards/<technology>/`; agents and skills reach them through a **Configuration Registry** ("Config Reg") of named paths, declared once in host configuration and rendered into the root `AGENTS.md`. `docs init` creates the directory structure in code; no skill is responsible for it.

---

## 1. Problem / Motivation

Coding standards are project-specific; agents and skills are portable across projects. Today
they are the same artifact. `products/kyber-squad/agents/dotnet-dev.md` mandates
FluentMigrator, a 0-7 folder layout, and "no Entity Framework"; `python-dev.md` carries a
`## Coding Standards` section of PEP 8 rules; the `code-review` skill ships seven
per-technology reference files. Every one of those is a policy decision belonging to a
repository, shipped inside an artifact that installs into any repository.

The consequence is that a host repository cannot state its own standard. It can only accept
the one embedded in the agents it installed, or contradict it locally and hope the agent
loses.

Two things are missing. A **place** for a standard to live as a governed document, and a
**lookup** that a portable artifact can perform without embedding a path that only makes
sense in one repository.

## 2. Approved decisions

- **D1:** Folder creation is code, not skill work. `DocsScaffolder`, under the existing
  `kyber-weave docs init`, creates the structure. No new subcommand.
- **D2:** `docs init` creates seven directories under the primary docs root — `standards/`,
  `plans/`, `specs/`, `todo/`, `adr/`, `rules/`, `reference/` — each with a canonical
  `README.md` carrying `doc-type: index` and the `--owner` value. Not `devops/`; not
  `archive/`, which is created on first archival and is excluded from the corpus anyway.
- **D3:** `standards/<technology>/` is scaffolded from a technology list declared in host
  configuration. A fresh configuration declares none, so `standards/` begins as a registry
  README listing what is available. Pre-seeding every host with seven technologies would
  plant governed documents for stacks the repository does not have.
- **D4:** `coding-standard` becomes a real closed-vocabulary doc-type — enum member, parse
  map, product default vocabulary, required-key entry, and retrieval authority — not a
  folder convention layered on `rule`. It ships to every host; using it stays optional.
- **D5:** `technology` becomes a new frontmatter key. Required on `coding-standard`,
  rejected on every other doc-type, and it must equal both the containing folder name and a
  technology declared in `ontology.technologies`. That list is the single source: it creates
  the folder, publishes the registry property, and legalizes the frontmatter value. It sits in
  the ontology section rather than one of its own because it is a closed vocabulary, which is
  what that section already holds.
- **D6:** Retrieval authority for `coding-standard` is 1.0 — undemoted, and unboosted. A
  standard is relevant when the question is how code should be written, which the text match
  already captures; boosting would place it above the architecture document on every
  question about code.
- **D7:** The Config Reg's truth is host configuration. `docs init` renders it into the **root
  `AGENTS.md` only**, between `<!-- KYBER_WEAVE_CONFIG_REG_START -->` and
  `<!-- KYBER_WEAVE_CONFIG_REG_END -->`, following the `CODEGRAPH_START` precedent already in
  that file. Machine consumers read YAML; agents read Markdown; neither parses the other's
  format.
- **D7a:** Everything `docs init` creates is **derived** from the docs root and the declared
  technologies rather than written into `config-reg:`; only a host's own additions are stored.
  Seeding derivable paths into the file was the original intent, and was dropped during
  implementation: a stored copy goes stale the day `docs-root` moves, and the operator would
  then be repairing values they never authored. Nothing else about the registry changed — the
  rendered block still carries every property.
- **D8:** The rendered region is always rewritten, regardless of `--force`. It is generated
  output. `--force` governs hand-authored content, and nothing outside the markers is
  touched. A repository with no `AGENTS.md` gets a minimal one — a repository with no agent
  instructions file is precisely the one that benefits from the registry.
- **D9:** Two new permanent rule ids in `docs validate`: `KW-CONFIG-REG-001` for a registry
  entry whose path does not resolve, `KW-CONFIG-REG-002` for a rendered block that no longer
  matches configuration. Not in `docs drift`, which means documentation-versus-code
  divergence. A third id, `KW-DOC-SPEC-007`, covers a `technology` value that disagrees with
  its folder. No placement rule forces `coding-standard` documents under `standards/`:
  `KW-CONFIG-REG-001` already catches a registry entry pointing nowhere.
- **D10:** No agent or skill changes in this work. The duplication in the 20 agents and the
  seven `code-review` reference files is real and is captured as a todo.
- **D11:** Kyber-Weave adopts the structure in the same change. `docs/standards/dotnet/` is
  authored for real — rewritten from the existing agent and skill content rather than copied,
  because a verbatim copy would import mandates (FluentMigrator, the 0-7 layout) that are
  false of this repository, and this corpus is held at zero findings.
- **D12:** The six remaining canonical technologies ship as templates under
  `products/kyber-squad/standards/`, not `docs/standards/`. Every document in `docs/` is a
  claim about this repository; a React standard here would be scoped to nothing. `products/`
  is where content destined for other repositories already lives.

## 3. Out of scope

- Authoring the standards-configuration skill that guides a user through declaring
  technologies and writing each standard. A later skill; the mechanism it will drive ships
  here.
- Removing embedded standards from agents and the `code-review` references (todo).
- Updating the `kyber-weave-docs` skill's doc-type vocabulary, which will ship listing
  neither `coding-standard` nor `todo` (todo).

## 4. Work

| # | Change | Where |
|---|---|---|
| 1 | `coding-standard` doc-type and `technology` frontmatter key | `Docs/Model`, `Docs/Parsing`, `Configuration/OntologyConfig` |
| 2 | `ontology.technologies` and `config-reg:` host configuration | `Configuration` |
| 3 | Directory and README scaffolding, technology folders | `Docs/Scaffolding/DocsScaffolder` |
| 4 | Config Reg rendering into root `AGENTS.md` | `Docs/Scaffolding` |
| 5 | `KW-CONFIG-REG-001`, `KW-CONFIG-REG-002`, `KW-DOC-SPEC-007` | `Docs/Validation` |
| 6 | Ontology reference and documentation updates | `docs/`, emitted ontology |
| 7 | Dogfood: run init here, author `docs/standards/dotnet/` | `docs/` |
| 8 | Six shipped technology templates | `products/kyber-squad/standards/` |
| 9 | Two todos | `docs/todo/` |

## 5. Verification

```bash
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
dotnet run --project src/KyberWeave.Cli -- docs validate .
dotnet run --project src/KyberWeave.Cli -- docs drift .
```

The corpus stays at zero findings. The self-updater, `install.sh`, and the Squad release
path are untouched, so the local release loop is not implicated.
