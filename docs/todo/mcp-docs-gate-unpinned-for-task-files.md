---
id: todo/mcp-docs-gate-unpinned-for-task-files
title: No test drives the docs MCP validate tool over a plan task file
doc-type: todo
component: DocGraph
status: draft
owner: dpalfery
last-reviewed: 2026-11-08
---

# No test drives the docs MCP validate tool over a plan task file

The conductor has no shell, so the only route by which it can check a plan's task file is the docs
MCP tool `docs_validate_governed_document_schema`. That tool and `kyber-weave docs validate` share
one gate — `DocsSchemaGate` constructs `PlanTaskFileValidator` for both — so the seven task-file rules
reach the conductor by construction. What is missing is a test that proves it from the MCP side.

Today the rules are pinned only at the validator: `PlanTaskFileValidatorTests` builds a document set
and calls the validator directly. Nothing asserts that the tool surface reports a finding for a
Ready plan with no task file (`KW-DOC-TASKS-006`), or for a malformed, divergent or stale file, and
nothing pins that a `--merge-ready` call reports `KW-DOC-LIFECYCLE-005` for a file left behind. The
positive case therefore rests on the shared call chain — a reading of the code, not a run.

The gap matters because the orchestrator is the caller that cannot check anything else: if the tool
surface diverged from the CLI gate, a run would keep dispatching from a task file the CLI would have
refused, and no test would notice.

**Work.** Add cases to `tests/KyberWeave.Tests/McpDocsGateToolsTests.cs` that call the validate tool
over a fixture repository holding (a) a Ready plan with no task file, expecting the `KW-DOC-TASKS-006`
warning in the tool's findings, and (b) a task file whose digest or task list disagrees with its plan,
expecting the matching error. Include the `--merge-ready` call if the tool exposes the flag, so
`KW-DOC-LIFECYCLE-005` is pinned on the route the conductor actually uses.

**Acceptance.** The tool's output carries the task-file finding for each fixture, and a run whose
filter names only these cases reports a non-zero test count. No production rule changes: this is the
missing pin, not a behaviour to build.

Found by slice 5 of the conductor run-speed programme (T26), recorded at its closeout.
