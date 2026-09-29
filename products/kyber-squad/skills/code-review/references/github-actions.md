# GitHub Actions review checklist

Review GitHub Actions changes against the path declared as **<github-actions-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse.

When **<github-actions-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no GitHub Actions standard declared" or "GitHub Actions
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, this checklist adds nothing; the lens's own concern still
applies.
