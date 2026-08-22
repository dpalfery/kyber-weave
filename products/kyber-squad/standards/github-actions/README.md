---
id: standards/github-actions
title: github-actions coding standard
doc-type: coding-standard
status: draft
technology: github-actions
owner: unassigned
last-reviewed: 2026-08-16
---

# github-actions coding standard

How CI workflows are written in this repository. Agents and skills resolve this document as
`<github-actions-coding-standard>`, so it outranks the defaults a portable agent shipped with.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`.

## Supply chain

- **Third-party actions are pinned to a full commit SHA**, with the version in a trailing
  comment. A tag is mutable: the action you reviewed is not necessarily the action that runs.
  First-party `actions/*` may be pinned to a major tag if the repository says so here.
- **`permissions:` is declared explicitly**, at the workflow and narrowed per job. Start from
  `contents: read` and add what a job proves it needs.
- **Untrusted input never reaches a shell.** A PR title, branch name, or issue body
  interpolated into `run:` is a script injection — pass it through `env:` and quote the
  variable.
- `pull_request_target` and `workflow_run` run with the base repository's secrets. Do not
  check out and execute fork code in them.

## Secrets

Secrets come from the secrets store, are passed by `env:`, and are never echoed, written to an
artifact, or included in a step summary. A workflow that needs to print a secret to debug it
needs a different debugging approach.

Prefer OIDC federation to a long-lived cloud credential stored as a secret.

## Structure

- One workflow per concern — validate, release, publish — rather than one file with a matrix
  of conditionals.
- Shared sequences become reusable workflows or composite actions. A block copied into three
  workflows will be fixed in one of them.
- `concurrency` with `cancel-in-progress` on pull-request workflows, so a force-push does not
  leave two runs racing.
- Independent jobs run in parallel; a `needs:` that is not a real dependency is wall-clock
  time spent for nothing.

## Speed

Cache dependencies with a key that includes the lockfile hash. A cache that never invalidates
is worse than none — it hides a broken restore behind a stale hit.

## Gates

The checks that must pass are required in branch protection, not merely present in the file. A
gate that can be skipped by merging anyway is documentation, not a gate.

Failing steps fail the job: no `continue-on-error` to make a red workflow green, and no
disabled test without a linked issue and a date.

## Determinism

Pin the runner image, the language version, and the tool versions. `latest` in CI means the
build that passes today fails on a morning nobody changed anything.
