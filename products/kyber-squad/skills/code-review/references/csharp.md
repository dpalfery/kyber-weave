# C# review checklist

Review C# changes against the path declared as **<csharp-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse.

When **<csharp-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no C# standard declared" or "C#
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, this checklist adds nothing; the lens's own concern still
applies.

## Review procedure

- **ReSharper InspectCode** is a declared gate, not a reading exercise. Its findings arrive
  through the `static-analysis-triage` lens; the `resharper-clt` skill owns how the gate is
  declared and what its inspections mean. Do not re-derive by eye what the tool already
  reported by rule id.
- **Cross-file duplication is not the analyzer's.** `DuplicatedStatements` sees one
  statement's branches and `RedundantOverload` one method group; neither looks across files.
  The `duplicate-implementation` lens and its CodeGraph-backed gate hold that concern, and
  `prior-art` holds the type-level version of it.
