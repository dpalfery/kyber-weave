---
name: kw-onboarding
description: "Guided first-time setup of Kyber-Weave and Kyber-Squad on a machine and repository: doctor, CodeGraph index, kw-docs and codegraph MCP servers, harness install with Arbiter hooks, every setting with its default, model profiles, the decision-model key, and a check of the repository's rules for conflicts. Safe to re-run. Use when a user says set up, onboard, get started with, or configure Kyber-Weave or Kyber-Squad, or asks to audit AGENTS.md or asks why agents get conflicting, contradictory or stale rules and instructions. Do NOT use for onboarding a repository's documentation (kyber-docs-onboarding-and-assessment) or for authoring a model preset (kyber-weave-model-profile)."
license: MIT
metadata:
  author: dpalfery
  version: 0.2.0
---

# Set up Kyber-Weave with the user

A walkthrough you run **with** the user, one question at a time. Assume the user has read no
Kyber-Weave documentation. Each step first detects what is already done and skips it, so the
skill is safe to run again. Keep a short ledger: already in place, changed now, declined.

## The rule for every question

**NEVER ask a question without its context.** Before every question, about any setting, fix,
install or finding, tell the user in plain words, in a short paragraph or 4 to 6 short lines:

1. **What it is**: one sentence defining the thing, with no undefined product term.
2. **Why it matters**: what the choice changes in the user's day-to-day work.
3. **What each option does**: the concrete effect of each answer, including choosing nothing.
4. **Cost and undo**: time, files written outside the repository, any risk, and how to change
   it later (the exact command, or "re-run this skill").
5. **What I found**: what is already on this machine or in this repository that bears on the
   answer. Detect first, ask second: never ask the user to recall what a command can show.
6. **What I recommend and why**: one default, chosen from what you found, that the user can
   accept with one word; say who should pick something else.

Then ask **one** question, phrased so "yes" or "the recommended one" is a complete answer. Use
the harness's structured question tool where it exists (for example `AskUserQuestion` in Claude
Code, `askQuestions` in VS Code Copilot), otherwise a numbered list answered with numbers or
`none`. Recommended option first. Never a bare list of product names, never two things in one
question, never more than about five options at once (split into groups). If the product does
not document an option well enough to explain it this way, say so and skip the question.
**Ask each thing once.** A topic that appears in several steps (for example a git hook or a
Copilot setting) is asked where it first comes up; later steps say "settled earlier". Advice
whose effect the product does not document goes in the summary, not in a question.

Each reference holds the context for the questions of its step, next to the setting it
describes. Define these at first use, or avoid them: harness, squad, Arbiter, decision model,
gate, MCP server, code index, fix level, severity override, run, worktree. Quote a rule id only
when the user needs it to look something up.

## Ground rules

- **ALWAYS ask before every change outside this repository**: a global install, a harness hook, a shell
  profile, the OS credential store, anything under the home directory. Inside the repository,
  say what you will write before you write it.
- **NEVER print, store, or echo a secret.** Keys go to the CLI's own masked prompt or `--key-stdin`, run by the
  user, into the OS credential store. Never ask for a key in the chat or put one in a shell profile.
- **Indexing is the user's decision**; a "no" stands for the rest of the run.
- **The CLI is the authority.** Use only verbs and flags that `kyber-weave <verb> --help` shows.
- **NEVER drop a harness by accident.** Every `--target` list you pass is the union of what is
  already installed and what the user chose ([harnesses.md](references/harnesses.md#never-drop-a-harness)).

## Step 0 — Say what will happen, then look first

1. Tell the user, in three or four lines: what onboarding does (checks this machine, connects
   the code-search and docs tools, installs Kyber-Weave's agents into the AI coding tools they
   choose, reviews settings, checks the repository's rules for conflicts); that it takes about
   15 to 30 minutes; that nothing outside this repository changes without their yes; and that
   they can stop at any point and resume by asking again.
2. Run the read-only detection in [references/first-look.md](references/first-look.md) and show
   its short "here is what I found" summary before the first question. Every later question
   refers back to it.
3. Missing `kyber-weave`: stop and point at the install one-liner in the `kyber-weave-cli` skill.
   No `.kyber-weave/kyber-weave.yml`, or a CLI older than the installed squad: ask using the
   context in first-look.md.

## Step 1 — Fix what the machine check found

For every doctor line that is not a plain pass, give the context in
[references/doctor-checks.md](references/doctor-checks.md) and ask about its fix, one at a time.
Exit `3` means CodeGraph is not installed: the user installs it themselves.

## Step 2 — Code index and docs tools

Load [references/mcp-and-index.md](references/mcp-and-index.md). Ask, in order and only where
detection shows a gap: build the code index; then the worktree git hook (its fix also refreshes
the index, so only after a yes to indexing); declare the `codegraph` server; wire the two servers
into each harness in first-look's recommended set (re-check any harness step 3 adds); then prove
each answers.

## Step 3 — Harnesses

Load [references/harnesses.md](references/harnesses.md). Three questions, each with its context:
which harnesses (recommend the ones in use), every repository or this one only (skip when
nothing new was chosen), and the Arbiter hooks. Removing a harness is never part of these; it is its own question, only when the
user asks. Then show each chosen harness's one-time trust step and needed setting.

## Step 4 — Settings

Load [references/settings.md](references/settings.md). Show the common settings group by group,
at most five at a time, each with its current value and its context line; ask which to change,
with "change nothing" recommended unless detection found a reason. Advanced settings stay hidden
unless the user asks to see them. Apply and validate the way the reference says.

## Step 5 — Models

Load [references/models.md](references/models.md); show which models each chosen harness uses
and ask whether to change any.

## Step 6 — Decision-model key

Load [references/settings.md § Decision-model key](references/settings.md#decision-model-key) and
ask with its context; when `kyber-weave arbiter status` already shows a provider and a key
found, report that in one line and ask nothing. Never ask the user to paste the key into the chat.

## Step 7 — Check the repository's rules for conflicts

Load [references/rules-and-structure-conflicts.md](references/rules-and-structure-conflicts.md) and
run it. Asked on its own ("audit AGENTS.md", "find conflicting rules"), run step 0's detection
and then only this step.

## Step 8 — Finish

1. Offer the cleanups first-look.md found (for example a retired skill folder left by an update).
2. Re-run `kyber-weave doctor --check .` and `kyber-weave arbiter status`.
3. Summarise in plain words, in three short lists: already in place, changed now, declined or left
   for later (each with what it would do and the command). No rule ids.
4. Next: when the Config Reg's `<onboarding>` line is missing, the `kyber-docs-onboarding-and-assessment`
   skill onboards the repository's documentation and records the answer runs need (already `done`
   or `declined`: say so and skip it); then start work through the conductor.
5. Name each harness to restart because its agents, hooks or MCP configuration changed.
