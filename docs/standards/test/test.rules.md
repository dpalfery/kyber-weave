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

- New behaviour gets an in-memory unit test. In C# that test runs against an interface with the
  shared fakes the seam register names; in a React or TypeScript UI it renders the component over
  fixtures and mocked query results, as the React standard says.
- Integration and end-to-end tests are the exception. A C# integration test proves an adapter or a
  whole flow against the real resource in the integration project; a browser flow that needs the
  real browser and server is a Playwright end-to-end test. Both run in CI, never in a developer's
  task gate; `AGENTS.md` names their homes.
- In C#, a behaviour you can only test through a real disk, process or network means a seam is
  missing: add the seam and unit-test against it. The browser-only behaviour the React standard
  sends to Playwright (keyboard and focus behaviour, multi-view navigation) is the sanctioned
  exception.

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
