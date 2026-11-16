---
id: standards/gitlab
title: "gitlab standard"
doc-type: coding-standard
status: draft
technology: gitlab
owner: unassigned
last-reviewed: 2026-11-16
---

# gitlab standard

## Naming and layout

- One pipeline per outcome; stages are ordered by what they produce.
- Jobs are named for what they do, and a job's `rules` decide when it runs.

## Coding standards

- Pin every image and `include` to a version or digest.
- The pipeline runs the commands a contributor runs.

## Testing

Run the pipeline on a merge request before it gates the default branch.

## Commands

- `glab ci run`, `glab ci status` and `glab ci trace` inspect a pipeline.
- `glab mr create` and `glab mr view` handle the merge request.

## Security and data handling

- Use masked, protected CI/CD variables for secrets; never a literal in `.gitlab-ci.yml`.
- Grant a runner token or deploy key the least scope that works.
- Untrusted input is passed through a variable and quoted.

## Done checklist

- The pipeline ran on the merge request.
- Variables are masked and protected.
- No secret appears in the diff or the job log.
