---
name: aws-cli-queries
description: "Query AWS resources read-only with the AWS CLI or an AWS MCP server. Confirms the account and role before any query, prefers describe, list and get forms, shapes output with --query, and reports the observed fact with its identifier and time. Load before any AWS query or when inspecting AWS resources."
---

# AWS CLI queries

## Sign in and confirm identity

- Confirm the account and role with `aws sts get-caller-identity` before any query.
- Use the profile or role the task names; do not assume the default profile is the target.

## Query patterns

- Prefer `aws <service> describe-*`, `list-*` and `get-*` forms.
- Shape output with `--query` and `--output json` so the report quotes facts, not a wall of
  JSON.

## Tool servers

Use an AWS MCP server or the AWS CLI, whichever the harness offers. A tool server is the
first choice where it answers the question.

## Output shape

Report the resource, its identifier, the fact you observed, the revision or time you
observed it, and anything you could not confirm.
