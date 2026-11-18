---
id: standards/pulumi/skills
title: "pulumi skills"
doc-type: skill
status: draft
technology: pulumi
owner: unassigned
last-reviewed: 2026-11-16
---

# pulumi skills

## Names

- Construct every Azure resource name through the `azure-naming` skill; do not improvise a
  convention.

## Documentation

- Pin registry documentation to the package version in use when behaviour matters.

## Hand-off

Agree stack output names (registry login server, app name, managed-identity client id) with
delivery before either side writes. Emit them so the pipeline reads them with
`pulumi stack output`.
