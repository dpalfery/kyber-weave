---
id: standards/shared-rules
title: "Shared rules"
doc-type: rule
status: draft
owner: unassigned
last-reviewed: 2026-11-16
---

# Shared rules

Rules that hold for every technology. Each technology's own rules and skills add to these;
they never replace them.

## Precedence

- Technology rules beat parent rules, parent rules beat these shared rules, rules beat
  skills, and skills beat a standard's general guidance.

## Security and data handling

- Never edit environment secrets or deployment credentials, and never print, log or commit a
  secret value.
- Validate a value that arrives from outside the trust boundary once, at its entry point,
  before it reaches a file, database, shell or collection API.
- Do not widen a security boundary (capability, permission scope, CSP) to make a change
  work.

## Testing

- Tests run in any order and do not depend on each other; each test arranges its own data
  and cleans it up.
- A new behaviour ships with the test that would fail without it. A bug fix ships with a
  regression test named for the scenario that broke.
- Never mock the database in an integration test; use a real engine the test owns.
- No test asserts only that a call does not throw, and no test sleeps for an arbitrary
  delay.
- Unit tests cover domain logic and service classes. Infrastructure, a database and HTTP
  belong to integration tests.
- A test for a value crossing the trust boundary has one case per input-site column.
