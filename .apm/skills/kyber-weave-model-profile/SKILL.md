---
name: kyber-weave-model-profile
description: "Builds a Kyber-Squad model preset through an interview: asks which installed harness to target, then tier by tier asks for a rough model intent, verifies the exact provider and model id, shows the resolved profile, writes it, and prints the apply, rollback, and restart instructions. Use when a user wants to create, switch, or compare model profiles or presets across harnesses, pin a named or provider-qualified model to a tier, or asks what to do after a profile is applied. Do not use for installing or updating the CLI or the harnesses themselves — that is `kyber-weave-cli`."
license: MIT
metadata:
  author: dpalfery
  version: 0.2.0
---

# Model profiles and model presets

Kyber-Squad agents do not name a model. Each agent names a **semantic model profile**, and
the profile resolves to a concrete token per harness. A **model preset** is a user-owned
file that overrides those tokens for one or more harnesses, without editing managed agent
files or waiting for a release.

This skill is an interview. Do not create a preset file, or run any mutating verb, until the
user has answered the questions below and confirmed the rendered profile. Ask one question at
a time, prefer the harness's question UI when it provides one, and never guess a model id.

The authoritative page is `<docs-root>/kyber-squad/model-profiles.md`, under the Config Reg
declared in the repository root `AGENTS.md`. The command surface and exit codes are in
`<docs-root>/cli-command-surface.md`. Instance running and its support matrix are in
`<docs-root>/kyber-squad/onboarding.md`. Read the owning page before using a flag this skill
does not list.

## Step 1 — Ask which harness

First establish which harnesses are available on this machine, then ask which one the profile
is for.

```bash
kyber-weave squad status --global      # targets actually deployed globally
kyber-weave squad status .             # targets deployed in this project
kyber-weave squad doctor --global      # renderers and prerequisites
```

The thirteen supported target tokens are:

```
codex, cursor, claude, copilot, opencode, kilo, antigravity,
warp, factory, pi, zcode, devin, commandcode
```

A target that is installed but not deployed cannot take a preset yet. Present the result in two
groups: **ready** (targets with a receipt entry in the global or project deployment) and
**supported but not deployed** (the remaining tokens). If the user says a harness in the
second group is installed, offer to deploy it with
`kyber-weave squad install --target <token>` before continuing. Ask: **which harness should
this profile target?** Accept a comma-separated list when the user wants one profile applied
to several harnesses.

If the user names a harness this skill cannot verify as deployed, stop and say so rather than
writing a preset that resolves to nothing.

## Step 2 — Ask tier by tier, then verify the id

Ask which of the six profiles the user wants to set. For each one, ask for a **rough intent**
in the user's own words — "strongest reasoning", "cheap and fast", "best at reading code",
"long context" — and whether they have a **provider preference** (GitHub Copilot, Anthropic,
ZAI, OpenCode Zen, and so on). If they have no preference, propose two or three candidates
across providers for that intent. Then resolve the chosen candidate to its exact token and
confirm it with the user.

| Profile | Intent | Agents that use it |
|---|---|---|
| `architect` | Deep architectural and product reasoning | `architect`, `product-owner` |
| `deep-planning` | Investigative planning and diagnosis | `bug-crusher-investigator`, `sql-database-architect` |
| `fast` | Bounded, checkable work | Most `*-dev`, `docs-dev`, `test-dev`, triage roles |
| `general` | Standard implementation and support | `dal-dev`, `github-devops`, `pulumi-dev`, `rust-dev`, `tauri-dev`, `typescript-dev` |
| `orchestration` | Coordination and dispatch | `conductor` |
| `reviewer` | Adversarial code reading | `code-reviewer`, `review-lens`, `review-triage` |

Verification is mandatory and harness-specific. Do not accept a model name the user typed
from memory without checking it against the harness's own catalog:

- **OpenCode** — list live provider-qualified ids with `opencode models`, or filter with
  `opencode models <provider>`. The token is `<provider>/<model-id>`, for example
  `github-copilot/claude-opus-5.5`.
- **Claude** — the accepted tokens are the aliases `opus`, `sonnet`, `haiku`, or a full model
  id the Claude harness accepts.
- **Copilot** — the token is the model display name the Copilot picker shows, including its
  ` (copilot)` suffix, for example `Grok 4.7 (copilot)`.
- **All other harnesses** — use the exact shape in the per-harness table below and verify the
  id in that harness's own model picker or CLI before writing it. If you cannot verify it,
  say so and ask the user to confirm from the harness.

A preset is validated against profile categories and harness keys only. It does **not**
validate provider catalogs, so an unverified id writes successfully and fails at run time.

