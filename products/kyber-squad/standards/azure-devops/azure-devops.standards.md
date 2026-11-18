---
id: standards/azure-devops
title: "azure-devops standard"
doc-type: coding-standard
status: draft
technology: azure-devops
owner: unassigned
last-reviewed: 2026-11-16
---

# azure-devops standard

## Naming and layout

- One pipeline per outcome, named for the outcome; stages and jobs carry the environment in
  their name.
- YAML lives beside the code it builds, at the path the pipeline declares.

## Coding standards

- Pin every task and container image to a version.
- The pipeline runs the commands a contributor runs.
- A stage that deploys requires an approval on the environment.

## Testing

Run the pipeline on a branch before it gates the default branch, and keep the run's log as
the evidence.

## Commands

- `az pipelines run`, `az pipelines runs show` and `az pipelines runs list` inspect runs.
- `az devops` manages projects, boards and repositories.

## Security and data handling

- Use variable groups backed by Key Vault for secrets; never a literal in YAML.
- Grant the pipeline's service connection the least scope that works.
- Untrusted input is passed through a variable and quoted, never interpolated into a script.

## Done checklist

- The pipeline ran on the branch and the log is clean.
- Approvals and permissions are the least that work.
- No secret appears in the diff or the log.
