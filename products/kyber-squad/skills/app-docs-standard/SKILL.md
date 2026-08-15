---
name: app-docs-standard
description: Apply the repository documentation standard when creating or updating application, service, tool, or system documentation.
license: MIT
metadata:
  author: David R Palfery
  version: 1.1.0
---

# Application Documentation Workflow

Use this skill for documentation changes that affect an application, runnable service, tool, system boundary, or component README.

## Required workflow

1. Read `6-Docs/documentation-standard.md` in full.
2. Read `6-Docs/catalog.md` and identify the owning component and canonical documents.
3. Inspect the source-root README and detailed documentation before drafting changes. Ground claims in the current implementation.
4. Apply the required documentation shape from the standard:
   - source-root README for the concise overview;
   - `onboarding.md`, `architecture.md`, and `requirements.md` for applications and runnable services;
   - catalog update for a new, moved, renamed, or materially changed component.
5. Keep one canonical source per topic. Link to existing system, deployment, operations, or reference material instead of copying it.
6. Validate Markdown style, internal links, catalog coverage, and the absence of secrets before delivery.

## Deliverable

State the component documentation updated, the catalog impact, and the validation performed. If no documentation change is needed, state the reason explicitly.
