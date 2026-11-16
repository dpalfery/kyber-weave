---
id: standards/pulumi/rules
title: "pulumi rules"
doc-type: rule
status: draft
technology: pulumi
owner: unassigned
last-reviewed: 2026-11-16
---

# pulumi rules

## Preview and apply

- Never apply a change whose preview was not inspected, and never hide a replacement inside
  an unreviewed apply.
- Provision the database resource; do not design its schema.
- Live-state questions go to the platform research agent. Intended state is the Pulumi
  program and its preview.

