---
id: standards/python/skills
title: "python skills"
doc-type: skill
status: draft
technology: python
owner: unassigned
last-reviewed: 2026-11-22
---

# python skills

## What this folder covers

- Language, packaging, typing, environment and bug-fix decisions: the standard's Naming and
  layout, Coding standards and Commands sections.
- Procedure, below: gathering verified facts, Pylance-backed automated refactoring, and
  Pylance documentation lookups. Pylance tools are used when available; without them, use the
  type checker, a focused snippet or a test, and say what stays unverified.

## Gathering facts

Use the lightest evidence that settles the question, and escalate when it does not:

1. Language facts from code intelligence (`pylanceLSP`, `pylanceSemanticContext`): types,
   diagnostics, references, signatures, base classes, overrides, call sites. Use
   `pylanceCheckSignatureCompatibility` before or after a signature change, and
   `pylanceImports` with `pylanceInstalledTopLevelModules` for unresolved imports.
2. Workspace facts (`pylanceSettings`, `pylancePythonEnvironments`, `pylanceWorkspaceRoots`)
   for settings, the selected interpreter and roots.
3. Execution facts: a focused snippet (`pylanceRunCodeSnippet`) or a test for a value or
   input/output case.
4. Debugger facts (`pylancePythonDebug`) for a branch, exception, loop iteration, closure,
   subprocess, mutable state or frame-local value.
5. Reasoning yields hypotheses; name an assumption as a hypothesis until a tool or test
   has verified it.
6. Check generated code with `pylanceSyntaxErrors` / `pylanceFileSyntaxErrors` before calling
   it ready. To explain a diagnostic, fetch the exact rule code first.

## Automated refactoring

`pylanceInvokeRefactoring` takes one file and one refactoring name; a workspace-wide pass is
many single-file calls.

- Scope: take files the user named, or get `pylanceWorkspaceRoots` then
  `pylanceWorkspaceUserFiles` and filter to the requested folder. Keep the scope exact. A
  workspace-wide pass runs in batches (for example 50 files) and reports after each one; when
  the filtered set is far larger than the request implies, confirm the scope before the first
  call.
- Names: `source.unusedImports`, `source.convertImportFormat`, `source.convertImportStar`,
  `source.convertImportToModule`, `source.renameShadowedStdlibImports`,
  `source.addTypeAnnotation`, `source.fixAll.pylance`. Compose by running one pass across
  the whole set, for example `convertImportStar` then `unusedImports`.
- Modes: `mode="edits"` or `"string"` previews without changing files; `"update"` (the
  default) applies. Preview first for a broad or ambiguous request. A preview-only request
  ("show me", "what would change") summarizes the would-be edits and waits for confirmation;
  apply with `update` only when the user clearly asked.
- `source.fixAll.pylance` applies whatever the Pylance settings configure; use the settings
  and diagnostics tools to answer what fix-all includes.
- Track changed, no-op and error files separately; a file with no edits is a no-op, not a
  manual cleanup.
- In a VS Code chat, `update` edits editor buffers, not disk: save before a terminal check,
  and use Pylance or editor-visible tools for immediate checks.
- After a larger refactor run the most relevant validation, then report the names used, the
  scope, changed/no-op/error counts, files saved before terminal validation, and files
  skipped by the user's filter.
- A manual edit after an automated refactoring stays separate and is explained.
- Do not use it for semantic rename, reference search or signature-impact analysis.

## Pylance documentation

For a claim about Pylance settings, diagnostics or behaviour, call `pylanceDocuments` with
`action="search"`, then `action="fetch"` on the best result and on each referenced document
you need (never read referenced paths from disk). Use `pylanceSettings` for effective
settings and `pylanceLSP` for a specific Problems entry. Say when the docs do not cover it. Source search is not official product guidance: Pylance
claims come from its docs. Name the setting, diagnostic code or feature that changed the
answer.

## Hand-off

When ceding work to delivery, provide the build, test and run commands and any image or
runtime need.
