---
id: plans/2026-09-24-claude-scoped-artifact-writes
title: Restore scoped plan and spec writes for architect and product-owner on Claude
doc-type: plan
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-24
development-mode: test-first
---

# Restore scoped plan and spec writes for architect and product-owner on Claude

**Status:** Draft — awaiting owner decisions D2 and D4
**Date:** 2026-09-24
**Development mode:** test-first
**Goal:** On the Claude target, let `architect` save a plan and `product-owner` save a spec
without granting either a whole-tree write, including when the parent session runs in
`acceptEdits`, `auto`, or `bypassPermissions`. Source: the
[ask-narrowing todo](../todo/claude-renderer-ask-narrowing.md).

## Verified facts (code.claude.com, 2026-09-24)

- Subagent frontmatter supports `hooks` (all events) and `disallowedTools`. It has no
  `permissions` field, and `tools` accepts no `Edit(path)` specifiers.
- Under a parent in `bypassPermissions`, `acceptEdits`, or `auto`, the subagent's
  `permissionMode` is ignored. A listed `Write` is therefore a silent whole-tree grant. This
  is why a `claude-capability-profile` envelope with `filesystem.write: allow` is rejected: it
  would break the KS-002 no-escalation rule.
- A `PreToolUse` hook receives `tool_input.file_path`. Exit 2 blocks the call, and a JSON
  `allow` cannot override it.
- On Windows, hook commands run in Git Bash if it is installed, otherwise PowerShell.
- Project-subagent hooks run only after the workspace is trusted.
- **Not documented:** whether `PreToolUse` hooks still fire under `bypassPermissions`. T0 gates
  the whole plan on this.

## Decisions

| Id | Decision |
|---|---|
| D1 | The lattice gains path scope. A profile may declare `filesystem.write: { decision: allow, scope: [<plan-index>] }`, naming Config Reg properties, never literal paths. A renderer that cannot enforce the scope treats the grant as `ask`. Every target except Claude therefore renders exactly what it renders today. |
| D2 | **Open.** Claude enforces the scope with a frontmatter `PreToolUse` hook on `Edit\|Write\|NotebookEdit`. Its command is a new CLI verb, `kyber-weave squad guard write --scope <props>`, not a shipped script: it works in both bash and PowerShell, adds no file for the install receipt to track, and works for `--global` installs. It resolves the Config Reg from the project's `AGENTS.md` at runtime and denies on any doubt: path outside scope, `..` traversal, symlink escape, missing Config Reg. Cost: `kyber-weave` must be on PATH at session time, which `squad doctor` checks. |
| D3 | `architect` keeps `process.execute` narrowed on Claude: no `Bash`, because Bash can write files through redirection (the `capability-not-isolable` finding). The architect reports the saved plan as "not validated", and `task-reviewer`, which already holds `Bash`, runs `docs validate` and `docs drift`. This matches the local workaround described in the todo. |
| D4 | **Open.** Scope is Claude only. Pi and ZCode keep `safety-narrowed`, and the todo stays open for them. T0 records whether Pi's extension `tool_call` event can block a write, as input for that follow-up. |

## Tasks

| # | Task | Done when |
|---|---|---|
| T0 | Live check in a scratch project: the frontmatter hook fires and exit 2 blocks under `default`, `acceptEdits`, `auto`, and `bypassPermissions`; behaviour with the workspace not yet trusted. | Evidence recorded here. **Stop and re-plan** if any mode skips the hook. |
| T1 | Red tests: scoped-write parsing and validation (unknown property rejected), guard allow/deny cases, the Claude contract test for architect and product-owner (Edit/Write plus hook, no Bash), and a no-change snapshot for every other target. | Tests fail for the right reason. |
| T2 | Model, loader, `capability-profiles.schema.json`, and validator support the scoped write. | T1 model tests green. |
| T3 | `squad guard write` in Core and the CLI. | Guard tests green. |
| T4 | `ClaudeRenderer` renders a scoped allow as tools plus the hook. The `safety-narrowed` record is dropped for that capability. | Claude contract test green. |
| T5 | Other renderers map a scoped write to `ask`. | No-change snapshot green. |
| T6 | `capabilities.yml`: architect is scoped to `<plan-index>`, product-planning to `<specification-index>`. The architect body adds the validation hand-off from D3. | Squad source validates. |
| T7 | Docs: requirements KS-002 and the Claude degradation row, the architecture lattice section, the todo narrowed to Pi and ZCode, the plan index. | `docs validate --merge-ready` and `docs drift` clean. |
| T8 | Live acceptance: `squad install --target claude`. The architect saves a plan; a write outside the scope is blocked under `acceptEdits` and `bypassPermissions`. | Evidence recorded. Full gate suite green. |

## Risks

- A hook skipped in some permission mode makes D1 an escalation. T0 exists to catch this.
- A guard bug is a security bug. It denies by default, and T1 carries the traversal and
  symlink cases.
