---
id: standards/aws
title: "AWS coding standard"
doc-type: coding-standard
status: draft
technology: aws
owner: unassigned
last-reviewed: 2026-11-16
---

# AWS coding standard

How AWS infrastructure and read-only inspection are done in this repository. Agents resolve
this document as `<aws-coding-standard>`.

## Naming and layout

- Prefix every resource with the environment and workload, using the repository's naming
  document when it has one.
- Infrastructure as code lives with the program that owns it; console changes are a hotfix
  path, back-ported to code immediately.

## Coding standards

- Prefer managed services and the least-privileged access that still does the job.
- Every stack is idempotent: a second apply changes nothing.
- Tag every resource with its owner and environment.

## Testing

Test infrastructure code with the provider's own preview or plan step, and assert the
resource shape the change is meant to produce. A clean preview is evidence only of what
would change, not of correctness.

## Commands

- `aws sts get-caller-identity` confirms the account and role before any inspection.
- The infrastructure tool's preview command is the review step; apply runs only after a
  reviewed preview.

## Security and data handling

- Never print, log or commit an access key, secret key, session token or secret value.
- Prefer short-lived credentials from an identity provider; never create a long-lived user
  key when a role will do.

## Done checklist

- The caller identity was confirmed.
- The preview or plan was reviewed and contains only the intended change.
- No secret appears in the diff, the logs or the report.
- The build, test and format commands the pipeline uses were run on the change.
