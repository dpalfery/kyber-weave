---
id: standards/jira
title: "jira standard"
doc-type: coding-standard
status: draft
technology: jira
owner: unassigned
last-reviewed: 2026-11-16
---

# jira standard

## Naming and layout

- An issue key is permanent; a title states the outcome, not the activity.
- Labels and components are the project's own vocabulary, not invented per issue.

## Coding standards

- One issue per deliverable; a change that spans two deliverables is two issues.
- A transition records why it happened, in the comment or the resolution field.

## Testing

A bug issue carries reproduction steps and the expected result. A fix links the issue in
its pull request and its regression test.

## Commands

- Use the Jira CLI or REST API to read and transition an issue; never scrape the web UI.
- Read an issue before changing it.

## Security and data handling

- Never write a secret, token or customer data into an issue or a comment.
- Redact a log excerpt before attaching it.

## Done checklist

- The issue's state matches the work.
- The comment says what changed and links the change.
- No secret or personal data was pasted.
