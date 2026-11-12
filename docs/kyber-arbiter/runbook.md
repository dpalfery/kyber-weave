---
id: kyber-arbiter/runbook
title: Kyber Arbiter runbook
doc-type: runbook
component: KyberArbiter
owner: dpalfery
last-reviewed: 2026-11-12
status: current
decided-by:
  - adr/0053-kyber-arbiter-three-step-decision-gates
  - adr/0054-squad-owned-blocks-in-shared-hook-files
  - adr/0071-the-arbiter-enforces-the-architect-shell-allowlist-on-claude
---

# Kyber Arbiter runbook

Operating the Arbiter: wiring hooks, granting trust, staying fail-closed, and diagnosing a
host. For what it is and why, see the [architecture](architecture.md).

## Hooks

The one-command install defaults to global scope: `kyber-weave arbiter install --target <t>`
ensures `~/.kyber-weave/kyber-weave.yml` (reusing the `docs init` scaffold), records the
installed target in `arbiter.global.targets`, sets `arbiter.enabled: true` non-destructively,
renders the target in the user scope through the existing Squad install pipeline, applies the
provider, stores the key in the OS credential store, verifies, and prints the target's trust
step. `--project` opts into a repository root instead, `--path` is valid only with it, and
`--global --project` exits `2` with `KW-ARB-INSTALL-001`. `--provider ""` means `none`. A
second run is an idempotent no-op. `squad install` / `squad update` render the same hooks
when the project's `arbiter.enabled` is true. `kyber-weave update` re-renders the same hooks after it swaps the
binaries. That refresh runs in the new release, in the finishing process the updater starts, so it renders the hook
source of the release being installed, not the templates of the release you updated from. A corrupt project registry
still refreshes the global hooks. A failed refresh does not fail the update: it prints `warning: arbiter hooks were
not refreshed: <cause>. The binaries are already updated; run kyber-weave update <version> to retry.` and exits 0,
where the cause is `the project registry could not be read: <message>` when the registry could not be read, `a program
component could not be loaded (<assembly>): <message>` for a failed assembly load, or otherwise the exception message.
A global hook resolves the project config first. When that project enables Arbiter, the user-level
hook's behaviour depends on the harness:

- **Claude** evaluates the dispatch under the project configuration unless a project hook covers it.
  The log lines, one per dispatch, are:
  - `Arbiter global scope: evaluating under the project configuration.` The decision and ledger are written
    to the project's `artifacts/arbiter/`.
  - `Arbiter global scope: deferred-to-project (project opts out: arbiter.enabled: false).` The project set
    `arbiter.enabled: false` explicitly. Nothing is written.
  - `Arbiter global scope: deferred-to-project (claimed by <owner>; project hooks that can fire: <sources>).`
    A project hook claimed the dispatch; `<owner>` is the claim's owner line and `<sources>` the project hook
    files that could have fired.
  - `Arbiter global scope: no tool use id to coordinate on; evaluating although project hooks may cover it: <sources>.`
    The dispatch carries no `tool_use_id`, so no claim can be made; the hook evaluates rather than leave the
    dispatch to a project hook that may never fire, and a project hook may evaluate it too.
  - `Arbiter: dispatch already claimed by the global hook; not evaluated twice.` This is the project-scope hook
    yielding to a `global` claim, so the dispatch is evaluated once.
- **Every other harness** logs
  `Arbiter global scope: deferred-to-project (<harness>: no per-dispatch probe; the project's own <harness> hooks decide).`
  Those harnesses have no per-dispatch probe yet, so a project with no hook for that harness is not gated there.
  `arbiter doctor` reports it as `ungated`.

With no project config, the user-level config is used and the per-root ledger lives under
`~/.kyber-weave/arbiter/projects/<root-key>/`.

**Claude's user-level block after updating.** A release with the Claude fix does not change the block on
its own: `kyber-weave update` refreshes only the harnesses listed in `arbiter.global.targets`, and a machine
that installed Claude before may not list it there. After updating, run the install once, passing the
`provider.kind` that `~/.kyber-weave/kyber-weave.yml` already holds (`none` below), because the install
writes that file's kind and omitting the option writes `none`. It never touches the per-user override
`~/.config/kyber-weave/arbiter.yml`, which is where a machine's real provider, model and endpoint usually
live, so those stay as they are:

```bash
kyber-weave arbiter install --target claude --provider none --yes
```

