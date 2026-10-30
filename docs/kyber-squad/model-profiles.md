---
id: squad/model-profiles
title: Model profiles and model presets
doc-type: onboarding
component: KyberSquad
source-root: src/KyberWeave.Core/Squad
owner: dpalfery
last-reviewed: 2026-10-29
status: current
decided-by:
  - adr/0037-squad-state-in-user-home
  - adr/0044-squad-lock-v2-and-instance-deployment-scope
code-refs:
  - SquadModelPreset
  - SquadModelPresetStore
  - SquadModelPresetValidator
  - SquadModelResolution
  - SquadModelPresetSelectionStore
---

# Model profiles and model presets

Canonical agents do not name a model. Each agent names a **semantic model profile**, and
Kyber-Squad resolves that profile to a concrete token for the harness being rendered. A
**model preset** is a user-owned file that overrides those tokens without editing managed
agent files or waiting for a release.

Use a preset when the goal is "run this tier on a different model or provider on this
machine". Do not edit a deployed agent file, and do not edit the bundled
`products/kyber-squad/profiles/models.yml` matrix: the next `squad update` overwrites the
first, and the second is shipped policy.

> A model profile is not a **harness profile**. A harness profile (configured under
> `ontology.harness-profiles` in `.kyber-weave/kyber-weave.yml`) describes a harness's
> directory and parity capabilities. A model profile selects inference, and is described here.

## The six profiles

| Profile | Intent | Canonical agents that use it |
|---|---|---|
| `architect` | Deep architectural and product reasoning | `architect`, `product-owner` |
| `deep-planning` | Investigative planning and diagnosis | `bug-crusher-investigator`, `sql-database-architect` |
| `fast` | Bounded, checkable work | Most `*-dev` agents, `docs-dev`, `test-dev`, triage roles |
| `general` | Standard implementation and support | `dal-dev`, `github-devops`, `pulumi-dev`, `rust-dev`, `tauri-dev`, `typescript-dev` |
| `orchestration` | Coordination and dispatch | `conductor` |
| `reviewer` | Adversarial code reading | `code-reviewer`, `review-lens`, `review-triage` |

Bundled tokens live in `products/kyber-squad/profiles/models.yml`, one column per harness.
The bundled matrix is the baseline; a preset changes only what it names.

## Preset files

Presets live under `~/.kyber-weave/squad/presets/<name>.yml`. The file stem is the preset
name and must match `^[a-z0-9][a-z0-9-]{0,63}$`.

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

That example is an OpenCode-only profile on the GitHub Copilot provider. `orchestration` is
omitted, so it keeps the bundled token.

### Merge rules

- **Key-by-key merge.** A category or harness the preset omits keeps the bundled token. The
  merge is per harness, so a preset can move `opencode` while leaving `claude` alone.
- **`inherit` removes the key.** Setting a token to `inherit` omits the rendered `model`
  key, which defers to that harness's own model selection. This is not the same as omitting
  the entry: omission keeps the bundled token.
- **`default` is the target-neutral fallback.** Per category, an optional `default` applies
  to harnesses without an explicit entry. Use it only when the same token is valid across
  those harnesses; harness tokens are not portable.
- **One file per provider set.** Give each provider or model mix its own preset name
  (`kyber-weave-copilot`, `kyber-weave-anthropic`, ...). Switching is one command, and
  `preset clear` returns a target to the bundled matrix.

## Per-harness token shapes

A token is only valid on the harness it was written for. Do not carry an id across columns.

| Harness key | Token shape | Example | Notes |
|---|---|---|---|
| `opencode` | `<provider>/<model-id>` | `github-copilot/claude-opus-5.5` | Provider-qualified; verify the id exists in that provider's catalog |
| `claude` | alias or full id | `opus`, `claude-haiku-5-5` | Aliases are `opus`, `sonnet`, `haiku` |
| `copilot` | Copilot display name with ` (copilot)` | `Grok 4.7 (copilot)` | Omitting the key defers to the user's picker |
| `cursor` | id, optional `[effort=...]` | `claude-sonnet-5-5[effort=high]` | `composer-2.5[]` is a valid empty suffix |
| `codex` | bare model id | `gpt-6.1-sol` | |
| `antigravity` | tier `pro` or `flash` | `pro` | Reasoning effort comes from the profile category, not the token |
| `pi` | `<provider>/<model>[thinking=<level>]` | `zai/glm-5.3[thinking=high]` | The suffix is parsed into `thinking` |
| `factory` | id with `[reasoningEffort=<level>]` | `glm-5.3[reasoningEffort=high]` | |
| `kilo` | provider/model or display string | `kilo/deepseek/deepseek-v4-pro-0813` | Display strings may contain spaces |
| `zcode` | bare model id | `glm-5.3` | |
| `devin` | id with effort suffix | `claude-opus-5-5-high` | Omitting the key leaves the router-chosen default |
| `warp` | — | — | Warp renders no model key; a preset entry has no effect |
| `commandcode` | bare model id | — | No bundled column; a preset supplies the first value |

## Create a profile

1. **Pick the harness.** Use `kyber-weave squad status --global` and
   `kyber-weave squad status .` to see which targets are deployed. A harness that is
   installed but not deployed needs `kyber-weave squad install --target <token>` first.
