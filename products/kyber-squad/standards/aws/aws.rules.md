---
id: standards/aws/rules
title: "aws rules"
doc-type: rule
status: draft
technology: aws
owner: unassigned
last-reviewed: 2026-11-16
---

# aws rules

## Inspection

- A read-only task changes nothing: no resource creation, update or deletion, and no
  repository edit.
- Print no secret value. Report that a secret exists, never what it is.

## Security and data handling

- Never run a wildcard command that could delete or stop resources while inspecting.
  Prefer describe, list and get forms.