For each tier, report the candidate set and the exact token you intend to write, then ask for
confirmation. Never carry an id from one harness column to another.

## Step 3 — Ask for the name and the scope

Ask for a preset name. It becomes the file stem and must match
`^[a-z0-9][a-z0-9-]{0,63}$`. Suggest a name that groups related profiles, for example
`kyber-weave-copilot`, `kyber-weave-anthropic`, or `kyber-weave-zai`.

Then ask the scope:

- **Project** (omit `--global`) — the selection applies to the current project root. This is
  the scope `kyber-weave squad run <harness>` uses.
- **Global** (`--global`) — the selection applies to the machine-wide deployment.

The same preset may be selected in both scopes; the selection is per target and per project
root. Ask which scope to record now, and note that the other can be set later with one command.

## Step 4 — Show the profile before writing it

Render the exact YAML and a resolved matrix. Do not write the file until the user confirms
both.

```yaml
schema: kyber-squad.model-preset/v1
profiles:
  architect:
    opencode: github-copilot/claude-opus-5.5
  deep-planning:
    opencode: github-copilot/claude-opus-5.5
  fast:
    opencode: github-copilot/mai-code-1.1-flash
  general:
    opencode: github-copilot/gpt-6.1-terra
  reviewer:
    opencode: github-copilot/grok-4.7
```

The matrix must state, per tier, either the confirmed token or **kept from the bundled matrix**.
Omission is not the same as `inherit`:

- **Omit a category or harness** — the bundled `models.yml` token stays in force.
- **Set a token to `inherit`** — the rendered `model` key is removed, so the harness chooses
  its own model.
- **Set `default` per category** — applies to harnesses without an explicit entry. Use it only
  when the token is valid across harnesses.

Warn when a profile is incomplete: if the user asked for "a full profile", every tier they
care about must be named, because omitted tiers keep the bundled matrix.

## Step 5 — Build and validate

After confirmation, write `~/.kyber-weave/squad/presets/<name>.yml`, then validate before any
deployment change:

```bash
kyber-weave squad preset show <name> --target <harness>
kyber-weave squad preset use <name> --target <harness> [--global] --dry-run
```

`show` prints the resolved matrix and validates the categories and harness keys. The dry run
lists every agent whose model changes and writes nothing. If either fails, fix the preset and
re-run; never fall through to applying.

## Step 6 — Ask who applies it, then print the commands

Ask: **"Apply this now, or leave it for you to run?"**

If the skill applies it, run the same command without `--dry-run`, then re-run `preset show`
and `preset list` to prove the selection:

```bash
kyber-weave squad preset use <name> --target <harness> [--global]
kyber-weave squad preset show <name> --target <harness>
kyber-weave squad preset list
```

Either way, finish by printing this block with the real name, harness, and scope filled in:

```bash
# Inspect the resolved profile
kyber-weave squad preset show <name> --target <harness>

# Apply it (global)
kyber-weave squad preset use <name> --target <harness> --global

# Apply it to this project instead
kyber-weave squad preset use <name> --target <harness>

# See which preset each target is using
kyber-weave squad preset list

# Re-render an existing deployment; the recorded preset is re-applied
kyber-weave squad update --global

# Revert this target to the bundled matrix
kyber-weave squad preset clear --target <harness> [--global]

# Launch a harness with the active project preset
# (Ready targets only: claude, codex, copilot, pi)
kyber-weave squad run <harness>
```

## Step 7 — Tell the user what to do next

Applying a preset rewrites deployed instruction files on disk. A model is resolved when a
session or agent starts, so:

- **Start a new session.** A session already running keeps the model it started with.
- **Restart the harness only if a new session still shows the old model or cannot see the new
  agent.** Some harnesses enumerate agents and models at launch and cache that list.
- **No restart is needed for the preset file itself.** Nothing is installed and no service is
  restarted.
- **Verify before trusting it.** Dispatch one agent from the changed tier and confirm the
  model in the session, or compare `preset show` output with the agents you expect to change.

If the harness rejects the model, the id is wrong for that harness or provider. Repoint the
preset and re-apply; do not edit deployed agent files.

## Multiple active profiles

- **One preset per target at a time.** `preset use` replaces that target's recorded preset;
  it does not stack.
- **Different targets may hold different presets simultaneously.** For example OpenCode on
  Copilot, Claude on Anthropic, and Kilo on ZAI, all active at once.
- **Different project roots may hold different presets.** A project selection (no `--global`)
  wins for that project and is what `squad run` uses; the global selection covers the rest.
- **The bundled matrix is a selectable "profile".** `preset clear` returns a target to it
  without deleting any preset file.
