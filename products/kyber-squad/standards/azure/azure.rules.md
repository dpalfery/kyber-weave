---
id: standards/azure/rules
title: "azure rules"
doc-type: rule
status: draft
technology: azure
owner: unassigned
last-reviewed: 2026-11-16
---

# azure rules

## Read-only

- Change nothing: no resource creation, update or deletion, no deployment or provisioning,
  and no repository edit.

## Security and data handling

- Print no secret. Report that a secret or credential exists, never its value.
- Quote resource identifiers and configuration values, not tokens or connection strings.
