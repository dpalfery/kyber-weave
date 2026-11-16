---
id: standards/test/rules
title: "test rules"
doc-type: rule
status: draft
technology: test
owner: unassigned
last-reviewed: 2026-11-16
---

# test rules

## Change boundary

- Do not change application code to make a test pass. A fault in the implementation is
  reported as an open item, not fixed from a test-only task.
- Never weaken an assertion, skip a test, or assert contradictory invariants to force a
  green run.
- Report a coverage gap rather than leaving it silent: name the branch and the reason.

## Trust boundary

- A test for a value that parses or converts input from outside the trust boundary has one
  case per input-site column. A test for a reported defect covers at least three other
  inputs of its class.
