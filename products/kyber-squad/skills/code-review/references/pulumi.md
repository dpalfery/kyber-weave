# Pulumi review checklist

Review Pulumi changes against the path declared as **<pulumi-coding-standard>** in the root
`AGENTS.md` registry. That document is the checklist. This file states no rule of its own,
because a rule stated here is one a host repository cannot reverse.

When **<pulumi-coding-standard>** is not declared, or the document it names is still
`status: draft`, say so in the lens output — "no Pulumi standard declared" or "Pulumi
standard is a draft" — so the reviewer surfaces it to the human. Do not substitute a
built-in checklist, and do not report the absence as clean.

Where the standard is silent, this checklist adds nothing; the lens's own concern still
applies.

## Review procedure

- The pull request should carry the `pulumi preview` output, so a replacement or deletion
  is visible before the change is applied. Ask for it when it is missing.
