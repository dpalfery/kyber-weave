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
`<github-actions-coding-standard>`.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

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

- Pull-request workflows trigger on `pull_request` and run the tests and linters.
- A declared coverage or quality threshold fails the build when it is missed.
- An AI review bot's API key is a secret like any other, the bot runs without elevated
  privileges, and its output is structured (JSON) so a gate can read it.

## Deployment

- **Build and deploy are separate jobs.** Build and test in one job; deploy depends on it and
  runs only when it passed. Every path from a push to production goes through a test gate.
- **Deploy secrets live in environment secrets**, not repository secrets, so they scope to the
  environment that uses them. Mask any computed secret-like value with `::add-mask::`.
- **Cloud sign-in is federated.** Authenticate to Azure with `azure/login` using OIDC
  (`client-id`, `tenant-id`, `subscription-id`); push container images to the registry with the
  same federated identity rather than a username and password.
- **Production requires a named reviewer** in the GitHub environment's protection rules.
- **Deploy jobs take a `concurrency` group per environment** without `cancel-in-progress`, so
  two deployments to one environment never interleave and neither is abandoned half-applied.
- **A deploy job is safe to re-run** against the same environment.
- **Container images are multi-stage builds.**
- **Test results and build artifacts are uploaded** with an explicit `retention-days`; .NET test
  steps log with `--logger trx` so the results can be published.

## Determinism

Pin the runner image, the language version, and the tool versions. `latest` in CI means the
build that passes today fails on a morning nobody changed anything.

Jobs run on a Linux runner unless the job needs another OS — Windows for a MAUI publish, macOS
for iOS signing — and say why when they do.
