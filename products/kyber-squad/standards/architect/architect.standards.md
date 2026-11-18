---
id: standards/architect
title: "architect standard"
doc-type: coding-standard
status: draft
role: architect
owner: unassigned
last-reviewed: 2026-11-16
---

# architect standard

## Naming and layout

- A plan names its tasks in execution order, one task per file-scoped change.
- Each task names the files it touches, its acceptance criteria and its test command.

## Coding standards

- Plan against the repository's own standards; cite the technology a task is written in
  rather than restating its rules.
- A change that spans two unrelated technologies is two tasks with a dependency, not one
  task with two technologies.

## Testing

A task is test-first when it changes code. The plan states the test command and the classes
that prove the task; a run that reports zero tests proves nothing.

## Commands

- Plan tasks around the repository's declared gates; do not invent a command.
- When the plan is approved, write the task file; never hand-edit it.

## Security and data handling

- A plan that needs a secret in a task is wrong: name the configuration key, never the value.

## Done checklist

- Every task has a command, acceptance criteria, and one capability or explicit role.
- The decisions table records each choice that a later reader would otherwise re-argue.
