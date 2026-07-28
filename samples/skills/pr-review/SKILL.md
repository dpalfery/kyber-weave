---
name: pr-review
description: Use to run the pre-merge pull request review checklist so required validations are never skipped. Use when reviewing or approving a pull request before merge. Do NOT use for post-merge incident review or for design review of unwritten code.
license: MIT
metadata:
  author: engineering-standards
  version: 1.0.0
---

# Pull Request Review Checklist

## When to use
Run before approving any pull request for merge.

## Checklist
- [ ] Tests cover the change — MUST pass.
- [ ] No secrets or credentials in the diff — MUST pass.
- [ ] Public API changes are documented — MUST pass.
- [ ] CI is green — MUST pass.

## Rules
- NEVER approve with failing CI.
- ALWAYS block on a missing test for new behavior.

## Example
A PR adds a new endpoint but no tests. Block on the missing-test item and request coverage before approving.