2. **Pick the tiers and models.** For each profile, choose the model intent and verify the
   exact token against the harness's own catalog:
   - OpenCode: `opencode models` lists provider-qualified ids.
   - Claude: `opus`, `sonnet`, `haiku`, or a full id the harness accepts.
   - Copilot: the display name the Copilot picker shows, with its ` (copilot)` suffix.
   - Other harnesses: confirm the id in that harness's picker or CLI before writing it.
3. **Write the preset** at `~/.kyber-weave/squad/presets/<name>.yml` using the schema above.
4. **Validate and preview.** These two commands write nothing:

   ```bash
   kyber-weave squad preset show <name> --target <harness>
   kyber-weave squad preset use <name> --target <harness> [--global] --dry-run
   ```

   `show` prints the resolved matrix and validates the profile categories and harness keys.
   The dry run prints each agent whose model changes as `<agent>: <old> -> <new>`.

5. **Apply it** with the scope you want.

## Verify the model id before you write it

Preset validation checks **profile categories and harness keys only**. It does not check
provider catalogs. A misspelled or unavailable model id writes successfully and fails when
the harness starts, so verify it in the harness first.

For OpenCode, the provider must also be configured and authenticated in that harness. List
its ids with `opencode models`; if the provider is absent, sign in with `opencode auth login`
(or the TUI's connect flow) before expecting the profile to resolve.

## Apply, switch, and revert

```bash
# Apply globally
kyber-weave squad preset use <name> --target <harness> --global

# Apply to the current project only
kyber-weave squad preset use <name> --target <harness>

# Preview the model diff without writing
kyber-weave squad preset use <name> --target <harness> --global --dry-run

# Show which preset each target is using
kyber-weave squad preset list

# Inspect the resolved matrix
kyber-weave squad preset show <name> --target <harness>

# Re-render an existing deployment; the recorded preset is re-applied
kyber-weave squad update --global

# Return a target to the bundled matrix
kyber-weave squad preset clear --target <harness> [--global]
```

Always pass `--target`. Without it, `preset use` falls back to every target in the
deployment receipt, which is rarely what a profile author intends.

`preset use` updates `squad.lock.yml` (schema `kyber-squad.lock/v2`) with the preset name
and lowercase SHA-256 digest under `model-presets`. Switching presets never changes the
release version of the deployed agents.

## Multiple profiles at once

- **One preset per target at a time.** `preset use` replaces that target's recorded preset;
  presets do not stack.
- **Different targets may hold different presets simultaneously.** For example OpenCode on
  Copilot and Claude on Anthropic, both active.
- **Different projects may hold different presets.** A project selection (no `--global`) is
  stored under `~/.kyber-weave/squad/selections/<root-key>.yml`, keyed by the project's
  physical root. It leaves the repository clean and survives updates.
- **The bundled matrix is always selectable.** `preset clear` returns a target to it without
  deleting any preset file.
- **`squad run` uses the active project preset**, not the global one. Only `claude`, `codex`,
  `copilot`, and `pi` are `Ready` for isolated instances; `opencode`, `kilo`, `cursor`, and
  `zcode` are `SpikePending` and exit `2`, and `antigravity`, `factory`, `commandcode`,
  `devin`, and `warp` are `Unsupported`. See
  [the instance support matrix](onboarding.md#3-harness-instance-support-matrix).

## After you apply it

A preset rewrites deployed instruction files on disk. The model is resolved when a session
or agent starts, so:

- **Start a new session.** A session already running keeps the model it started with.
- **Restart the harness only if a new session still shows the old model** or cannot see the
  new agent. Some harnesses cache their agent and model list at launch.
- **Nothing is installed and no service is restarted** by applying a preset.
- **Verify one agent from the changed tier** and confirm its model in the session, or compare
  `preset show` with the agents the dry run listed.

## Troubleshooting

| Symptom | Cause and remedy |
|---|---|
| Exit `1`, unknown profile category, file and line named | The preset names a category absent from `models.yml`. Use one of the six profiles above |
| Exit `1`, unknown harness key | The key is not a registered target token. Check the table above |
| Exit `1`, no canonical profiles resolve | Neither a checkout nor a version snapshot was found. Run `kyber-weave squad update`, or work from a kyber-weave checkout |
| Exit `2`, unknown preset name or missing `--target` | Client-input error; check the file stem and the target list |
| Model accepted but rejected at run time | The id does not exist in that provider's catalog, or the provider is not authenticated. Re-verify and re-apply |
| `squad status` says `(changed since applied)` | The preset file was edited after it was applied. Re-run `preset use` |
| `squad status` says `(missing)` and exits `1` | The referenced preset file was deleted. Restore it or `preset clear` the target |

Canonical profiles resolve from the `products/kyber-squad` checkout first, then the snapshot
for the deployment's Squad version under `~/.kyber-weave/squad/profiles/`, then the newest
snapshot. That order is why a preset behaves the same in a checkout and in a consumer
repository.

## Related

- [CLI command surface](../cli-command-surface.md) — every `squad preset` flag and exit code.
- [Concurrent harness instances](onboarding.md#1-running-isolated-harness-instances-squad-run) —
  `squad run`, instance cleanup, and the support matrix.
- [Kyber-Squad architecture](architecture.md) — the preset overlay seam and lock v2.
