---
name: change-scope
description: "The one rule for what a change owns and what it leaves to a human: own what you touch, build what was asked, follow the patterns that exist; route everything else to the pull request notes; create a todo only when a human says to. Use when planning, reviewing a plan, reviewing code or finishing a run, whenever you decide whether something is this change's work, an extra the request did not ask for, or a note for the owner. Do not use to judge severity or correctness itself: that is the code-review skill's lens charters."
license: MIT
metadata:
  author: dpalfery
  version: 0.2.0
---

# Change Scope

**Own what you touch, build what was asked, and follow the patterns that exist.**

- **Own what you touch.** If the change edits or extends code, it fixes the defects, the breaches of a documented rule or standard, the stale documents and the duplication in that code, whatever their origin. Never defer them, and never call them out of scope to keep the change small. A stale document is always this change's work.
- **Build what was asked.** The change adds new infrastructure, a new test or CI strategy, a new architecture pattern, or a task the request does not need only when the request asks for it. The request is the owner's prompt, or the specification or plan the owner approved.
- **What counts as new.** Concretely, these need the request to ask for them: a new database, catalog, schema, container or service, test-only or not; a new credential or secret; a new or changed CI or deployment step; a new test fixture strategy or test project; a new messaging event, endpoint or hub method that an operation the request does not name would raise. Rating a task `hard` because of one of these is a sign it is not asked for.
- **Tests use what exists.** Tests use the repository's existing fixtures, seed data, test database and test setup. When shared data or a parallel writer makes an exact assertion unsafe, assert only what holds despite other writers (rows the test creates itself, found by a unique marker; a count that rose by at least what the test added), or leave the exact case as a `PR note:`; never build new test infrastructure to make it exact.
- **Follow the patterns that exist.** A fix that departs from the repository's documented patterns without the request asking for it is itself a finding.

## The pull request notes

A risk whose fix the request did not ask for, or one that sits in a flow the feature does not change, is not this change's work and not a finding: it goes in the pull request notes for a human to decide. Write it as one line, `PR note: <where> — <what> — <why it is left to a human>`. Never raise a finding against a fix another finding would add: judge what the change does, not what a fix might bring.

## Todos

An agent creates a todo only when a human tells it to. It never decides on its own to create one: what it would defer or escalate is a `PR note:` line.
