---
id: adr/0071-the-arbiter-enforces-the-architect-shell-allowlist-on-claude
title: The Arbiter enforces the architect shell allowlist on Claude as a hook guard, rendered in the user-level agent file
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-12
component: KyberArbiter
decided-by: []
supersedes: []
code-refs:
  - ArchitectShellGuard
  - ClaudeRenderer
  - ArbiterHookWiring
  - ClaudeHookAdapter
---

# ADR 0071: The Arbiter enforces the architect shell allowlist on Claude as a hook guard, rendered in the user-level agent file

## Status

Accepted, 2026-11-12. It refines [ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md) and
[ADR 0070](0070-the-user-level-claude-hook-evaluates-unless-a-project-hook-covers-it.md). It adds a
hook guard beside the existing read and conductor guards. It does not change the decision engine, the
rules, the ledger, or the project-scope renderers for any other agent.

## Context

The architect's shell boundary was instruction-only. `architect.md` names the command set and ends
with "The lattice cannot enforce this list; you keep it." The capability lattice grants the architect
`process.execute: allow` so it can run the docs checks and `codegraph explore` inside a worktree, and
the lattice has no command or path scoping. With `filesystem.write` and `network.read` also allowed, the
shell could build, install, fetch, or write outside the plan, spec and todo directories. Nothing
enforced the boundary on any harness.

Three facts shape the choice of mechanism:

- A Kyber-Weave rule is evaluated by the rule engine for dispatch triggers only. Every decision becomes a
  ledger event, and repeat counts of escalations stop tasks ([ADR 0070](0070-the-user-level-claude-hook-evaluates-unless-a-project-hook-covers-it.md)).
- Shell calls are frequent and carry no claim key, so a global and a project hook could both record one call.
- Claude Code runs the frontmatter hooks of a user-level sub-agent (`~/.claude/agents/`) without the
  workspace trust step while that sub-agent is active, and a project sub-agent of the same name outranks the
  user one. So a hook in the agent file fires once, and a hook in the user-level `settings.json` fires for
  every agent.

## Decision

1. **A guard, not a rule.** `ArchitectShellGuard`, permanent id `KW-ARB-SHELL-001`, is a stateless hook
   guard beside `ReadGuard` and `ConductorGuard`. It denies an architect `Bash` or `PowerShell` call outside
   the documented command set before the call runs. A hook guard is not a rule: hosts cannot tune it by id.
   `arbiter.enabled: false` turns the whole Arbiter off, and with it this guard. Each decision writes a hook-log
   line, not a ledger event.
2. **Matching is whole-command, anchored, allow by documented form, and deny on anything it cannot parse.**
   A command is split on unquoted `&&`, `||`, `;`, `|` and newline. Each part must match a documented form. A
   structural pre-filter runs first and refuses non-ASCII characters on both tools, control characters, a line feed
   inside quotes, an unquoted `#` comment, and any backslash in Bash; a backslash is literal in PowerShell.
   Substitution, backticks, `$`, process substitution, subshells, braces, heredocs, background `&`, redirects
   other than `2>&1` and `2>/dev/null` (the latter in Bash only), environment-prefix assignments, shell keywords such as loops and
   conditionals, and wrapper commands (`bash`, `sh`, `eval`, `xargs`, `env`, `sudo` and similar) are denied.
   So are `git -c` and any git global option other than `--no-pager` and `-C`, `git … --output`,
   `find -exec`, `-ok` and `-delete`, and `tail -f`. `sort` is matched against an allowlist of flags, so
   `sort -o` is denied. `rg` is matched against a denylist of flags, so `rg --pre` is denied. `touch` is
   allowed only for a `.md` or `.json` file under the plan, spec and todo directories, with the operand resolved
   under the plan, spec and todo folders in both readings: the payload's working directory, and the directory after the call's one allowed `cd`. An unquoted glob character is refused, and so is a quoted one, because `touch` takes literal operands only.
