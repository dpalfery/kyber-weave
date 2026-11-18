---
id: standards/github-actions/rules
title: "github-actions rules"
doc-type: rule
status: current
technology: github-actions
owner: dpalfery
last-reviewed: 2026-11-16
---

# github-actions rules

## Workflows in this repository

- CI runs the full test suite on a pull request. A dev agent does not; its evidence is the
  task gate on its own change.
- Every gate the review council runs is declared in `.kyber-weave/kyber-weave.yml`. A workflow
  invokes the declared command rather than spelling out a second one.
- A workflow file is linted with `actionlint`; a shell script with `shellcheck`.
- Secret and token values never appear in a workflow, a log or a rendered file.

## Test-first

- A workflow change ships with the test that pins its trigger, permissions and command. Those
  tests live in `tests/KyberWeave.RepoPolicy.Tests`.
