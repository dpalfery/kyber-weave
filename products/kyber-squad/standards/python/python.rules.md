---
id: standards/python/rules
title: "python rules"
doc-type: rule
status: draft
technology: python
owner: unassigned
last-reviewed: 2026-11-22
---

# python rules

- Never infer the editor's Pylance interpreter from terminal `python` or `PATH`; read the
  selected interpreter through the editor tooling when it is available, or say it is
  unverified.
- Never patch from a familiar bug pattern before an observed value, diagnostic or failing
  test confirms this code takes that path; fix the producer of the wrong behaviour, not a
  downstream symptom.
- Never treat text search as proof of an override, re-export, alias or call-site
  compatibility; use a semantic tool or a test.
- Never say a test, breakpoint or tool would prove something: run it, or label the result
  unverified.
- Never apply an automated refactoring a second time to a file it already changed, and never
  use one for an arbitrary rewrite.

The rules that hold for every technology, including secrets and input validation, are
in the shared rules file, `shared-rules.md` at the root of the standards folders (`<standards-root>/shared-rules.md`), one level above this folder.
