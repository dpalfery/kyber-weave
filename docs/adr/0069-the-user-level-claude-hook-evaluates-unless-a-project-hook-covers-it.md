---
id: adr/0069-the-user-level-claude-hook-evaluates-unless-a-project-hook-covers-it
title: The user-level Claude Arbiter hook evaluates a dispatch under the project unless the project opts out or a project hook covers it
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-11
component: KyberArbiter
decided-by: []
supersedes: []
code-refs:
  - HookCommand
  - ClaudeHookAdapter
  - ArbiterHookDecisionEngine
---

# ADR 0069: The user-level Claude Arbiter hook evaluates a dispatch under the project unless the project opts out or a project hook covers it

## Status

Accepted, 2026-11-11, by the owner's decision. It changes the deferral rule of the user-level
(`--scope global`) hook for Claude Code only. It refines [ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md)
and does not change the decision engine, the rules, or the project-scope renderers.

## Context

The Arbiter installs one hook at user scope, so every Claude Code session on the machine runs it. When a
project turned Arbiter on, the user-level hook used to log `deferred-to-project` and stop, on the
assumption that the project's own hook gates the dispatch. A project with no Claude hook of its own has
no such gate. Its dispatches reached no evaluation at all: about thirty dispatches in one real run
recorded no decision, and `arbiter doctor` still reported the project's hooks as installed.

Claude Code's documented behaviour, which the design relies on:

- Hooks in `.claude/settings.json` run in sub-agents as well as in the main thread.
- A sub-agent's frontmatter hooks run only while that sub-agent runs.
- A skill's hooks register when the skill is invoked and last for the session.
- A project sub-agent outranks a user sub-agent of the same name, and a personal skill outranks a project
  skill of the same name.
- Identical handlers are de-duplicated only across settings files, and the user-level command differs
  anyway, because it carries `--scope global`.

A static reading of those files cannot prove that a project hook will fire for a given dispatch, so the
question is how to avoid both a dispatch no hook gates and a dispatch evaluated twice. A double evaluation
is harmful: the decision log counts earlier escalations, and a repeat count of two stops the task.

## Decision

1. **The user-level Claude hook defers only in two cases.** When the project sets `arbiter.enabled: false`
   explicitly, it defers. When a project hook covers the dispatch, it defers. In every other case it
   evaluates the dispatch under the project's configuration, and the decisions and ledger go to the
   project's `artifacts/arbiter/` folder. Without a project config the user configuration is used and the
   ledger stays under `~/.kyber-weave/arbiter/projects/<root-key>/`, as before.
2. **Coverage is probed per dispatch, from the files that Claude Code reads.** `ClaudeHookCoverage` returns
   the candidate sources: the project's `settings.json` and `settings.local.json`, the effective agent file
   for the payload's `agent_type`, and every effective skill, where a personal skill shadows a project skill
   of the same name. A source is a candidate only when its hook sits under the exact `PreToolUse` or
   `PostToolUse` container, its matcher matches `Agent` or `Task`, and its command runs the project-scope
   hook and not `--scope global`. Command text alone never counts, because the read-guard and hand-back
   matchers run the same command and do not gate a dispatch.
3. **One dispatch is evaluated once, through a claim file.** Only the dispatch events claim: the tool
   `Agent` or `Task`, on `PreToolUse` or `PostToolUse`. The claim is `artifacts/arbiter/claims/<id>.pre` or
   `.post`, created exclusively, and it holds `project` or `global`. The project-scope hook claims before it
   evaluates and yields only to an existing `global` claim. The user-level hook, when candidates exist,
   waits up to 1.5 seconds for a `project` claim and defers if one appears. Otherwise it claims `global` and
   evaluates. A claim that cannot be written does not skip the evaluation: the hook evaluates without one,
   so a dispatch may be evaluated twice in that failure, never ungated.
4. **The main thread is decided by the same rule.** A main-thread payload has no `agent_type`, so
   agent-file hooks never apply. A project skill hook gates only when that skill is the effective one and
   was invoked this session. Otherwise the user-level hook evaluates the dispatch.
5. **Other harnesses keep the deferral, now with its reason.** Only Claude has a per-dispatch probe. Every
   other harness still defers when the project enables Arbiter, and the log names what it deferred to.
   Extending the probe to other harnesses is a follow-up, because their covering-hook semantics are not
   measured.
6. **Claude's user-level block is written only under `PreToolUse` and `PostToolUse`.** Claude Code documents
   only those spellings, so a lower-case legacy entry is migrated when the block is next rendered. The
   casing rule is recorded in [the Squad architecture](../kyber-squad/architecture.md).

## Alternatives Considered

- **A static rule only.** Decide from the settings files alone, with no claim. This leaves either a double
  evaluation, where a project skill hook was not in fact invoked, or an ungated main-thread dispatch. Rejected.
- **Always evaluate in the user-level hook.** Simple, and it never leaves a gap, but every dispatch in a
  project with a project hook is evaluated twice, and the repeat count then stops the task. Rejected.
- **A ledger in the home directory for the user-level hook.** It would split one run's decisions and repeat
  counts across two folders, and `arbiter audit` and `arbiter tune` read only the project folder. Rejected.

## Consequences

- A project that enables Arbiter and has no Claude hook is gated by the user-level hook from the next
  render onward, with no configuration change in the project.
- A main-thread dispatch in a project whose project skill hook exists but was never invoked waits at most
  1.5 seconds. The wait is logged.
- Claude Code's sub-agent and skill hook rules are not fully documented, and the design does not depend on
  them being right: when a wrong assumption leaves a candidate that never fires, the cost is the bounded wait,
  and when a candidate is missed the claim still prevents a double evaluation.
- `arbiter doctor` and `arbiter status` report, per installed harness, which hook gates it, so an ungated
  harness is visible rather than hidden behind a generic "installed" line.
- Other harnesses remain deferring. Their gap is named in every deferral line and in doctor's output, not
  closed.