This adds `claude` to `arbiter.global.targets`, rewrites the Claude hook block with `PreToolUse` and
`PostToolUse` names, migrates any lower-case legacy entry, and rewrites the global receipt. Later
`kyber-weave update` runs then refresh it too. Do not use `kyber-weave squad update --global` alone, which
re-renders the block but leaves the target list wrong, or `kyber-weave update` alone, which skips a
harness missing from the list.

Phase 1 wires Claude (per-agent frontmatter hooks), Copilot in VS Code (per-agent
`.agent.md` hooks), Copilot CLI (`.github/hooks/kyber-arbiter.json`), and OpenCode (the
plugin shim). Phase 2 adds Pi (the `.pi/extensions/kyber-arbiter.ts` shim), Codex (the
owned block in the shared `.codex/hooks.json`), and Cursor (owned entries in the shared
`.cursor/hooks.json`). Phase 3 adds Kilo (the `.kilo/plugin/kyber-arbiter.ts` shim),
Antigravity (the owned `kyber-arbiter` group in `.agents/hooks.json`), Factory (the owned
block in `.factory/hooks.json`, dropped while the user's hooks live in
`.factory/settings.json` — see fail-closed behaviour below), and Devin (the owned block
in `.devin/hooks.v1.json`). Warp and ZCode render no hooks at project scope: they are
fallback-only — see the MCP fallback below. The hook binary is invoked as:

```bash
kyber-weave-arbiter hook --harness <claude|copilot-vscode|copilot-cli|opencode|pi|codex|cursor|kilo|antigravity|factory|devin> [--caller <agent>]
```

Every target except `claude` and `copilot-vscode` is project-wide: it gates only dispatches
carrying the `KYBER-ARBITER: true` marker and carries no `--caller`. `codex`, `cursor`,
`factory`, `devin` and `antigravity` are owned entries spliced into a shared hook file;
`pi` and `kilo` are whole owned files, `.pi/extensions/kyber-arbiter.ts` and
`.kilo/plugin/kyber-arbiter.ts`.
It reads the harness event on stdin and writes only the harness's decision document on
stdout — logging goes to stderr. The exit code is 0.

`arbiter uninstall --target <t>` removes only Squad-owned arbiter files/blocks for that
target (owned plugin/extension files, owned hook blocks); it defaults to user/global scope
and removes the target from `arbiter.global.targets` in `~/.kyber-weave/kyber-weave.yml`.
Use `--project` to uninstall from a repository scope instead. `--remove-key` deletes the
stored key and warns when another scope still shares the key; without it the key is untouched.

