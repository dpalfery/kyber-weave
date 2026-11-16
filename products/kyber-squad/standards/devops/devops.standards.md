---
id: standards/devops
title: "devops standard"
doc-type: coding-standard
status: draft
role: devops
owner: unassigned
last-reviewed: 2026-11-16
---

# devops standard

## Naming and layout

- Pipeline files live where the platform expects them, one workflow per outcome.
- Job, step and environment names are identifiers a reader can grep.

## Coding standards

- A pipeline runs the same commands a contributor runs, and pins every action or image to a
  version.
- Secrets come from the platform's secret store; never from a file in the repository.

## Testing

Test the pipeline by running it on a branch before it gates the default branch. A workflow
that has never run is not evidence.

## Commands

- The platform's own CLI is the inspection tool: `gh` for GitHub, `az` for Azure DevOps,
  `glab` for GitLab, and the Jira CLI or REST API for Jira.
- Preview a change before it applies: a workflow diff, a pipeline run, or a dry run.

## Security and data handling

- Least privilege for every token: the job gets the scope it needs and no more.
- Untrusted input never reaches a shell unquoted.

## Done checklist

- The pipeline ran on the branch, not only on paper.
- Every secret is a reference, never a literal.
- The change is reversible.
