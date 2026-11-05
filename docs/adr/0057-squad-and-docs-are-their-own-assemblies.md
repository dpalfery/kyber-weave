---
id: adr/0057-squad-and-docs-are-their-own-assemblies
title: Squad and Docs are their own assemblies
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-05
code-refs:
  - SquadTarget
  - SquadPathPolicy
  - DocType
  - StringDistance
---

# ADR 0057: Squad and Docs are their own assemblies

## Status

Accepted, 2026-11-05.

## Context

`KyberWeave.Core` held the whole engine, about 300 files. Squad (76 files) and Docs (70) were the
two largest areas, and neither used the other. Nothing stopped a new edge between them, or from
the shared code up into either one. Every host linked every area: the MCP server carried Squad,
and the Arbiter host carried both.

## Decision

1. **Squad builds as `KyberWeave.Core.Squad` and Docs as `KyberWeave.Core.Docs`**, each in
   `src/<assembly>/` and referencing `KyberWeave.Core`. Neither references the other. The CLI
   references both, the MCP server references Docs only, and the Arbiter host references neither.
2. **Namespaces do not move.** Each assembly's name is its root namespace, so the split changed
   no `using` and no public type name.
3. **Core keeps the few Squad and Docs types the rest of Core reads**, in their original
   namespaces under `src/KyberWeave.Core/Squad/` and `src/KyberWeave.Core/Docs/`:
   - Configuration reads `SquadTarget` and its catalog.
   - The status-line deployer reads `SquadPathPolicy`, the path semantics and the two path
     exceptions.
   - Ontology configuration reads `DocType`.

   A type moves down into Core only when Core code needs it. Moving a type down needs no ADR.
4. **Core grants both assemblies internals access**, for the shared path semantics and the
   ontology defaults. It is the one exception to keeping internals inside an assembly.
5. **Resources move with the type that reads them.** `KyberStandardsTemplates` reads `Standards.*`
   from the Docs assembly. The response-guidance default and its drift guard live in the Squad
   project. `GateCatalogue` and the Arbiter keep theirs in Core. Logical names are unchanged.

## Alternatives Considered

- **Rename namespaces to match new assembly names such as `KyberWeave.Squad`.** Rejected for now.
  It touches every file that imports Squad or Docs, for no behavioural gain.
- **Move configuration's Squad and Docs sections up into those assemblies.** Rejected.
  `KyberWeaveConfig` aggregates every section, and splitting it would change the public
  configuration surface.
- **Make the shared internals public instead of granting internals access.** Rejected. That
  widens Core's public surface to serve two in-repo friends.

## Consequences

- An edge from Squad into Docs, or from Core into either, is now a compile error, not a review
  comment.
- A Docs-only or Squad-only change no longer rebuilds the other area. Core changes still rebuild
  both.
- The release now packs `KyberWeave.Core.Squad` and `KyberWeave.Core.Docs` alongside
  `KyberWeave.Core`, and Core's package no longer contains Squad or Docs.
- Tests that read sources or project files by path name the new projects.
- The Squad path helpers that Core keeps still carry `Squad` names in a `Squad` namespace. A
  neutral file-system home for them is a follow-up.