Global scope carries several block-bearing targets in one receipt, in any order: `cursor`,
`codex`, `factory`, `antigravity` and `devin` each record a block there, and a global install
merges the requested target into the receipt the earlier installs wrote instead of replacing
them, so `arbiter install --target cursor`, then `--target codex`, then `--target factory`
leaves three blocks in `~/.kyber-weave/squad/global/squad.receipt.json` and each command
exits `0`. Each block's recorded path is relative to that target's own global root, so
several targets legitimately record the same file name; the receipt tells them apart by
target, exactly as it separates its owned files. Every verb reads that recorded path back
rather than recomputing it from a format constant: `squad status --global` and `squad doctor`
list each block with its target label and report drift per owned entry; a repeat
`arbiter install` for any target runs the update path over every target the receipt owns and
rewrites each block it still finds intact; and `arbiter uninstall --target <t>` removes only
that target's block, leaves the others in place, and deletes the receipt only when it owns no
file and no block any more. See the
[Squad block identity](../kyber-squad/architecture.md#owned-blocks-in-shared-hook-files)
rules for why the target is part of a block's identity there.

The Kilo plugin shim resolves and verifies the repo root from its own plugin location
(walking up to the nearest `.kyber-weave/kyber-weave.yml`, fallback `.git`), binds that root
into both the spawn `cwd` and the envelope `cwd`, and blocks clearly when no root resolves —
never spawning with a possibly-wrong cwd.

## The Copilot conductor guard

When the Arbiter is enabled, the Copilot conductor's agent file runs `kyber-weave-arbiter guard` on `preToolUse` and `userPromptTransformed`. It denies the conductor's own project work and restates the workflow on every model-facing message, because VS Code requires the conductor to hold tools its workers need. See [ADR 0066](../adr/0066-the-copilot-conductor-holds-tools-a-hook-denies-it.md).

A denial names the blocked tool. If the conductor genuinely needs a new Copilot tool, add it to `ConductorGuard`. The guard is rendered only when the Arbiter is enabled at install. Once rendered it reads no configuration, so setting `arbiter.enabled: false` later does not remove it until the agents are rendered again.

## The architect shell guard

On Claude, the Arbiter denies an architect `Bash` or `PowerShell` call that falls outside the command set
`architect.md` documents (`KW-ARB-SHELL-001`). The check is `ArchitectShellGuard`, a stateless hook guard
beside `ReadGuard` and `ConductorGuard`; it is not a rule, so no decision or ledger event is written. The
hook is a `PreToolUse` entry with matcher `^(Bash|PowerShell)$` in the architect's agent file, at project
scope and at user scope. The user-scope copy is an exception to the rule that user-level agents carry no
per-agent hooks: a matcher in the user-level `settings.json` would start the hook for every shell call of
every agent on the machine. See [ADR 0071](../adr/0071-the-arbiter-enforces-the-architect-shell-allowlist-on-claude.md).

**What it denies.** The command is split on unquoted `&&`, `||`, `;`, `|` and newline, and each part must
match a documented form. Anything else is denied before it runs, including:

- substitutions (`$(…)`, backticks, `$`), process substitution, subshells, braces, heredocs and background `&`;
- redirects other than `2>&1` and `2>/dev/null`, and environment-prefix assignments. `2>/dev/null` is allowed in Bash only; PowerShell allows `2>&1` alone;
- shell keywords such as loops and conditionals, and wrapper commands such as `bash`, `sh`, `eval`, `xargs`, `env` and `sudo`;
- git global options other than `--no-pager` and `-C` (for example `git -c`), and `git … --output`. Long-option abbreviations of denied git options (`--output`, `--ext-diff`, `--textconv`) are refused too;
- `find -exec`, `-ok` or `-delete`, and `tail -f`;
- `sort` with any flag that is not on its allowlist (so `sort -o` is denied), and `rg` with a denylisted flag such as `--pre`;
- `touch` on anything other than a `.md` or `.json` file under the plan, spec and todo directories. `touch` paths
  must resolve under the plan, spec or todo folder of the payload's working directory, not of a directory reached by
  `cd`. A `cd` out of the tree makes a later `touch` denied, and a blank working directory fails closed, so every
  `touch` is denied then. An absolute path or a `..` escape is denied.

A command the guard cannot parse is denied, so a wrongly denied command costs one `STATUS: BLOCKED` round trip.

**Forms that look unusual but are allowed.** The Bash tool resets its directory on every call, so `cd <dir> &&`
runs a command in the worktree, and `git -C <dir>` does the same for git. The docs commands take a path operand
in place of `.`. `sort` accepts only the flags on its allowlist, and `rg` accepts any flag except the ones on its
denylist. `touch` operands resolve as described in the list above. `codegraph explore` takes exactly one query, and `doctor --ensure` takes no operand. `rg` refuses `--pre`, `--pre-glob`, `-z` and `--search-zip`. `kyber-weave arbiter plan <plan file> -f json` is read-only
and is the architect's one way to read a task file's execution digest; it is on the architect's command list and in
the guard. The command list itself lives in `architect.md`, which the sync test binds to the guard.

**Reading a denial.** A denial names the command the architect tried. The architect is told to return
`STATUS: BLOCKED` with that command rather than work around it, so a denial is the list working. Find
the `KW-ARB-SHELL-001` line in the hook log. If the command is one the planner really needs, widen the list
(next section) rather than asking the architect to route around it.

**Widening the list.** Change three things together, in one change:

1. one row in the guard's `DocumentedForms` table, which is the set of documented forms the guard allows;
2. the architect command-set clause in `products/kyber-squad/agents/architect.md`, where every backticked span
   between `Your shell is limited to this command set:` and the first `before you edit it.` must match a
   `DocumentedForms` key;
3. the sync test `KyberWeave.Tests.Arbiter.ArchitectShellGuardSyncTests`, which binds those spans to the keys.

Changing `architect.md` also changes its body digest, so update `final-body-sha256` in
`products/kyber-squad/migration/architect.md` from the value `SquadCanonicalContentTests` prints, then
re-render with `kyber-weave squad update` (add `--global` for the user-scope copy).

**Harness matrix.** The guard is enforced on Claude only, for the `Bash` and `PowerShell` tools at project and
user scope. Every other harness keeps the list as instruction only, and the architect keeps it by its own
discipline there: Copilot in VS Code (the terminal tool name in its payload is undocumented), Copilot CLI (its
hooks gate only `task`), Codex, Cursor, OpenCode, Kilo, Pi, Factory, Devin and Antigravity. Warp and ZCode have
no hooks. These are follow-ups. The guard is enforced at once, with no shadow mode and no setting of its own, like
`ReadGuard` and `ConductorGuard`. The only off switch is `arbiter.enabled: false` in the project configuration,
which turns the whole Arbiter off, and with it this guard.

**Confirming it fires.** After an architect run that used the shell, search the hook log for `KW-ARB-SHELL-001`.
A denied architect `ls -la | sed` call is the expected line: `sed` is not in the list, so the whole pipeline
is denied. If no such line appears, check that the architect's agent file carries the `PreToolUse` hook with
matcher `^(Bash|PowerShell)$`, and that the Claude workspace trust dialog has been accepted for the project.
The user-scope copy runs without that trust step while the architect sub-agent is active.

**Known gap: the worker read guard does not cover PowerShell.** The worker read guard protects planning paths
from `Read`, `Grep`, `Glob` and `Bash` only. `ClaudeHookAdapter.IsReadTool` and `ClaudeRenderer.ReadGuardMatcher`
name `Bash` and not `PowerShell`, so a worker's `PowerShell` read of a plan or specification is not guarded. This
is a follow-up.

## Trust steps

A hook that never runs enforces nothing, and the audit cannot see dispatches no hook
observed — so grant trust at install time:

| Harness | Trust gate |
|---|---|
| Claude | Accept the workspace trust dialog for the project. `claude -p` sessions never count: headless runs execute no project sub-agent frontmatter hooks and stay unenforced. |
| Copilot in VS Code | Open the project as a trusted workspace and keep `chat.useHooks` enabled, or the hooks stay off. |
| Codex | Trust the project's `.codex/` layer, then review and trust the new hook through `/hooks`. Every `squad update` that changes the hook needs that review again, or the changed hook is skipped. |
| Pi | Trust the project so that `.pi/extensions/` loads. Until trust is granted the extension does not load and dispatches stay ungated. |
| Warp | No trust gate — there are no hooks to trust. Setup instead: Squad writes no MCP configuration for Warp, so register the `kw-arbiter` server — `kyber-weave-arbiter serve --repo-root <root>` — in Warp's own MCP configuration. Until it is registered only the advisory marker fallback enforces there. |
| ZCode | ZCode requires `hooks.enabled: true` in `~/.zcode/cli/config.json`. Squad never flips this setting; when absent or false, it is reported as `hooks-disabled-by-harness`. At project scope, ZCode ignores project hooks and uses the MCP fallback. |
| CommandCode | CommandCode block-on-dispatch unconfirmed; register `kyber-weave-arbiter serve --repo-root <root>` through MCP configuration. |
| Cursor, Copilot CLI, OpenCode | No documented trust gate. |

`arbiter install`, `squad install`, and `squad update` print the applicable step for the rendered targets.

## Fail-closed behaviour

Any internal hook error blocks the dispatch with an explicit reason carrying
`KW-ARB-HOOK-001` — never a silent pass. On conductor triggers the block carries an
escalation envelope for architect; the conductor dispatches architect with it and never
retries the blocked dispatch unchanged. The exception is a refused planner dispatch
(`KW-ARB-PLANNER-001`): its envelope names a corrected dispatch, and the conductor re-issues
that planner dispatch instead of sending it to architect. Timeouts are bounded by `provider.timeout-ms` plus
the harness budget; a hook killed mid-evaluation still leaves its ledger event, which
`audit` reports as a decision-less entry.

Pre-dispatch denies block on every target. Post-dispatch limits differ by harness: some
targets deliver outcomes only as context, and Antigravity and Devin deliver none. The
[harness notes](architecture.md#harness-facts) state each limit, and the
[degradation taxonomy](../kyber-squad/requirements.md#degradation-taxonomy) lists the records.
Antigravity cannot observe returns, so `READY-001`'s completion check and `MODE-001`'s RED
check answer `returns-unobservable` there, which allows and is logged; the in-flight overlap
check finds nothing there, since no dispatch ever closes.
Devin's dispatch target and prompt are read from `tool_input.profile` and
`tool_input.prompt`. The vendor documents only that the tool "takes a profile", so those
argument names rest on the undocumented fact F12. A `run_subagent` dispatch missing either
is logged as unmarked, and `audit` reports it as `KW-ARB-AUDIT-002`, including when the
target is absent: a Squad dispatch whose profile the harness did not send is still flagged.
If Devin renames the arguments, every dispatch goes unmarked until the adapter is updated.
`arbiter doctor` prints one informational line for each of copilot-cli, codex and cursor.

## The MCP fallback (Warp, CommandCode, and project-scope ZCode)

Warp documents no hooks, CommandCode block-on-dispatch is unconfirmed (`dispatch-unconfirmed`),
and project-level hooks are not executed in ZCode's current version, so the marker fallback is
advisory there and the MCP fallback is the enforcement surface. Squad writes no MCP configuration
for them, so declare the server yourself in the harness's own MCP configuration under the key
`kw-arbiter`, launched as:

```bash
kyber-weave-arbiter serve --repo-root <root>
```

The key and the executable are separate contracts: `kw-arbiter` is the value of
`McpServerNames.Arbiter`, so it is the same spelling every renderer grants, while the argv above
is unchanged.

`serve` exposes five tools. `arbiter_evaluate_agent_dispatch_or_return(trigger, facts)` evaluates
one event
exactly as the hook would — same facts, rules, ledger append and decision record — and
returns the same allow, envelope or review note; the caller asserts the routing facts and
the log records `caller-source: asserted`, so this path is advisory.
`arbiter_list_effective_rules_by_trigger(trigger?)` lists the rule catalogue,
`arbiter_check_configuration_and_key_readiness()` reports the
root, rule-set hash, rule count and provider state, never the key, and
`arbiter_select_relevant_document_sections` and `arbiter_classify_bot_review_comment` answer the
section and bot-comment triage (see [phase 3 facts](architecture.md#phase-3-facts-review-shortcuts-stores-and-comments)).
The evaluate and classify tools write decision records; the other three only read. Every
response leads with a provenance line naming the root it answered from; the root is
resolved from `--repo-root`, then `KYBER_WEAVE_REPO_ROOT`, then the working directory. The
conductor calls `arbiter_evaluate_agent_dispatch_or_return` before each dispatch and after each
return, and
code-reviewer calls it once before the lens fan-out and once before the refutation
fan-out. On Squad-rendered fallback targets the tools are granted through the
`decision.query` capability, allowed only for the `orchestrator` and `reviewer` profiles —
the conductor and code-reviewer.

## Reading a planner refusal

A planner refusal is the Arbiter stopping a dispatch to a planner, such as `architect`,
because the `PLAN_FILE:` or marker it carried was not acceptable. The envelope's `ANSWER`
line names the cause, `CALLER` shows the caller the Arbiter resolved, and `NEXT` says what
to do. The conductor re-issues the planner dispatch with the correction; it does not send
the refused dispatch to `architect`. Rule `KW-ARB-PLANNER-001` gives four answers:

- **`malformed`**: the prompt has no recognised marker. Re-issue the dispatch with one, such
  as `INTAKE:`, `FINDINGS:`, the `STATUS: ARBITER_ESCALATION` envelope, or a `FEATURE:` and
  `PHASE:` pair. `FINALIZE` only counts with a `PLAN_FILE:` alongside it.
- **`plan-file-invalid`**: the `PLAN_FILE:` was sent but rejected, so it is treated as a
  refusal even when the body has a valid marker. The usual cause is an absolute path,
  such as a worktree path. Re-issue with the plan's repository-relative path: `/`
  separators, no leading `/`, no `..`, no backslash, no space and no tab.
- **`plan-file-outside-planning-dirs`**: the path is valid but is not under the plan, spec
  or todo directories. Re-issue with a `PLAN_FILE:` under one of those directories.
- **`recognised`** is the pass answer, not a refusal. It is the answer a corrected dispatch
  gets: a planning-directory `PLAN_FILE:`, or a marker that needs no `PLAN_FILE:`.

`REPEAT` counts earlier refusals from the same session when the dispatch has no
`PLAN_FILE:`, because there is no plan to key on. A refusal that is the first in its session
reads `REPEAT: 1`, whatever other sessions have done. On a harness that sends no session
id, such as Copilot, these refusals always read `REPEAT: 1`. A `REPEAT` of 2 or more means the same
kind of refusal came back in this session, so fix the cause rather than resending the
dispatch unchanged.

## Setup, doctor, audit

The one-command install replaces the old multi-step sequence. The key is never shown and
never stored in a file:

```bash
kyber-weave arbiter install --target kilo --provider "" --arbiter-key "<key>"  # default global scope: config, hooks, provider, key, verify, trust step
kyber-weave arbiter install --project --path . --target kilo --provider "" --key-stdin  # repo-scope opt-in; --path valid only with --project
kyber-weave arbiter uninstall --target kilo  # default global scope: removes only Squad-owned files/blocks; add --project for repo scope; --remove-key to delete stored key
kyber-weave arbiter setup    # choose provider (none, TypeSafe cloud, local Ollama); stores the key in the OS credential store
kyber-weave arbiter status   # provider, model, endpoint origin, whether a key resolves — never the value; reports by scope
kyber-weave arbiter doctor   # warns on switched-off model rules, untuned models, missing keys, un-ignored log dirs, missing binaries, and missing or stale model targets files (KW-ARB-MODEL-001); notes post-dispatch advisory limitations
```

`--target` is required and validated against the Squad target roster; `--provider ""` means
`none`; the install and uninstall commands default to user/global scope and `--project` is the opt-in for a
repository install. `--path` is valid only with `--project`, and `--global --project` exits
`2` with `KW-ARB-INSTALL-001`. `--dry-run` plans and prints the target's physical files without
writing anything at either scope. A bare install inside a repository that already has a project install prints a migration notice: `Installed at user scope; use --project to manage this repository's install.` A second install is an idempotent no-op. `--arbiter-key` is
visible in argv and shell history (a one-line warning says so); `--key-stdin` and
`TYPESAFE_API_KEY` stay the safer paths. A keyed provider with no resolving key surfaces
`KW-ARB-KEY-001`. Exit `2` for client-input errors, `1` for operational failures, `0` for
success or no-op.

`arbiter doctor` and `arbiter status` print one line per installed harness that says what gates its dispatches
in this project. A harness is installed when it is in `arbiter.global.targets`, owns an Arbiter hook in the
global Squad receipt, or has a project deployment. The lines are:

| Line | Meaning | Fix shown |
|---|---|---|
| `gated by project hooks` (Claude: with the project sources named) | A project hook file gates the harness's dispatches | none |
| `gated by the global hook under this project's configuration` | Claude: the user-level hook is installed and evaluates the project's dispatches; when project hook sources exist they are listed as gating when they fire | none |
| `gated by the global hook under your user configuration` | The project has no Arbiter config, so the user-level config applies | none |
| `opted out (arbiter.enabled: false)` | The project sets `arbiter.enabled: false` | none |
| `fallback-only: no hook; the Arbiter MCP server is its only gate` | Warp and ZCode have no hook to install | none |
| `ungated` | Nothing gates the harness here | `kyber-weave arbiter install --project --target <token>`; Claude: `kyber-weave arbiter install --target claude` |

Two diagnostics accompany them. `KW-ARB-PROJECT-001` (warning) is raised once per ungated harness while the
project enables Arbiter. `KW-ARB-GLOBAL-001` (warning) is raised when the global Squad receipt owns a hook block,
or a harness's own shim hook file, for a harness missing from `arbiter.global.targets`: `kyber-weave update` would
never refresh it. Its hint is `kyber-weave arbiter install --target <token>`. Doctor no longer prints
`Project hooks: installed.`, and status no longer prints `project overrides global`.

Read-only inspection:

```bash
kyber-weave arbiter validate [path]  # arbiter: section, user override, and shipped rules
kyber-weave arbiter rules [--trigger <t>]
kyber-weave arbiter plan <file>      # what the parser understood, with KW-ARB-PLAN-001 / KW-ARB-PARSE-00x; also reports the execution digest for a plan with parsed tasks that drops no dependency token (KW-ARB-PARSE-001) (-f json: metrics.execution-digest)
kyber-weave arbiter eval --trigger <t> --event <file> [--provider none]  # offline dry run; writes nothing
kyber-weave arbiter audit [--plan <file>] [--session <id>] [--since <ISO-8601>]
kyber-weave arbiter tune [path] [--rule <id>] [--thresholds 0.5,0.6,0.7] [--json]  # read-only score report; see below
```

`audit` reports decision-less events, unmarked dispatches to Squad agents, unpaired
pre/post events, and implementation packets whose body names a planning path
(`KW-ARB-AUDIT-001`…`-004`), and lists attestations for information. code-reviewer runs
`audit --plan <PLAN_FILE>` at the end of a run and cites the result.

## Tuning thresholds (`arbiter tune`)

`arbiter tune` reads `decisions.jsonl` and `ledger.jsonl` from the Arbiter directory and writes
nothing. It scores the step-1 answers so a threshold can be chosen from evidence rather than from
the shipped defaults. Each step-1 rule gets:

- the count of scored answers, split into shadow and enforced;
- a confidence histogram in tenths (`confidence (tenths)`);
- for each candidate threshold, how many answers would change side (`threshold 0.70: 3 flip(s)`);
- one line per scored answer, with its decision id, score and the `RESULT:` the worker reported.

`--rule <id>` limits the report to one rule. `--thresholds` takes values between 0 and 1 and
defaults to `0.5,0.6,0.7,0.8,0.9`. `--json` writes the same figures as JSON. A log with no scored
answers prints `no scored answers.`. A log with a malformed line exits `1` and reads nothing
partial, so repair or remove the line and run again.

Two limits, both known:

- The report pairs a decision with the `RESULT:` line only. The `TASK_GATE:` outcome is not paired
  yet; that pairing is not wired.
- A `delegate.model` row shows the profile, the token and `delivered`. Read them as described in
  [Switching model targeting to enforce](#switching-model-targeting-to-enforce).
- Shadow and enforced answers are counted apart, but a threshold's flip count is computed from the
  scores alone, so a shadow answer that would flip is counted the same way as an enforced one.

### Reading shadow entries

A rule in shadow is evaluated and logged in full. Its entry in `decisions.jsonl` carries
`"shadow": true` and `would-effect`, the effect it would have contributed if it were enforced. The
outcome it produced for the dispatch is the family's neutral effect (`keep`, `spawn` or `allow`), so
a shadow entry never blocked or changed anything. Read it as "this rule would have said X". When
`would-effect` names `escalate`, `skip`, `verify`, `retarget`, `answer`, `drop` or `duplicate`, the
rule's answer is the evidence to weigh before turning it on with `shadow: false`.

An entry whose evidence reads `suppressed: primary <outcome>` is a secondary judgement that was not
delivered because the primary dispatch was not `allow`. It is recorded so you can see what the
judgement would have said.

### `KW-ARB-MODEL-001`: model targets file missing or stale

`kyber-weave arbiter doctor` raises `KW-ARB-MODEL-001` (warning) when model targeting is not `off`,
a hooked global target is installed, and its `model-targets.<harness>.json` file is missing,
unreadable, or older than the Squad lock. The remedy is the one the message names:

```bash
kyber-weave squad update
```

The update rewrites the table from the current presets. A stale table changes what a retarget is
pointed at, so refresh it after changing a preset. A project-scope target is not checked by this
diagnostic.

## Switching model targeting to enforce

`arbiter.model-targeting.mode` has three values. The default is `shadow`.

| Mode | What happens |
|---|---|
| `off` | Both model rules are disabled. No `model` block is written. |
| `shadow` | The rules run and each `delegate.model` decision records a `model` block with `reason: shadow`. Nothing is delivered. |
| `enforce` | A retarget is written onto the dispatch, unless a reason above holds. |

To move from shadow to enforce:

1. Run `kyber-weave arbiter tune` and check the rows that carry a model block. Each shows the profile
   the Arbiter chose, its token, and whether it was delivered.
2. Confirm the table exists for the harness: `kyber-weave arbiter doctor` must not raise
   `KW-ARB-MODEL-001`.
3. Set `arbiter.model-targeting.mode: enforce` in the configuration. This is the only switch for the
   two model rules; a per-rule `shadow` override on either fails `KW-ARB-CONFIG-014`.
4. Run `kyber-weave arbiter doctor` and `kyber-weave arbiter validate` to check the configuration.

### Reading the `tune` profile column

Each `delegate.model` row reads like this:

```text
decision <id> score 0.82 profile general token claude-sonnet-5-5 delivered true RESULT: pass
```

- `profile` is the semantic profile the target chose (`fast`, `general`, `deep-planning` and so on).
- `token` is the model token the table names for that profile, or `none` when the profile has no token.
- `delivered` is `true` when the Claude adapter wrote the model field and no reason blocked the write. It is
  not proof the model ran. In `shadow`, `delivered` is always `false`.
- `RESULT:` is the worker's own report, paired from the ledger.

A row appears only for a scored answer that carries a model block. The text report does not print the
`reason`; the `--json` report does not carry it either. To see why a dispatch was kept, read its
`model.reason` in `decisions.jsonl`.

### What `delivered: true` does and does not mean

`delivered: true` means the Arbiter wrote the model field onto the Claude `Agent` dispatch, and the decision
records it only after that write held. It does not mean the model ran. Two settings can override it after the write:

- An installed mod's `agent.spawn` hook can replace the model.
- The `CLAUDE_CODE_SUBAGENT_MODEL_FORCE` environment variable forces a model on every sub-agent.

Check the dispatch's own transcript if you need to know which model ran.

### When a retarget is not written

- A token that is neither an accepted alias nor a full Claude id of an accepted family is not written. The
  decision records `delivered: false` with `model.reason: token-not-accepted`, and the dispatch runs as it
  would with no model block.
- A write that throws is recorded as `delivered: false` with `model.reason: model-write-failed`. The hook log
  has a line beginning `model-write-failed`. The dispatch runs without the model field, and its routing header
  is stripped. Check `decisions.jsonl` first, then the hook log, when a retarget looks wrong.

## Phase 3 rules: reading and enforcing

Nine phase 3 rules ship in shadow: `KW-ARB-NITPICK-001`, `KW-ARB-NITPICK-002`, `KW-ARB-DUP-001`,
`KW-ARB-TRIAGE-001`, `KW-ARB-INTAKE-001`, `KW-ARB-DOCIMPACT-001`, `KW-ARB-FAILURE-001`,
`KW-ARB-TRIM-001` and `KW-ARB-COMMENT-001`. Until a rule is enforced, a dispatch behaves as it did
before: the lens is still spawned, the finding still reaches the host, and the investigator, planner
or docs-dev still runs. Read the shadow entries in `decisions.jsonl` first.

- A `would-effect` of `drop` (nitpick filter) means the finding would have been set aside as
  `ARBITER_DROPPED`. Compare a sample against the findings the council kept.
- A `would-effect` of `duplicate` means the refutation would have been denied as `ARBITER_DUPLICATE`.
- A `would-effect` of `answer` on a shortcut means the worker would have been withheld and the
  substitute digest returned. Check the digest's `ARBITER_DECISION` against the worker's own report
  before trusting it.
- A `would-effect` of `same-failure` means the rework would have carried an `ARBITER_SAME_FAILURE`
  advisory. It never denies, so it is safe to enforce once the advisory reads correctly.
- A `would-effect` of `advise` on `KW-ARB-TRIM-001` or `KW-ARB-COMMENT-001` means a section would have
  been trimmed or a bot comment advised as a nitpick or already fixed. Check each one before enforcing.

To enforce one rule, set `shadow: false` on its entry under `arbiter.rules` in the project config, as
[configuration](../configuration.md#savings-keys) describes. Enforce one rule at a time and read its
entries after each change.

Stores hold the text of review findings and worker failures under `<arbiter-dir>/stores/`. Each item
is limited to `arbiter.stores.max-item-bytes` (default 16384), and an oversized item is refused, not
cut short. `arbiter.stores.retain-days` (default 14, clamped to 1 to 3650 at run time) sets how long a
store directory is kept. A directory's age is the time of its newest file, so an active run is not
swept mid-review; a directory whose newest file is older than that is removed on a later append. A
failed store write is reported as `unavailable`, never thrown.

Two evidence lines appear in a decision record's notes, and neither drops a finding:

- `review-run-missing` — a lens dispatch arrived with no `REVIEW_RUN` header, so its findings are
  judged but not stored, and duplicate detection has no store to compare them against. Check that the
  lens dispatch carries the header.
- `findings-over-cap` — a return carried more findings than the 64-question budget. The findings past
  the budget were not judged and are kept, not dropped.

Two advisory commands run outside a dispatch:

- `kyber-weave arbiter sections --path <FILE> --task <TEXT>` lists the sections of one markdown document
  a task needs. It fails safe to every section.
- `kyber-weave arbiter comment --path <FILE> --line <N> --body <TEXT> [--commit-sha <SHA>]` classifies
  one bot review comment as `real`, `nitpick` or `already-fixed`. Each call records a decision, and any
  unsure answer is `real`. While the rule is in shadow the class printed is always `real`, and a
  `shadow: would have been <class>` line says what the model gave.
- `kyber-weave arbiter comment --summary --body <TEXT> --author <LOGIN>` classifies each row of a bot's
  PR-level summary table (`Severity | File | Line | Issue`); `--path` and `--line` are ignored. Only a
  login listed in `arbiter.comment-triage.bot-logins`, or one ending `[bot]`, is classified. Every
  other row is `real` and unasked. Rows past 50 are `real` and unasked too. A missing or unreadable
  table keeps the whole comment `real`.

## Recording a harness defect

Support is claimed from vendor documentation, so a misbehaving hook is a defect against the
vendor's stated behaviour — record each one with three facts:

1. **Harness and version** — the harness token and the exact harness version observed.
2. **The contradicted documented cell** — which cell of the
   [harness table](architecture.md#harness-facts) the behaviour contradicts.
3. **The affected trigger** — recorded as `arbiter-not-enforced` for that trigger on that
   harness until the defect is fixed or the fallback covers it.

## Related

- [Architecture](architecture.md) — engine, rules, configuration, harness facts
- [Kyber-Squad onboarding](../kyber-squad/onboarding.md) — installing hooks, trust, scopes
- [Rule reference](../ci-pipelines/rule-reference.md) — every diagnostic id
