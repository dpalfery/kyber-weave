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

## Running tests in this repository

- No dev agent runs the full suite. Run the classes that cover the change, plus build and
  format of the touched projects, and check the run reports a non-zero test count.
- A run that reports zero tests verified nothing. A filter that matches nothing exits 8, and
  calling that a pass is a false claim.
- Never weaken, skip or delete a test to make a gate pass.
- Tests whose subject is the repository itself (scripts, workflows, gate configuration, docs
  prose, source scans) live in `tests/KyberWeave.RepoPolicy.Tests`. Tests whose subject is the
  wording of skills and agents live in `tests/KyberWeave.PromptContent.Tests`.
- Fixtures and the targeted commands per area are in the `AGENTS.md` of each test project.
