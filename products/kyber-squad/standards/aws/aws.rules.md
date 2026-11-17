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
- Run only the AWS CLI's read verbs: `describe-*`, `list-*`, `get-*` and `ls`, as
  `aws <service> <verb>`. Every other command that names `aws` — a mutating verb, an absolute
  path, a wrapper, a subshell, a redirect or a compound command — is refused by the Arbiter
  where the worker guard sees the call (Claude Code and Copilot CLI); on every other harness
  this list is an instruction you are trusted to follow.
- A mutating verb before the first option is refused even when the command also looks like a
  read: `aws s3 rm s3://bucket/list` deletes the key `list`, so `rm` decides. Never rely on a
  read-shaped suffix.
