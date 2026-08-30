---
id: todo/portable-artifacts-carry-project-standards
title: Remove project-specific coding standards embedded in canonical agents and code-review references
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-30
status: draft
---

# Remove project-specific coding standards embedded in canonical agents and code-review references

This is **context for planning the work, not a plan** — what's known, what needs deciding, and
where the precedent already is.

## Why this exists

Coding standards are project-specific; agents and skills are portable. Both currently ship as
the same artifact, so a host repository cannot state its own standard — it can only accept the
one embedded in the agents it installed, or contradict it locally and hope the agent loses.

The mechanism that fixes this now exists: a `coding-standard` doc-type, a
`<docs-root>/standards/<technology>/` folder created by `docs init`, and a
`<technology-coding-standard>` property in the configuration registry that a portable artifact
resolves by name. See [the plan](../archive/plans/2026-08-16-coding-standards-and-config-reg.md). What
has not happened is removing the duplicated content from the artifacts themselves — deliberately
deferred so that the mechanism and a 27-file content migration did not land together.

## What is known

Two populations, one defect:

- **The 24 canonical agents** under `products/kyber-squad/agents/`. Several already
  resolve a registry property instead of embedding a standard (`csharp-dev`, `maui-dev`,
  `dal-dev`, `pulumi-dev`, `python-dev`, `test-dev`). Remaining cases include:
  - [`react-dev.md`](../../products/kyber-squad/agents/react-dev.md) defers to "established
    coding standards and linting rules" without naming where they are — which is the shape the
    others should take once a registry property exists to name.
- **The seven per-technology references** under
  [`products/kyber-squad/skills/code-review/references/`](../../products/kyber-squad/skills/code-review/references/)
  — `csharp`, `react`, `python`, `sql`, `azure`, `pulumi`, `github-actions`. These are written
  in reviewer voice ("check that…") rather than author voice, but they are standards.
- Rewritten templates for all seven already exist at
  [`products/kyber-squad/standards/`](../../products/kyber-squad/standards/README.md). This
  repository's own [C# standard](../standards/csharp/README.md) and
  [test standard](../standards/test/README.md) are separate, adopted documents — not
  stand-ins for the host templates. Nothing installs the templates yet.

## What needs deciding

- **What an agent says when the property is absent.** A host that never declared a technology
  has no `<technology-coding-standard>` to resolve. Does the agent fall back to built-in
  defaults (which is the duplication again, just quieter), state that no standard is declared
  and proceed, or refuse? This is the decision that determines how much content can actually
  be deleted rather than moved behind a conditional.
- **Whether the code-review references survive at all.** If a standard is the single source,
  the reference file is either deleted or reduced to "review against
  `<technology-coding-standard>`". Deleting them changes what `skill validate` and the routing
  tests see, so it is not purely a content edit.
- **How the templates get installed.** `squad install` does not deploy `standards/` today, and
  the standards-authoring skill that would place them is itself deferred. Either could own it.

## How to verify

- No agent or skill under `products/kyber-squad/` states a technology-specific normative rule
  that a host repository could reasonably reverse — no framework mandates, no line lengths, no
  folder-layout requirements.
- Every agent that needs a standard resolves it by registry property name, in the form
  `app-docs-standard` already uses for `<docs-root>`.
- `kyber-weave skill validate` and `skill scan` still pass for every affected skill.

## Related

The broader [skill-resource migration todo](migrate-skill-resources-into-standards.md)
preserves all 64 supplemental resources while classifying normative policy, procedures, scripts,
provider guidance, and metadata into appropriate durable homes. Neither todo authorizes resource
deletion before its migration and routing acceptance criteria pass.
