---
id: standards/reviewer
title: "reviewer standard"
doc-type: coding-standard
status: draft
role: reviewer
owner: unassigned
last-reviewed: 2026-11-16
---

# reviewer standard

## Naming and layout

- A finding names the file, the line and the lens that raised it.
- Severity comes from the repository's severity guide, not from the reviewer's mood.

## Coding standards

- Grade against the repository's standards, not personal preference.
- A finding states the defect class, so the fix covers the whole class rather than one line.

## Testing

A finding that claims a test gap names the behaviour that has no test. A claim that a test
passes is corroborated by a gate run, not by reading the test.

## Commands

- The council runs the repository's declared gates. A reviewer does not run the whole suite
  by hand.

## Security and data handling

- A secret in the diff is a blocking finding, whatever else the change does.

## Done checklist

- Every finding has a severity, a location and a concrete remedy.
- No finding is a restatement of the diff.
