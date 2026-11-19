---
id: standards/test/rules
title: "test rules"
doc-type: rule
status: current
technology: test
owner: dpalfery
last-reviewed: 2026-11-16
---

# test rules

## Unit tests by default

- New behaviour gets an in-memory unit test against an interface, using the fakes in the test
  support project. That is the default for every change.
- An integration test is the exception, kept for proving an adapter against the real resource. It
  lives in the integration project and runs in CI; a developer's task gate never runs it.
- A behaviour you can only test through a real resource means a seam is missing: add the seam and
  unit-test against it rather than writing a slow test.

## Running tests in this repository

- No dev agent runs the full suite. Run the classes that cover the change, plus build and
  format of the touched projects, and check the run reports a non-zero test count.
- A run that reports zero tests verified nothing. A filter that matches nothing exits 8, and
  calling that a pass is a false claim.
- Never weaken, skip or delete a test to make a gate pass.
- Tests whose subject is the repository itself (scripts, workflows, gate configuration, docs
  prose, source scans) and tests whose subject is shipped content live in their own projects,
  which the repository's `AGENTS.md` names; content tests read through the corpus seam and write
  nothing.
- Fixtures and the targeted commands per area are in the `AGENTS.md` of each test project.
