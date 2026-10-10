# SQL review checklist

Review SQL changes against the path declared as **<sql-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse.

When **<sql-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no SQL standard declared" or "SQL
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, this checklist adds nothing; the lens's own concern still
applies.
