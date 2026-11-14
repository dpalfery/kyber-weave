---
name: kw-onboarding
description: "Guided first-time setup of Kyber-Weave on a machine and repository: runs the doctor, makes sure the CodeGraph index and the kw-docs and codegraph MCP servers answer, asks which coding harnesses to install the squad and Arbiter hooks for, lists every setting with its default so the user picks which to change, shows the model profile per harness, and checks the Arbiter decision-model key. Safe to re-run. Use when a user says set up, onboard, get started with, or configure Kyber-Weave or Kyber-Squad. Do NOT use for onboarding a repository's documentation (kyber-docs-onboarding-and-assessment) or for authoring a model preset (kyber-weave-model-profile)."
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
---

# Set up Kyber-Weave with the user

A walkthrough you run **with** the user, one step at a time. Each step first detects what is
already done and skips it, so the whole skill is safe to run again. Keep a short ledger as you
go: what was already in place, what you changed, what the user declined.

## Ground rules

- **ALWAYS ask before every change outside this repository**: a global install, a harness hook, the
  user's shell profile, the OS credential store, anything under the home directory. Inside the
  repository, still say what you will write before you write it.
- **NEVER print, store, or echo a secret.** Keys go to the CLI's own prompt, `--key-stdin`, or an
  environment variable the user sets themselves.
- **Indexing is the user's decision.** Explain what CodeGraph indexing does and ask; a "no" stands
  for the rest of the run.
- **The CLI is the authority.** Use only verbs and flags that `kyber-weave <verb> --help` shows.
  If a step needs something the CLI does not offer, say so instead of inventing it.
- **Plain words.** Explain each check, setting, or term in one sentence. Do not quote rule ids
  unless the user needs one to look something up.

**How to ask.** Use the harness's structured question tool when it has one (for example
`AskUserQuestion` with multi-select in Claude Code, `askQuestions` in VS Code Copilot). Question
tools cap the number of options, so ask per group and split a long group across several
questions. Without such a tool, print a numbered list and let the user answer with numbers
(`2, 5, 7`) or `none`. Always put the recommended choice first.

## Step 0 — Preconditions

1. `kyber-weave --version`. Missing: stop and point the user at the install one-liner in the
   `kyber-weave-cli` skill. An old version: offer `kyber-weave update` (machine-wide, so ask).
2. Confirm the current directory is inside a git repository that is not the home directory.
3. Look for `.kyber-weave/kyber-weave.yml` at the repository root. Missing: explain that it is the
   repository's Kyber-Weave configuration, and offer `kyber-weave docs init .` (add `--no-skill`
   when the squad is already deployed, because the squad ships the same skills). Do not answer the
   documentation-onboarding question here; that belongs to the later step.

## Step 1 — Check the machine

Run `kyber-weave doctor --check .` (read-only; it never indexes or installs). For every line that
is not a plain pass, explain it in one sentence using [references/doctor-checks.md](references/doctor-checks.md),
offer the fix it prints, and apply it only with the user's yes. Exit `3` means CodeGraph is not
installed: the user installs it themselves; you give no install command.

## Step 2 — Code index and docs tools

Load [references/mcp-and-index.md](references/mcp-and-index.md) now.

1. **Index.** If the repository has no `.codegraph/`, explain what an index is for and ask before
   running `kyber-weave doctor --provision .`. If it exists, the step-1 result already says
   whether it is healthy.
2. **Declared servers.** Read `mcp.required-servers` in the configuration. `kw-docs` should be
   there; offer to add `codegraph` only after explaining what declaring it changes.
3. **Wiring per harness.** For each harness the user uses (ask now if step 3 has not), check its
   MCP configuration holds `kw-docs` and `codegraph`. Wire gaps with
   `kyber-weave docs mcp-wire . --target <slug>` where the reference lists a slug, otherwise show
   the manual entry and let the user place it.
4. **Prove they answer.** In this session, call the docs server's
   `docs_get_source_repository_and_revision` and one `codegraph_explore` query. Report each as
   answering, not wired, or failing.

