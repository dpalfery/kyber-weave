---
id: todo/squad-hardcoded-docs-root
title: Replace the hardcoded 6-Docs path with a resolvable docs-root across Kyber-Squad's canonical instructions
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# Replace the hardcoded 6-Docs path with a resolvable docs-root across Kyber-Squad's canonical instructions

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the precedent already is. It does not sequence tasks or commit to an
implementation.

## Why this exists

`6-Docs` is the Kyber-Squad product's default documentation root, but it's written as a
literal, hardcoded string throughout the canonical agent and skill instructions — 72
occurrences across 19 files. Any project that overrides its docs root (Kyber-Weave's own
repository does exactly this, via `ontology.docs-root: docs` in
`.kyber-weave/kyber-weave.yml`) gets agents whose own instructions point at the wrong
directory. `architect`, `conductor`, and `product-owner` all instruct reading and writing to
`6-Docs/...` unconditionally — there is no per-deployment resolution.

## What is known

- One canonical skill already does this correctly:
  [`products/kyber-squad/skills/app-docs-standard/SKILL.md`](../../products/kyber-squad/skills/app-docs-standard/SKILL.md)
  reads: *"Locate the repository's documentation root from the configured
  `ontology.docs-root` (e.g. `docs/` or `6-Docs/`)"* and then uses `<docs-root>` as a
  placeholder for the rest of its instructions (`<docs-root>/catalog.md`, etc.). This is the
  pattern to propagate, not a new one to invent.
- The 19 affected files, all under `products/kyber-squad/`:
  - Agents: `agents/architect.md`, `agents/architect-v3.md`, `agents/conductor.md`,
    `agents/conductor-v3.md`, `agents/docs-dev.md`, `agents/product-owner.md`,
    `agents/test-dev.md`.
  - Skills: `skills/conductor/SKILL.md`, `skills/conductor-v3/SKILL.md`,
    `skills/product-owner/SKILL.md`, `skills/product-owner/references/requirements-phase.md`,
    `skills/product-owner/references/design-phase.md`,
    `skills/product-owner/references/tasks-phase.md`, `skills/bug-crusher/SKILL.md`,
    `skills/github-devops/SKILL.md`, `skills/create-pull-request/SKILL.md`,
    `skills/second-brain/SKILL.md`, `skills/second-brain/references/templates.md`.
- Representative occurrences (not exhaustive — 72 total):
  `architect.md:19` ("check `6-Docs/`"), `architect.md:62-63` ("read `6-Docs/plans/README.md`
  ... Place plans in `6-Docs/plans/`"), `conductor.md:25` ("Read only files under `6-Docs/`"),
  `conductor.md:45` ("Pure `6-Docs/` lookup"), `product-owner.md:33` ("All artifacts live in
  `6-Docs/specs/{feature_name}/`"), `product-owner.md:86,90` (registering and archiving
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
  a documentation-content edit repeated 72 times; the latter is a real renderer feature that
  doesn't exist today.

## How to verify

- Every one of the 19 files uses the same `<docs-root>` convention `app-docs-standard.md`
  already does, with no remaining literal `6-Docs` outside of illustrative examples (e.g.
  "e.g. `docs/` or `6-Docs/`" is fine — an unconditional path like `6-Docs/plans/README.md`
  is not).
- `grep -rn "6-Docs" products/kyber-squad/agents/ products/kyber-squad/skills/` returns only
  the illustrative-example lines, not directive ones.
