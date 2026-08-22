---
id: todo/kyber-weave-docs-skill-vocabulary
title: The kyber-weave-docs skill teaches a doc-type vocabulary two members short
doc-type: todo
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# The kyber-weave-docs skill teaches a doc-type vocabulary two members short

This is **context for planning the work, not a plan**.

## Why this exists

[`.apm/skills/kyber-weave-docs/SKILL.md`](../../.apm/skills/kyber-weave-docs/SKILL.md) states
the closed doc-type vocabulary inline, and that list is now missing two members the validator
accepts:

- `todo`, closed as a real doc-type by
  [the specs/plans/todos work](../archive/plans/2026-08-16-specs-plans-todos-governance.md) —
  the skill was not updated then;
- `coding-standard`, added by
  [the standards work](../archive/plans/2026-08-16-coding-standards-and-config-reg.md), which
  deliberately made no skill changes.

The skill also says *"if nothing fits, the answer is `reference` — never invent a value"*.
An agent following it will label a coding standard as `reference`, which is a worse outcome
than the invention the sentence exists to prevent: the document validates, so nothing reports
it, and it never resolves as a standard.

## What is known

- The stale list is in the `## Required frontmatter` section, plus the required-key table
  immediately below it, which needs a `coding-standard` → `technology` row.
- The skill knows nothing about the configuration registry. An agent authoring a standard
  needs to know that `technology` is legal only when declared in `ontology.technologies`, and
  that declaring it is what creates the folder and the registry property.
- The authoritative statements the skill should agree with are
  [`documentation-ontology.md`](../documentation-ontology.md) and
  [`OntologyConfig`](../../src/KyberWeave.Core/Configuration/OntologyConfig.cs). Both are
  current.
- The same duplication will recur: the vocabulary is stated in the enum, the config defaults,
  the emitted ontology reference, this repository's ontology document, and the skill. Four of
  those five moved together this time.

## What needs deciding

- **Whether the skill should state the vocabulary at all**, or instruct the agent to read it
  from `<documentation-ontology>` in the registry. Stating it is faster at authoring time and
  goes stale; resolving it costs a file read and cannot. The skill is portable, so this is
  exactly the question the registry exists to answer.
- **Whether authoring a standard belongs to this skill or the deferred standards skill.** If
  the latter, `kyber-weave-docs` needs only the vocabulary correction and a pointer.

## How to verify

- The doc-type list in the skill matches `OntologyConfig.DefaultDocTypes` exactly, or the skill
  no longer contains a list.
- `kyber-weave skill validate .apm/skills/kyber-weave-docs` and `skill scan` pass.
- Authoring a coding standard by following the skill produces a document that passes
  `docs validate` on the first attempt.