- **Run a harness against the active project preset** with `kyber-weave squad run <harness>`.
  Set the project selection first; the global selection is not what `run` uses. Only `claude`,
  `codex`, `copilot`, and `pi` are `Ready`; `opencode`, `kilo`, `cursor`, and `zcode` are
  `SpikePending` and exit `2`, and `antigravity`, `factory`, `commandcode`, `devin`, and `warp`
  are `Unsupported`.
- **Updates preserve the selection.** `squad update` re-applies the recorded preset; only
  `preset clear` or a different `preset use` changes it.

## Per-harness token shapes

| Harness key | Token shape | Bundled example | Notes |
|---|---|---|---|
| `opencode` | `<provider>/<model-id>` | `github-copilot/claude-opus-5.5` | Verify with `opencode models` |
| `claude` | alias or full id | `opus`, `claude-haiku-5-5` | Aliases are `opus`, `sonnet`, `haiku` |
| `copilot` | display name with ` (copilot)` | `Grok 4.7 (copilot)` | Omitted key defers to the user's picker |
| `cursor` | id, optional `[effort=...]` | `claude-sonnet-5-5[effort=high]` | `composer-2.5[]` is an empty suffix |
| `codex` | bare model id | `gpt-6.1-sol` | |
| `antigravity` | tier `pro` or `flash` | `pro` | Reasoning effort comes from the tier |
| `pi` | `<provider>/<model>[thinking=<level>]` | `zai/glm-5.3[thinking=high]` | Suffix is parsed into `thinking` |
| `factory` | id with `[reasoningEffort=<level>]` | `glm-5.3[reasoningEffort=high]` | |
| `kilo` | `<provider>/<model-path>` | `kilo/deepseek/deepseek-v4-pro-0813` | Model path may contain slashes; display strings with spaces are also valid |
| `zcode` | bare model id | `glm-5.3` | |
| `devin` | id with effort suffix | `claude-opus-5-5-high` | Omitted key leaves the router default |
| `warp` | — | — | Renders no model key; a preset entry has no effect |
| `commandcode` | bare model id | — | No bundled column; a preset supplies the first value |

## Validation and failure modes

| Symptom | Cause and remedy |
|---|---|
| Exit `1`, unknown profile category, file and line named | The preset names a category absent from `models.yml`; use one of the six above |
| Exit `1`, unknown harness key | The key is not a registered target token; check the table above |
| Exit `1`, no canonical profiles resolve | No checkout and no version snapshot. Run `kyber-weave squad update`, or work from a checkout |
| Exit `2`, unknown preset name, or no `--target` and no deployment receipt | Client-input error; check the file stem and the target list |
| Model accepted, rejected at run time | The id does not exist in that provider's catalog; re-verify and re-apply |

Canonical profiles resolve from the `products/kyber-squad` checkout first, then the snapshot
for the deployment's Squad version under `~/.kyber-weave/squad/profiles/`, then the newest
snapshot.

## Example

The user says: "I want a coding profile for OpenCode on Copilot; strongest model for planning,
a cheap one for dev work, a good reviewer."

1. `squad status --global` shows `opencode` deployed. Confirm OpenCode as the harness.
2. Ask tier by tier. Verify each candidate with `opencode models`, then confirm the tokens:
   `architect`/`deep-planning` → `github-copilot/claude-opus-5.5`, `fast` →
   `github-copilot/mai-code-1.1-flash`, `general` → `github-copilot/gpt-6.1-terra`,
   `reviewer` → `github-copilot/grok-4.7`, `orchestration` omitted so the bundled token stays
   in force.
3. Name it `kyber-weave-copilot`, scope global.
4. Show the YAML and the resolved matrix. The user confirms.
5. Write the file, run `preset show` and `preset use --dry-run`. Both pass.
6. Apply `kyber-weave squad preset use kyber-weave-copilot --target opencode --global`, then
   print the Step 6 command block.
7. Tell the user to start a new OpenCode session, and to restart only if the new model is not
   picked up.

## Never

- **NEVER** write a preset before the harness, tiers, name, and scope are answered and the
  rendered profile is confirmed.
- **NEVER** write an unverified model id. Verification is harness-specific and mandatory.
- **NEVER** edit a deployed agent file or `products/kyber-squad/profiles/models.yml` to
  change a model. Author a preset instead.
- **NEVER** hand-edit `~/.kyber-weave/squad/selections/` or a `squad.lock.yml` preset entry.
- **NEVER** copy a token between harness columns.
- **NEVER** put credentials or provider secrets in a preset. It records model ids only.
