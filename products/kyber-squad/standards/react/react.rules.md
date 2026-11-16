---
id: standards/react/rules
title: "react rules"
doc-type: rule
status: draft
technology: react
owner: unassigned
last-reviewed: 2026-11-16
---

# react rules

## Components

- Prefer the repository's design-system components when they exist; do not re-implement a
  button, dialog or field the system already provides.
- Implement the error and loading states of every asynchronous view. A spinner that never
  resolves and a blank panel are both defects.

## Security and data handling

- Never render an untrusted value through a DOM-injection escape hatch. Content from the
  server or a user is text unless the standard says otherwise.
