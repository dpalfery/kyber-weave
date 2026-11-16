---
id: standards/github
title: "github standard"
doc-type: coding-standard
status: draft
technology: github
owner: unassigned
last-reviewed: 2026-11-16
---

# github standard

## Naming and layout

- One workflow per outcome; trigger it by the job it does, not by the workflow's name.
- Job and step names are identifiers; a reader greps them.
- Workflow files live under `.github/workflows/`.

## Coding standards

- Pin every action and container image to a version or digest.
- The workflow runs the commands a contributor runs, and no others.
- Determinism: a run with no source change produces the same result.

## Testing

- A workflow is tested by running it on a branch before it gates the default branch.
- Tests that pin workflow behaviour read the YAML and assert the trigger, permissions and
  command it declares.

## Commands

- `gh workflow run`, `gh run watch` and `gh run view --log` inspect and start a run.
- `gh pr create` and `gh pr view` handle the pull request.
- `pulumi stack output` supplies the values a deployment step consumes.

## Security and data handling

- Declare `permissions:` explicitly and grant the least the job needs.
- A secret is a repository or environment secret reference, never a literal.
- Untrusted input never reaches a shell; pass it through `env:` and quote it.
- Pin the supply chain: action versions, image digests, and the runner's own tools.

## Done checklist

- The workflow ran on the branch and its logs are clean.
- Permissions are the least that works.
- No secret appears in the diff, the logs or the report.
