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
- Run only the Azure CLI's read verbs: `list*` and `show*`, as `az <group> list...` or
  `az <group> show...`. Every other command that names `az` — a mutating verb, an absolute
  path, a wrapper, a subshell, a redirect or a compound command — is refused by the Arbiter
  where the worker guard sees the call (Claude Code and Copilot CLI); on every other harness
  this list is an instruction you are trusted to follow.
- A mutating verb before the first option is refused even when the command also looks like a
  read: `az group delete list` deletes the group named `list`, so `delete` decides. Never rely
  on a read-shaped suffix.

## Security and data handling

- Print no secret. Report that a secret or credential exists, never its value.
- Quote resource identifiers and configuration values, not tokens or connection strings.
