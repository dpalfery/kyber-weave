---
id: standards/shared-rules
title: "Shared rules"
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-11-16
---

# Shared rules

Rules that hold for every technology in this repository. Each technology's rules and skills
add to these; they never replace them.

## Precedence

- Technology rules beat parent rules, parent rules beat these shared rules, rules beat
  skills, and skills beat a standard's general guidance.

## Security and data handling

- Never commit a secret, a token or a deployment credential, and never print one in a report
  or a log.
- A value from outside the trust boundary is validated once, at its entry point, before it
  reaches a file, database, shell or collection API.
- Never widen a boundary — capability, permission scope, CSP — to make a change work.

## Testing

- No dev agent runs the full suite. Run the classes that cover the change, and check the run
  reports a non-zero test count. The council and CI run the whole suite.
- A test runs in any order and arranges its own data. Never mock the database in an
  integration test.
- Never weaken, skip or delete a test to make a gate pass.

## Documentation

- Editing anything under `docs/` means re-running `docs validate . --merge-ready` and
  `docs drift .`; the corpus stays at zero findings.
- Rule ids (`KW-*`) are permanent. Add a new id; never reuse or renumber one.
- A persisted plan belongs under `docs/plans`; an archived plan under `docs/archive/plans`.