3. **Claude only, for the `Bash` and `PowerShell` tools, at project and user scope.** `ClaudeRenderer.BuildArbiterHooks`
   renders a `PreToolUse` entry with matcher `^(Bash|PowerShell)$` into the architect's agent file at project scope
   and at user scope. The hook command is the Arbiter's `ArbiterHookWiring` command line.
4. **The user-scope copy is an exception to Req 22.2.** Req 22.2 renders nothing per agent at user scope. The
   architect's shell hook is rendered there in the user-level agent file, because the alternative is worse. A
   matcher in the user-level `settings.json` would start the hook process for every shell call of every agent
   on the machine, and it would fire twice beside a project agent-file hook. In the agent file exactly one
   file's hooks fire. At user scope the event is evaluated under the project configuration when the project
   enables Arbiter, under the user configuration when the project has none, and passed when the project sets
   `arbiter.enabled: false`.
5. **Enforced at once.** There is no shadow mode and no setting of the guard's own, like `ReadGuard` and
   `ConductorGuard`. The only off switch is `arbiter.enabled: false` in the project configuration, which turns the
   whole Arbiter off, and with it this guard. A wrongly denied command costs one `STATUS: BLOCKED` round trip,
   which is the accepted cost.
6. **The other harnesses stay instruction-only.** Copilot in VS Code (the terminal tool name in its payload is
   undocumented), Copilot CLI (its hooks gate only `task`), Codex, Cursor, OpenCode, Kilo, Pi, Factory, Devin
   and Antigravity have no verified shell-tool hook here. They are follow-ups. On them the architect's list stays
   instruction-only.

## Alternatives Considered

- **A shipped rule in `default-rules.yml`.** Rejected. The rule engine evaluates dispatch triggers only, so a
  shell call would need a new trigger family. Every decision would become a ledger event, and repeat counts
  would then stop tasks. Shell calls have no claim key, so a global and a project hook could both record one
  call. The shipped-rule count and its pinning tests would change, and a host could tune or disable a security
  boundary by rule id. A hook guard avoids all four.
- **A `^(Bash|PowerShell)$` matcher in the user-level `settings.json`.** Rejected. It starts the hook process for
  every shell call of every agent on the machine, and it fires twice beside a project agent-file hook.
- **Shadow mode, or a per-guard switch.** Rejected. A boundary that a host can switch off on its own, or that only
  records what it would have denied, does not enforce the list. The cost of a wrong denial is one round trip.
- **Enforcing on every harness now.** Rejected for this change. Several harnesses have no verified shell-tool
  name or no pre-call hook for the shell tool, and an unverified matcher would be a claim the harness does not
  honour. Each one is a follow-up with its own evidence.

## Why an ADR

The decision constrains future work. Every change to the allowed command forms touches three artifacts
together: the guard's documented-forms table, the architect command-set clause, and the sync test. Moving the
hook out of the agent file, or back into `settings.json`, would have to be reworked against the trust and
precedence rules above. Viable alternatives were rejected, and the rejection is not self-evident from the code.

## Consequences

- The architect's command list is enforced on Claude at project and user scope, and it stays instruction-only on
  the other harnesses until each follow-up lands.
- The user-level agent file now carries one per-agent hook. Its behaviour depends on Claude Code's documented
  sub-agent precedence and on hooks in frontmatter running only while the sub-agent runs. The design relies on
  those documented rules.
- Widening the allowed list is a three-part change: one documented-forms row, the `architect.md` clause, and the
  sync test. It also refreshes the architect body digest, because the body changes.
- A denial is not recorded in the ledger, so `arbiter audit` and `arbiter tune` do not see it. The denial is
  found in the harness hook log.
- The worker read guard does not cover the `PowerShell` tool, because `ClaudeHookAdapter.IsReadTool` and
  `ClaudeRenderer.ReadGuardMatcher` name `Bash` only. This is a known gap and a follow-up.
- The path checks are textual, so a symlink, Bash `CDPATH` or a PowerShell backslash on a POSIX host can move the
  real target of a `touch`. The residual is an empty `.md` or `.json` file, tracked in issue #605.
