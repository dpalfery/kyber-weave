# Python review checklist

Review Python changes against the path declared as **<python-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse.

When **<python-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no Python standard declared" or "Python
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, this checklist adds nothing; the lens's own concern still
applies.

## Review procedure

- Coverage on new code is measured against the floor the host declares under
  `review.coverage` in its Kyber-Weave configuration, as `kyber-weave review gates` reports it.
