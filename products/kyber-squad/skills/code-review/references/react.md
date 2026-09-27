# React review checklist

Review React changes against the path declared as **<react-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse.

When **<react-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no React standard declared" or "React
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, this checklist adds nothing; the lens's own concern still
applies.

## Review procedure

- A UI change should come with screenshots or a short recording in the pull request
  description. Ask for them when they are missing; that is a review convention, not a
  coding rule.
