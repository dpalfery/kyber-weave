---
id: archive/todo/claude-renderer-ask-narrowing
title: Claude and Pi renderers leave architect and product-owner unable to persist their artifacts
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-24
status: superseded
---

# Claude and Pi renderers leave architect and product-owner unable to persist their artifacts

> [!NOTE]
> **Closed 2026-09-24 without a renderer change.** The owner chose the simple fix:
> `architect` and `product-planning` now hold `filesystem.write: allow` in
> `products/kyber-squad/profiles/capabilities.yml`. The plan, spec and todo boundary is
> instruction-only, stated in both agent bodies. Every target, Claude, Pi and ZCode
> included, now renders the write tools. A path-scoped lattice and hook-based guards were
> considered and rejected as over-engineering. On ZCode and Pi a guard that fails to run
> lets the write through, so it bought little over the instruction. `architect` keeps
> `process.execute: ask`.

This is **context for planning the work, not a plan** — it states what is known, what is
assumed and unverified, and where the seam is. It does not sequence tasks or commit to an
implementation.

## Why this exists

Found on 2026-09-14 while running the conductor flow in Claude Code against
[pi.md](pi.md). `squad install --target claude` rendered an `architect` subagent with no
`Edit`, `Write`, or `Bash`. The conductor's plan path requires `architect` to save a Draft plan
under the directory named by **<plan-index>** and re-run `docs validate` and `docs drift`.
The rendered agent can do neither, so the plan path stops at its first write.

`product-owner` has the same problem. Its `product-planning` profile marks
`filesystem.write: ask`, so the spec path cannot save requirements, design, or tasks either.

## What is known

- `products/kyber-squad/profiles/capabilities.yml` sets `filesystem.write: ask` and
  `process.execute: ask` on `architect`, and `filesystem.write: ask` on `product-planning`. The
  comments say why: the lattice has no path scoping, and `ask` makes a target that cannot
  express it narrow to deny rather than open the whole tree.
- `ClaudeRenderer` (`src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs`) grants a tool only
  on `allow`. It narrows `ask` to deny and records a `safety-narrowed` `SquadDegradationRecord`,
  because `permissionMode` applies to the whole subagent, not to one capability.
- So the narrowing is intentional and recorded. The gap is that no Claude-side mechanism
  restores the narrow grant the contract actually needs, so two headless roles lose their only
  write.
- Copilot already has a target-specific envelope for this case: `architect-copilot`, referenced
  through `copilot-capability-profile` in `products/kyber-squad/schemas/agent.schema.json`.
  Claude has no equivalent field.
- Verified 2026-09-14 against `code.claude.com/docs/en/sub-agents`:
  - Subagent frontmatter supports `hooks`.
  - A `PreToolUse` hook receives `tool_input` as JSON on stdin and blocks a call by exiting 2.
  - `tools` does not accept permission-rule syntax such as `Edit(path)` or `Bash(cmd *)`.
  - Edits to an existing `.claude/agents/` file apply without a session restart.

## What is assumed and needs verification, not trusted as-is

- **Whether withholding is still the right default.** [requirements.md](../../kyber-squad/requirements.md)
  defines `safety-narrowed` as "the target cannot prompt the user". Claude Code can prompt: a
  subagent's listed tools go through the parent session's permission mode. The real risk is a
  parent running `acceptEdits` or `bypassPermissions`, where a listed `Write` becomes a silent
  whole-tree grant. The definition and the renderer's reasoning should agree.
- **Hook portability.** A path-scoping hook needs a script and a command path that works for a
  project install and a `--global` install. Whether `$CLAUDE_PROJECT_DIR` expands in a
  subagent-frontmatter hook command is unverified. Shipping a script also adds a
  receipt-managed file the renderer does not produce today.
- **Where validation belongs.** `architect` needs `process.execute` only for `docs validate`
  and `docs drift`. The choice is between a `Bash` grant guarded by a command-allowlist hook
  and moving that check to `task-reviewer`, which already holds `Bash`.

## The code seam

- `ClaudeRenderer` permission mapping and `BuildDegradationRecords`.
- Either a `claude-capability-profile` field, following `copilot-capability-profile` through
  `agent.schema.json`, `SquadSourceLoader`, and validation, or path scoping in the capability
  lattice itself so every target benefits.
- If hooks are the mechanism: the renderer emits `hooks:` frontmatter and the guard script, and
  `SquadRendererRegistry` output identities include the script.
- The Claude row of the degradation table in [requirements.md](../../kyber-squad/requirements.md)
  and the lattice section of [architecture.md](../../kyber-squad/architecture.md).

## Local workaround in use

In the worktree where this was found, the untracked `.claude/agents/architect.md` was hand-edited:

- It gained `Edit` and `Write`, plus a `PreToolUse` hook on `Edit|Write`.
- The hook (`.claude/hooks/architect-plans-only.sh`) resolves the requested path and exits 2
  unless it falls under `docs/plans/`.
- `Bash` was not granted. `task-reviewer` runs the documentation checks on the saved plan.

## Pi

`PiRenderer` has the same narrowing, for a different reason. Pi has no permission prompts
(P2 of the archived Pi plan), and `tools` is binary: a tool is listed or it is not. Every
`ask` therefore withholds its tools and records `safety-narrowed`. `architect` and
`product-owner` lose `edit`/`write`/`bash` on Pi exactly as they lose `Edit`/`Write`/`Bash`
on Claude. There is no Pi-side hook or path-scoped grant to restore the narrow write those
roles need.

A fix that only teaches `ClaudeRenderer` a hook or a `claude-capability-profile` leaves Pi
broken. Prefer a lattice-level path-scoped `ask` (or an equivalent per-target envelope on
both renderers) so both harnesses regain the write without opening the whole tree.

## How to verify

- Extend `tests/KyberWeave.Tests/ClaudeRendererContractTests.cs`. Render the checked-in
  `products/kyber-squad` corpus and assert that `architect` and `product-owner` can write their
  artifact directories and nothing else.
- In a scratch project, run `squad install --target claude` and have `architect` save a plan
  under the plan index, then write a file outside it. The first write must succeed and the
  second must be blocked, including when the parent session runs in `acceptEdits`.
