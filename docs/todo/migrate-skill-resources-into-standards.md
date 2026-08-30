---
id: todo/migrate-skill-resources-into-standards
title: Migrate retained skill-resource knowledge into durable canonical homes
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-08-30
---

# Migrate retained skill-resource knowledge into durable canonical homes

This todo captures authorized deferred work discovered while synchronizing Kyber-Squad's rendered
Copilot agents and skills with the Hotshot Logistics golden tree. It is context for future planning,
not authorization to migrate or delete the retained resources during the golden synchronization.

## Why this exists

The golden tree contains 24 `SKILL.md` files but omits supplemental files that those skills route
to. Kyber-Squad's canonical tree preserves 64 supplemental resources: 61 are directly referenced
by retained skills, two are create-PR scripts with reusable behavior, and one is
`setup-dev-environment/agents/openai.yaml`. Removing them to make canonical storage resemble the
rendered tree would discard working knowledge and leave dangling references.

The current safe boundary is therefore deliberate: canonical source uses the exact golden raw bytes
for all 24 `SKILL.md` files, restores the 64 supplemental resources from the synchronization
baseline, and renders exactly the 24 golden skill files to Copilot. Recursive Squad packages
continue to carry all retained resources until each item has a verified durable replacement.

## Deferred migration

- Inventory every retained resource and classify its content before moving anything.
- Move normative technology and repository policy into the appropriate canonical
  `products/kyber-squad/standards/*` template or governed documentation baseline.
- Assign procedural guidance, operational playbooks, review lenses, provider instructions, scripts,
  and agent metadata to an appropriate durable canonical home rather than forcing non-policy
  material into a coding standard.
- Preserve content item by item, recording the source, destination, and verification evidence for
  every migration or intentional supersession.
- Update retained `SKILL.md` routing only after the destination content and deployment behavior are
  verified. Keep the original resource packaged until that routing change is proven.

## Acceptance criteria

- Every retained resource has a reviewed disposition and a content-preservation mapping; nothing is
  deleted merely because the current golden render omits it.
- No retained canonical or packaged `SKILL.md` has a dangling relative reference.
- Normative content has one canonical home under `products/kyber-squad/standards/*` or the governed
  documentation baseline, and non-policy content has an explicitly appropriate durable home.
- A content-by-content audit proves migrated or superseded material remains available with no
  silent loss of procedures, examples, lens criteria, provider guidance, scripts, or metadata.
- `docs init` and the relevant Squad deployment/package path demonstrate that the new canonical
  locations reach consumers as designed before old routing or resources are removed.
- Focused source, skill, render, package, and reference-integrity tests pass, along with
  `docs validate` and `docs drift` at zero findings.

## Related work

[Remove project-specific coding standards embedded in canonical agents and code-review
references](portable-artifacts-carry-project-standards.md) is related but narrower: it addresses
project-specific normative rules embedded in portable artifacts. This todo covers the broader
content-preserving migration of all retained resource kinds and links to that work rather than
duplicating its scope or decisions.
