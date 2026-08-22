# Lens: static-analysis-triage

**Runner: `review-triage`.** This is a triage lens — its input is the analyzer, linter, compiler, or type-checker gate output, and the work is attributing that output to the change rather than judging code. It runs on the fast model profile. If you are `review-lens`, you were misrouted; say so rather than reviewing the code yourself.

## Applicability

Applies whenever the analyzer, linter, compiler, or type-checker gate produced output. Runs
**after** that gate.

Skip only when no such gate is configured for this repository — and say so, because a
repository with no static analysis is worth the reviewer knowing about.

On .NET repositories the analyzer gate is normally ReSharper `InspectCode`, declared under
`review.gates` and documented by the `resharper-clt` skill — read that skill for the report
format and the inspections it commonly emits. You consume its output; you do not run it
yourself, and you never run `cleanupcode`, which rewrites source.

## What this lens owns

Turning gate output into review findings: every diagnostic in a file the diff touched,
triaged, attributed, and reported by rule identifier.

You do not re-derive analysis the tools already did. You determine which of their output the
change is responsible for, and which of it matters.

## What to look for

**Attribute each diagnostic to the change.** A diagnostic on a line the diff touched belongs
to this change. A diagnostic elsewhere in a touched file may be pre-existing — check the base
revision before reporting it. Getting this wrong in either direction is costly: unattributed
pre-existing noise buries real findings, and a genuinely introduced diagnostic dismissed as
pre-existing is exactly how a codebase accumulates them.

**Report by rule identifier, always.** The identifier is what makes a finding actionable,
searchable, and suppressible. "Analyzer warning" is not a finding; the specific rule, its
identifier, and its location is.

**Severity from the rule, then from consequence.** Security and correctness rules are
`critical` or `major`. Maintainability and style rules are `minor` unless the specific
instance has a consequence you can describe — in which case describe it, because that
consequence is the finding rather than the rule.

**Suppressions added in this diff.** A suppression is a claim that the analyzer is wrong
here. Every one added by this change needs a stated justification, and a suppression with no
reason, or with a reason that does not survive reading the code, is a finding at the severity
of the rule it silences. A blanket suppression at file or project scope is a finding
regardless.

**Warnings-as-errors posture.** Where the repository treats warnings as errors, any warning
is a build failure and is reported at that weight. Where a project adds to its ignore list,
that change is subject to the same justification standard as an inline suppression.

**New diagnostics not yet failing.** A rule configured below error level, newly triggered by
this change, is a real finding — it is a defect the build chose not to stop for.

## What this lens must not report

- Diagnostics in files the diff did not touch.
- Pre-existing diagnostics on untouched lines within touched files.
- Disagreement with a rule's existence. The repository enabled it; that decision is not
  under review here.
- Findings you produced by reading the code yourself rather than from gate output. Other
  lenses own that; your authority comes from citing the tool.
- Formatter output. Whitespace is not a review finding.
