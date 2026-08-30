---
id: todo/squad-hardcoded-docs-root
title: Replace the hardcoded 6-Docs path with a resolvable docs-root across Kyber-Squad's canonical instructions
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-30
status: draft
---

# Replace the hardcoded 6-Docs path with a resolvable docs-root across Kyber-Squad's canonical instructions

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the precedent already is. It does not sequence tasks or commit to an
implementation.

## Why this exists

`6-Docs` is the Kyber-Squad product's default documentation root, but it remains as a
literal string in canonical skill instructions and retained resources. The current sweep finds
46 occurrences on 38 lines across eight files; one file uses it only as the approved illustrative
example, while seven still carry actionable directives. The 24 canonical agents now use Config Reg
properties. Any project that overrides its docs root (Kyber-Weave's own
repository does exactly this, via `ontology.docs-root: docs` in
`.kyber-weave/kyber-weave.yml`) can still receive a skill whose instructions point at the wrong
directory. There is no per-deployment substitution for those retained literals.

## What is known

- One canonical skill already does this correctly:
  [`products/kyber-squad/skills/app-docs-standard/SKILL.md`](../../products/kyber-squad/skills/app-docs-standard/SKILL.md)
  reads: *"Locate the repository's documentation root from the configured
  `ontology.docs-root` (e.g. `docs/` or `6-Docs/`)"* and then uses `<docs-root>` as a
  placeholder for the rest of its instructions (`<docs-root>/catalog.md`, etc.). This is the
  pattern to propagate, not a new one to invent.
- The seven files with actionable directives are all under `products/kyber-squad/skills/`:
  `skills/product-owner/SKILL.md`, `skills/product-owner/references/requirements-phase.md`,
    `skills/product-owner/references/design-phase.md`,
    `skills/product-owner/references/tasks-phase.md`, `skills/bug-crusher/SKILL.md`,
    `skills/second-brain/SKILL.md`, `skills/second-brain/references/templates.md`.
- Representative occurrences include `skills/product-owner/SKILL.md` ("All artifacts live in
  `6-Docs/specs/{feature_name}/`") and its retained phase references (registering and archiving
  specs at `6-Docs/specs/README.md` / `6-Docs/archive/specs/`).
- This matters beyond Kyber-Weave's own repository: any project that installs Kyber-Squad and
  configures a non-default docs root inherits the same mismatch.

## What needs deciding

- **Where does `<docs-root>` resolve from, for a project that isn't running Kyber-Weave's
  own docs tooling at all?** `app-docs-standard`'s existing instruction points at
  `ontology.docs-root` — that's Kyber-Weave's own ontology config
  (`.kyber-weave/kyber-weave.yml`, read by `OntologyConfigLoader`). Kyber-Squad and
  Kyber-Weave's docs governance are installed together from this repository but are
  logically separable — a project could plausibly install Squad without adopting
  Kyber-Weave's documentation ontology. Does `<docs-root>` in every agent/skill body always
  mean "read `.kyber-weave/kyber-weave.yml`'s `ontology.docs-root`, default `6-Docs` if
  absent," or does Squad need its own, independent docs-root setting?
- **Is this a rendering-time substitution or an authoring-time rewrite?** The canonical
  source bodies could keep the literal placeholder text `<docs-root>` (as
  `app-docs-standard` already does) and every reader — human or agent — resolves it
  contextually, or the render pipeline (`CopilotRenderer` and future renderers, see
  [architecture.md §8](../kyber-squad/architecture.md#8-rendering)) could substitute a
  resolved value into the rendered per-harness output. The former needs no code change, only
  a documentation-content edit across the remaining directive sites; the latter is a real renderer feature that
  doesn't exist today.

## How to verify

- Every one of the seven directive-bearing files uses the same `<docs-root>` convention `app-docs-standard.md`
  already does, with no remaining literal `6-Docs` outside of illustrative examples (e.g.
  "e.g. `docs/` or `6-Docs/`" is fine — an unconditional path like `6-Docs/plans/README.md`
  is not).
- `grep -rn "6-Docs" products/kyber-squad/agents/ products/kyber-squad/skills/` returns only
  the illustrative-example lines, not directive ones.