## Step 3 — Harnesses

Load [references/harnesses.md](references/harnesses.md) now.

1. Get the supported list from the product: the `--target` line of
   `kyber-weave squad install --help`, or the renderer line of `kyber-weave squad doctor --global`.
2. Detect what is in place: `kyber-weave squad status --global` and `kyber-weave squad status .`
   (each deployed file names its harness in parentheses) and `kyber-weave arbiter status`.
3. Ask which harnesses the user wants (multi-select), marking the ones already deployed.
4. Ask global (recommended: every repository) or this project only, then deploy. An explicit
   `--target` list on `squad update` replaces the deployed set, so never update one harness alone:
   - Only refreshing what is deployed: `kyber-weave squad update` (`--global` for global), no `--target`.
   - Adding a harness globally: `kyber-weave squad install --global --target <new>`; the others stay.
   - Adding a harness to a project that has others: `kyber-weave squad update . --target <every deployed token>,<new>`.
   - Nothing deployed in the project yet: `kyber-weave squad install . --target <tokens>`.
5. For each chosen harness, `kyber-weave arbiter install --target <token>`
   (add `--project` for project scope). It keeps the provider already configured; pass `--provider`
   only to change it.
6. Show the harness's row from the reference: its trust step and any setting it needs. Offer to
   apply a setting only where the reference says how, and never edit a shell profile or harness
   settings file without a yes.

## Step 4 — Settings

Load [references/settings.md](references/settings.md) now.

1. Read the current value of every setting in the reference (configuration file, `kyber-weave
   arbiter status`, `kyberdash dash settings show` when KyberDash is installed). Leave out
   settings whose product is not installed, and say so.
2. Show **one** list, grouped as the reference groups it: name, current value (or default), one
   sentence on what it does and when you would change it.
3. Ask which items the user wants to change: one multi-select per group, plus a "change nothing"
   answer.
4. For each selected item only: show its options with the recommended default first, ask, apply
   it the way the reference says, and show the before and after.
5. After any change to the configuration file, run `kyber-weave docs validate .` and fix any
   finding about the configuration or the generated settings block. Report other findings
   without fixing them; documentation is the later docs-onboarding step.

## Step 5 — Models

1. Explain tiers in one line: each agent names a tier (a model profile) such as `fast` or
   `reviewer`, and each harness maps a tier to a concrete model.
2. Run `kyber-weave squad preset list`. For each chosen harness show which preset it uses; with a
   preset, `kyber-weave squad preset show <name> --target <token>` prints tier to model. Without
   one the harness uses the bundled models: read that harness's column from
   `~/.kyber-weave/squad/profiles/<version>.yml`, where `<version>` is the one
   `kyber-weave --version` prints (without any `+build` suffix). If the file is missing,
   `kyber-weave squad update` writes it.
3. Ask which harnesses' models the user wants to change (multi-select). For those, hand over to
   the `kyber-weave-model-profile` skill, which builds and applies a preset.

## Step 6 — Decision-model key

`kyber-weave arbiter status` says whether a key resolves, never its value. Load
[references/settings.md § Decision-model key](references/settings.md#decision-model-key), explain
what the key is for and what happens without it, and if the user wants one, have them run
`kyber-weave arbiter setup` (masked prompt) or `arbiter install ... --key-stdin`. Never ask the
user to paste the key into the chat.

## Step 7 — Finish

1. Re-run `kyber-weave doctor --check .` and `kyber-weave arbiter status`.
2. Summarise in three short lists: already in place, installed or changed now, declined or left
   for later (each with the command that would do it).
3. Next steps: run the `kyber-docs-onboarding-and-assessment` skill to onboard this repository's
   documentation, gates and coding standards (it records the onboarding answer runs need), then
   start work through the conductor (`/conductor` or the harness's equivalent entry point).
4. Remind the user to restart any harness whose agents, hooks, or MCP configuration changed.
