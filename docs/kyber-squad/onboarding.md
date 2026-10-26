---
id: squad/onboarding
title: Kyber-Squad adoption and usage guide
doc-type: onboarding
component: KyberSquad
source-root: src/KyberWeave.Core/Squad
owner: dpalfery
last-reviewed: 2026-10-26
status: current
decided-by:
  - adr/0019-pi-native-subagents-and-primary-lowering
  - adr/0022-antigravity-native-agents
  - adr/0025-devin-native-agents-and-skill-lowering
  - adr/0028-devin-target-scoped-authoring-capability-profiles
  - adr/0034-factory-droid-entry-point-skill
  - adr/0037-squad-state-in-user-home
  - adr/0044-squad-lock-v2-and-instance-deployment-scope
code-refs:
  - SquadDeploymentPlan
  - SquadStateMigration
  - SquadCanonicalProfileStore
  - SquadLifecycleService
  - SquadModelPresetValidator
---

# Kyber-Squad adoption and usage guide

`kyber-weave squad` is the unified lifecycle and deployment control plane for agent ecosystems.
It manages the installation, update, inspection, and uninstallation of **24 canonical agents** and
**28 canonical skills**, with transactional recovery and state governance. Twelve harness targets
are declared; all twelve are currently implemented and registered.

---

## Command Reference

All squad operations are grouped under the `squad` branch:

```bash
# Install squad into project (or global) scope
kyber-weave squad install [path] [--target <targets>] [--exclude <targets>] [--global] [--dry-run] [--adopt] [--path <PATH>] [--yes] [--version <VERSION>] [--no-capture-plugin]

# Update an existing squad deployment
kyber-weave squad update [path] [--target <targets>] [--exclude <targets>] [--global] [--projects <all|none|PATH>] [--dry-run] [--replace-managed] [--path <PATH>] [--yes] [--version <VERSION>] [--no-capture-plugin]

# Uninstall squad deployment
kyber-weave squad uninstall [path] [--global] [--dry-run] [--path <PATH>] [--yes]

# Inspect installation health, version, and file drift
kyber-weave squad status [path] [--global] [--path <PATH>]

# Validate toolchain prerequisites and runtime health
kyber-weave squad doctor [path] [--global] [--path <PATH>]

# Build release packages (repository maintainer only)
kyber-weave squad pack --format <apm|plugins|all> --out <directory>

# Manage model presets
kyber-weave squad preset use <name> --target <targets> [--global] [--dry-run]
kyber-weave squad preset clear --target <targets> [--global]
kyber-weave squad preset list
kyber-weave squad preset show <name> --target <target>

# Run harness against an isolated instance configured with the active preset
kyber-weave squad run <target> [-- <args>...]

# Remove a specific harness preset instance
kyber-weave squad instance remove --target <target> --preset <name> [--yes]
```

**Version pinning.** `squad install` and `squad update` accept `-v|--version <VERSION>` to pin the
Kyber-Squad release version to deploy or update to — `X.Y.Z` or `X.Y.Z-prerelease`; a leading `v`
and `+build` metadata are accepted and stripped. Omitting the flag keeps the default: the running
CLI's own version. Input that is not a semantic version is rejected before any network call with
**exit code 2**; a well-formed version with no matching GitHub release fails with **exit code 1**
and a diagnostic naming the version, the repository, and the releases page listing available
versions. `squad update --version` may pin forward or backward relative to the receipt's recorded
version — there is no guard; the pinned version is deployed as asked. (`squad pack` carries the
same `-v|--version`, but there it stamps the package manifests and archives rather than pinning a
release to deploy.)

**Global scope and projects.** `--global` takes no path: the global anchor is your home directory
(one legacy single-root case excepted, see [Deployment Scopes](#deployment-scopes)), so a positional path or `--path`
given with `--global` is ignored with a warning. `--projects` applies to
`squad update --global` only, and is rejected with **exit code 2** anywhere else. It is covered under
[Updating global and registered projects](#updating-global-and-registered-projects).

---

## Harness Targets and Auto-Detection

Kyber-Squad declares twelve coding-harness targets:

| Target Token | Input Aliases | Strong Project Marker | Projection | Renderer Status |
|---|---|---|---|---|
| `codex` | — | `.codex/` | Native agents + primary-agent entry-point skills | Implemented and registered |
| `cursor` | — | `.cursor/` | Native agents | Implemented and registered |
| `claude` | — | `.claude/` | Native agents + primary-agent entry-point skill | Implemented and registered |
| `copilot` | `github-copilot` | `.github/copilot-instructions.md`, `.github/instructions/`, `.github/agents/`, `.github/prompts/`, `.github/hooks/` | Native agents | Implemented and registered |
| `opencode` | — | `.opencode/` | Native agents | Implemented and registered |
| `kilo` | — | `.kilo/` | Native agents | Implemented and registered |
| `antigravity` | — | *Explicit or configured target only* | Native agents (directory per agent) | Implemented and registered |
| `pi` | — | *Explicit or configured target only* | Native agents (with conductor lowered to skill) | Implemented and registered |
| `warp` | — | `.warp/` | Role-skill lowering | Implemented and registered |
| `factory` | `factory-droids` | `.factory/` | Native droids | Implemented and registered |
| `zcode` | — | `.zcode/` | Native agents (with conductor lowered to slash command) | Implemented and registered |
| `devin` | — | `.devin/` | Native agents (directory per agent, with conductor lowered to skill) | Implemented and registered |
| `commandcode` | `command-code` | `.commandcode/` | Native agents (with conductor also rendered as an entry-point skill) | Implemented and registered |

**Renderer coverage today**: this is the declared roster, not the set that currently installs.
Rendering canonical source into a harness's native files is Kyber-Weave's own code (see
[architecture.md](architecture.md#8-rendering)) — as of this writing `claude` (native subagents with primary-agent entry-point skill), `copilot` (native), `cursor` (native),
`codex` (native agents with primary-agent entry-point skills), `antigravity` (native: `.agents/agents/<name>/agent.md` + `.agents/skills/<name>/SKILL.md`, [ADR 0022](../adr/0022-antigravity-native-agents.md)), `opencode` (native), `kilo` (native), `pi` (native subagents with primary-agent lowering), `factory` (native), `warp` (fallback role-skill lowering to `.warp/skills/`), `zcode` (native subagents and skills, with primary agents lowered to slash commands), and `devin` (native subagents and skills, with primary agents lowered to a skill), and `commandcode` (native agents and skills, with primary agents also rendered as an entry-point skill) have renderers. All thirteen declared targets are covered. `kyber-weave squad doctor` reports current coverage.

### Detection Rules

- **Strong markers only**: Detection activates a target only when its designated directory or specific configuration file is present.
- **Negative fixtures**: Generic files such as `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, generic `.github/` directories, and `.agents/skills/` are negative fixtures that **never** activate a target.
- **Antigravity**: Requires explicit `--target antigravity` or configuration entry; `.agents/` will not auto-activate it.
- **Interactive fallback**: In an interactive terminal, if no target markers are discovered, `squad install` presents a multi-selection list of all 12 targets.
- **Non-interactive terminal**: If run without an interactive TTY and without detected or configured targets, `squad install` exits immediately with **exit code 2** and outputs the exact command required (e.g. `kyber-weave squad install --target <target>`).
- **Target-root echo and confirmation**: Every mutating run (`install`, `update`, `uninstall` without `--dry-run`) prints the resolved absolute target root and its scope (project/global) before any write. An interactive console is then asked to confirm; declining prints `Declined. No changes were made.` and exits with **exit code 2**. `--yes` skips the prompt for automation attached to a terminal; non-interactive consoles (scripts, CI, captured output) echo the root and proceed without prompting.
- **Deployment root selection**: The root comes from the positional `[path]`, which defaults to the current directory (`.`); `--path <PATH>` wins over that default. Supplying both a non-default positional and `--path` is rejected with **exit code 2** and a hint naming both forms. Under `--global` the root is the home directory and this selection is ignored, with a warning.
- **Update**: An explicit `--target` list is the complete desired deployment target set and takes precedence over the existing receipt. When `--target` is omitted, update reuses the receipt roster. Update never auto-detects filesystem markers.
- **Uninstall**: Uses the recorded target roster from the existing deployment receipt and never performs marker detection.

### Codex notes

Codex keeps each agent as a native TOML file at `.codex/agents/<name>.toml`. For a primary
agent without a same-name canonical skill, Kyber-Squad also derives an entry-point skill at
`.codex/skills/<name>/SKILL.md` from the agent description and instruction body, with the
agent's resource files beside it. This gives Codex a skill entry point for primary roles such
as `conductor` without removing the native agent file.

The primary shared identities `product-owner` and `bug-crusher` use their existing canonical
skills as entry points; Kyber-Squad emits no second derived skill for them. If another primary
agent has a same-name canonical skill but is not declared a shared identity, rendering fails
closed because both artifacts would claim the same skill path. In global scope, Codex output
uses `agents/<name>.toml` and `skills/<name>/SKILL.md` under the target root, without the
project `.codex/` prefix.

### Claude notes

Claude renders each primary agent as both a subagent (for enforced invocation) and an
entry-point skill (for main-thread access). The `conductor` derives its entry-point skill from the
agent body; the two root wrappers `product-owner` and `bug-crusher` reuse their canonical skill as
the entry point instead ([ADR 0036](../adr/0036-primary-agent-canonical-skill-entry-point.md)). The
following operational details ensure correct deployment and use:

**Three ways to run the conductor:**

The generated skill and subagent have renderer coverage. Their invocation, reference loading,
and coexistence have not yet been verified in a live Claude Code session; that check remains
open in the [Claude conductor live-verification todo](../todo/claude-conductor-live-verification.md).

1. **`/conductor <path or request>`** — main thread, in the conversation.
   - The entry-point skill runs in the main conversation where the Agent tool is available.
   - The session's tools, permission mode, MCP servers and model apply; the profile's enforced
     model and tool restrictions do not.
   - This is how the conductor reaches you from inside an ongoing session.

2. **`claude --agent conductor`** — enforced mode, CLI launch.
   - Enforces the agent's tool allow-list, `Agent(roster)` delegation roster, and the
     orchestration profile's `sonnet` model.
   - This is the strictly enforced alternative where every capability decision is enforced.

3. **`@agent-conductor`** — nested subagent, discouraged.
   - Runs the conductor as a nested subagent where the `Agent(roster)` parentheses are ignored
     (official Claude Code documentation); the conductor's real roster is `Agent(architect,
     azure-reader, …)` from its `delegates-to` declaration. By default the Agent tool works at
     this depth, but see Cloud sessions below for an observed environment constraint that
     removes it entirely.
   - This is the failure mode to avoid; use `/conductor` or `--agent` instead.

**Automatic skill loading (Q3-B).**
The skill's description stays in Claude's context, so Claude may auto-load the `/conductor`
skill into the main conversation by itself when a request matches it, without you typing
`/conductor`. For operators who want it opt-in only:
- Add `"permissions": {"deny": ["Skill(conductor)"]}` to your own Claude Code settings
  (`.claude/settings.json` or `settings.local.json`). The exact-match rule prevents Claude from
  invoking the skill; whether it also blocks a user-typed `/conductor` is undocumented and not
  yet verified live.
- Or start the enforced form with `claude --agent conductor`.

Either way it is a Claude Code setting, not a file Squad writes.

**Automatic delegation to the subagent (no frontmatter opt-out).**
Delegation to `@agent-conductor` cannot be switched off from frontmatter. The operator setting
`"permissions": {"deny": ["Agent(conductor)"]}` is documented Claude Code behaviour, not a file
Squad writes. Whether it blocks `@agent-conductor` specifically is not yet verified live.

**Scope precedence asymmetry.**
With both a global and a project Squad install:
- `/conductor` (skill) resolves to the **personal** copy at `~/.claude/skills/conductor/SKILL.md`
  (skills: personal over project).
- `@agent-conductor` and `--agent conductor` resolve to the **project** copy at
  `.claude/agents/conductor.md` (subagents: project over personal).

To ensure that personal skills load during project development, operators may configure personal
skills within project settings, e.g.:

```json
{
  "skills": ["~/.claude/skills"]
}
```

This points Claude at your personal skills directory while working in a project.

**Cloud sessions (SSH / Docker containers / devcontainers / github.dev).**
Claude Code environments with spawn depth 1 (including observed behavior in Cloud sessions)
disable nested delegation, making subagents unable to dispatch further agents. Operators should
verify subagent delegation within their execution environment.

**MCP servers.**
Subagents inherit MCP tools declared by the session. Ensure any MCP servers referenced in
`allowed-tools` are properly declared in Claude Code settings (`mcpServers`).

### Pi notes

Pi supports native subagents through `@gotgenes/pi-subagents` (`@tintinweb/pi-subagents`),
deploying agents to `.pi/agents/<name>.md` and skills to `.pi/skills/<name>/SKILL.md`
([ADR 0019](../adr/0019-pi-native-subagents-and-primary-lowering.md)).
Primary-invocation agents (`conductor`) are lowered to skills at `.pi/skills/<name>/SKILL.md`
to prevent nested subagent depth exhaustion.

**Tool extensions**: Pi agents requesting MCP or external tools map their requirements to
the verified `pi-tools` `ext:` entries, which render as `extensions: [pi-mcp-adapter]` plus
those `ext:` tool names appended to its capability-derived `tools` CSV. That agent requires
the `pi-mcp-adapter` extension in the deployment harness. `network.publish` still has no
built-in Pi tool and still records `permission-not-expressible`.
The next `squad update` / `squad install --target pi` re-renders receipt-managed agent files
with the overlay wiring and overwrites them, superseding any manual patch of those files.

**Coexistence with Antigravity**: `--target antigravity,pi` produces disjoint output trees —
`.agents/` (Antigravity agents and skills) and `.pi/` (Pi) — so neither target overwrites or orphans the
other. Pi's own skill loader also reads the project's `.agents/skills/` in addition to
`.pi/skills/`, and within a project `.pi/skills` is added first. With both targets installed, Pi
therefore keeps its `.pi/skills` copy for any name that also appears under `.agents/skills/` — for
example `conductor` — and prints a diagnostic naming the skipped `.agents/skills/` duplicate,
which is not loaded. Antigravity's native agents project to `.agents/agents/<name>/agent.md`, which
Pi's skill loader does not inspect, avoiding collision with Pi's subagent tree.

### Factory notes

Factory custom droids are Markdown files under `.factory/droids/` (project) and
`~/.factory/droids/` (personal / `--global`). Skills live at
`.factory/skills/<name>/SKILL.md` and `~/.factory/skills/<name>/SKILL.md`. Squad does not
write `.factory/agents/` or the compatibility trees `~/.agents/skills/` and
`~/.agent/skills/`.

**Entry point:** the Factory app dispatches droids as subagents and has no session-parent
selector ([ADR 0034](../adr/0034-factory-droid-entry-point-skill.md)), so each primary agent also
renders as an entry-point skill under `.factory/skills/<name>/SKILL.md` (personal
`~/.factory/skills/<name>/SKILL.md`). The `conductor` derives its entry-point skill from the agent
body; the two root wrappers `product-owner` and `bug-crusher` reuse their canonical skill
([ADR 0036](../adr/0036-primary-agent-canonical-skill-entry-point.md)). Invoke a primary agent
through its skill; the droid at `.factory/droids/<name>.md` is kept for
Task dispatch. The skill runs under the session's permissions — the droid's explicit tools
allow-list and `mcpServers: []` do not apply to it.

**Override:** when a project droid or skill shares a name with a personal one, the project
`.factory/` definition wins (docs.factory.ai, 2026-09-16). There is no documented environment
override for `~/.factory`.

**Model and reasoning effort:** a `factory:` value in `models.yml` may end in
`[reasoningEffort=<level>]` (for example `glm-5.3[reasoningEffort=high]`). The renderer writes the
bare model as `model:` and the level as `reasoningEffort:`. Factory accepts only `low`, `medium`,
or `high`; any other level fails the render. The renderer never emits `reasoningEffort` without a
pinned `model` (Factory ignores it on `inherit`), so leave the suffix off `inherit` entries.

**Inspect:** in a Factory session, `/droids` lists project and personal droids; `/skills`
lists discovered skills. Confirm names there after install.

### Command Code notes

Command Code agents are single Markdown files under `.commandcode/agents/` (project) and
`~/.commandcode/agents/` (personal / `--global`); skills live at
`.commandcode/skills/<name>/SKILL.md` and `~/.commandcode/skills/<name>/SKILL.md`. Squad does
not write the `.agents/skills/` tree Command Code also reads. Layout and frontmatter were read
from the `command-code` npm package 1.84.0.

**Entry point:** Command Code runs custom agents only as subagents, so each primary agent also
renders as an entry-point skill, the same shape as Factory. Invoke it as `/conductor`.

**Tools:** `tools` lists Command Code's snake_case tool names (`read_file`, `grep`,
`shell_command`, ...). An omitted `tools` grants nothing in Command Code; Squad always writes the
list. Subagents never get the `agent` tool, so `delegate` is not expressible, and MCP tools are
not granted.

**Models:** a `commandcode:` value in `models.yml` may end in `[reasoningEffort=<level>]`, where
the level is `off`, `low`, `medium`, `high`, `xhigh`, or `max`. No profile pins one yet, so every
agent inherits the session model.

### Devin notes

The `devin` target deploys to Devin Desktop — Cognition's desktop app for Windows and macOS,
formerly Windsurf — whose local agent, Devin Local, reads the Devin CLI's formats. Subagents
render as `.devin/agents/<name>/AGENT.md` and skills as `.devin/skills/<name>/SKILL.md`. With
`--global` they go to Devin's user configuration directory: `%APPDATA%\devin\` on Windows,
`~/.config/devin/` on macOS and Linux (`$XDG_CONFIG_HOME/devin/` when that is set). The
decisions behind the rendering are in
[ADR 0025](../adr/0025-devin-native-agents-and-skill-lowering.md).

**Before you install**: select the **Devin Local** agent, not Cascade — Cascade has no
subagents — and turn on **Subagents (Preview)** in Devin Settings. Keep Devin current: the
rendered `allowed-tools` lists name `write`, which Devin recognizes from CLI v3000.11.1
(2026-09-21). If an administrator has set the **Default subagent model** to None, or
`subagents_enabled` is `false`, no subagent runs and only the `conductor` skill is usable.

**Models**: each subagent pins a Devin model by exact id, effort included, from its model
profile. Without a pin, Devin runs a custom subagent on its router-chosen default subagent
model, not on the model you picked, so pinning is what keeps planners and reviewers on a
frontier model:

| Model profile | Devin model | Agents |
|---|---|---|
| `architect` | `claude-opus-5-5-high` | architect |
| `deep-planning` | `claude-opus-5-5-high` | bug-crusher-investigator, sql-database-architect |
| `general` | `swe-2-high` | dal-dev, github-devops, go-dev, product-owner, pulumi-dev, rust-dev, tauri-dev, typescript-dev |
| `fast` | `deepseek-v4-1-flash-high` | azure-reader, csharp-dev, docs-dev, maui-dev, python-dev, react-dev, research-agent, test-dev |
| `reviewer` | `grok-4-7-high` | code-reviewer, review-lens, review-triage |

The `conductor` skill runs on whatever you select in the model picker; Fusion is Devin's
recommendation there. A model outside your organization's allowlist is an administrator
change, not a Squad one. `devin doctor` flags a profile whose frontmatter Devin rejects, and
`/session-stats` in a session lists cost by model, which is how to confirm a subagent ran on
its pin — worth doing after install, because the Grok 4.7 and DeepSeek V4.1 Flash ids follow
Devin's naming but were not yet in its published model list when they were pinned.

**Devin Cloud**: Cloud sessions — including `devin --cloud` and a `/handoff` from a local
session — load no custom subagents; Devin documents them as CLI and Desktop only. The model
pins therefore never apply there. Cloud does discover skills, from `.devin/skills/` and five
other roots including `.claude/skills/`, `.github/skills/`, and `.agents/skills/`, so the Squad
skills are visible in Cloud while the Squad agents are not. The `conductor` skill is rendered
with `triggers: [user]` so Devin never starts it on its own; start it yourself in Desktop, and
do not invoke it in Cloud, where there is no roster for it to dispatch.

**Loading the same agent twice**: besides `.devin/`, Devin loads `.agents/agents/` and
`.agents/skills/` natively — exactly where the `antigravity` target writes — and by default
imports `.claude/skills/`, `.claude/commands/`, `.github/skills/` (and `~/.copilot/skills/`),
and `.windsurf/skills/` through its `read_config_from` setting. A repository with Squad
installed for `devin` and for `claude`, `copilot`, or `antigravity` therefore shows Devin two
definitions of the same agent or skill. `squad doctor` warns when it finds one and names the
remedy: for an imported tree, turn the import off in `.devin/config.json`, for example

```json
{
  "read_config_from": { "claude": false }
}
```

— bearing in mind the `claude` import also brings `CLAUDE.md` rules and Claude's MCP servers,
which turning it off drops too. `.agents/` cannot be turned off, so with Antigravity pick one of
the two targets per repository.

**Coming from Windsurf**: Devin still imports `.windsurf/skills/`, so hand-authored skills there
keep loading after installing `devin`. Squad never writes or removes anything under
`.windsurf/`; a skill there that shares a Squad skill's name is one of the duplicates above.

**Authoring roles on Devin**: Devin's file-change tools cannot create a file that does not exist,
so a role that only edits could not start a new plan or specification. `architect` and
`product-owner` therefore render on Devin from the `architect-devin` and `product-planning-devin`
capability profiles, which grant `exec` alongside the write tools. Both roles carry the same
harness-neutral initialise-before-edit instruction: `architect`'s plan-authoring reference, and
`product-owner`'s agent body plus the skill's spec-authoring reference, tell each role to
initialise the destination via shell (e.g. `touch <file>`) before editing.
`architect`'s `PLAN_READY` contract and `product-owner`'s
`SPEC_FINALIZED` contract still require `docs validate` and `docs drift` to pass. Every other role
renders from its shared profile and keeps `ask` narrowing to withheld. If a plan or spec write still
fails on a missing file, create the empty file and re-run the request — the conductor does this as
fallback when the harness withholds file creation from subagents. See
[ADR 0028](../adr/0028-devin-target-scoped-authoring-capability-profiles.md).

**Conductor and delegation**: Devin has no primary-agent primitive, so each primary agent is
deployed as a skill — the `conductor` as the skill `conductor`, invoked as `/conductor`, and the
two root wrappers `product-owner` and `bug-crusher` through their own canonical skills
([ADR 0036](../adr/0036-primary-agent-canonical-skill-entry-point.md)) — and runs in the main Devin Local
session, which dispatches the specialists as subagents. Its orchestrator boundary — no searching, editing, running commands,
or reading the web — is instruction-only on Devin, as on Pi. Subagents cannot delegate further
on Devin: Devin cannot limit nested delegation to a roster, so Squad
does not enable it. `code-reviewer` therefore applies each review lens itself and says in its
report that the council ran in-process; `architect` does its own sweeps and hands a live Azure
question back for the conductor to put to `azure-reader`; `product-owner` does its own external
research. The receipt records a `permission-not-expressible` degradation for each.

**MCP servers**: subagents' `allowed-tools` name the CodeGraph, context7, and Kyber-Weave MCP
tools individually. Configure those servers in Devin's `mcp_config.json` (project `.devin/` or
the user configuration directory); Devin also imports servers from `.mcp.json`,
`.cursor/mcp.json`, and `opencode.json`. Squad does not write any of them, and `squad doctor`
does not check them for Devin.

**Plugins**: Devin can install the Agent Plugins archive `squad pack` produces, which exposes
the skills as `/kyber-squad:<skill>`. Use that or `squad install --target devin`, not both, or
every skill appears under two names.

**Inspect:** `devin doctor` reports which custom profiles loaded and flags malformed
frontmatter, and `devin skills list` lists discovered skills. Confirm names there after install.

---

## Deployment Scopes

Kyber-Squad operates across three deployment scopes:

### 1. Project Scope (Default)

When run without `--global`, `kyber-weave squad` deploys agents and skills directly into the specified workspace root (defaulting to the current directory `.`):

- Deployment state is kept in your home directory, not in the repository, under `~/.kyber-weave/squad/projects/<root-key>/`:
  - `squad.lock.yml` — records bundle versions, toolchain signatures, and target configurations.
  - `squad.receipt.json` — tracks ownership, relative file paths, installation timestamps, and SHA-256 hashes.
- `<root-key>` is the lowercase SHA-256 of the project's canonical physical path. A symbolic link or a
  differently spelled path to the same directory resolves to the same state.
- Each project install, update, and import is recorded in the registry, `~/.kyber-weave/squad/registry.json`,
  which is how a global update finds the project. Uninstalling the project removes its entry. See
  [Registered projects](#registered-projects).
- The lock and receipt are no longer committed to version control, and Squad does not write them into the
  repository. Earlier releases did. Migration imports those files on first use (see
  [Migration from earlier layouts](#migration-from-earlier-layouts)); the in-repo copies are left in place,
  and `squad doctor` reports them.

### 2. Global Scope (`--global`)

Passing `--global` keeps Squad's lock and receipt in one place, `~/.kyber-weave/squad/global/`, and
writes the rendered agents and skills into each harness's own global directory — not into the project
path and not into a single shared "home" folder. `--global` is symmetric across `install`, `update`,
`uninstall`, `status`, and `doctor`.

The global anchor is your home directory. Under `--global`, a path argument (positional or `--path`) is
ignored with a warning, and the directory you run from has no effect. The warning is not printed when the
path is `.`. There is one global receipt, and global state is not listed in the registry.

One legacy case differs. If the home holds a pre-existing single-root global receipt, its relative paths
belong to one root. Migration runs first and records that root as an absolute `targetRoot`, and then the
anchor is that recorded root, not your home and not the directory you run from. A path given with
`--global` still produces a warning in this case, and the warning names the recorded root. A legacy receipt
that records no absolute root keeps the path you gave, or the current directory when you gave none, and
prints no such warning. Every later run anchors on the same root.

| Target | Global root (override env var → default) | Global-scope relative paths |
|---|---|---|
| `claude` | `$CLAUDE_CONFIG_DIR` → `~/.claude` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `codex` | `$CODEX_HOME` → `~/.codex` | `agents/<name>.toml`, `skills/<name>/SKILL.md` |
| `cursor` | `$CURSOR_CONFIG_DIR` → `~/.cursor` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `copilot` | `$COPILOT_HOME` → `~/.copilot` | `agents/<name>.agent.md`, `skills/<name>/SKILL.md` |
| `antigravity` | `~/.gemini/config` (no override) | `agents/<name>/agent.md`, `skills/<name>/SKILL.md` |
| `opencode` | `$OPENCODE_CONFIG_DIR` → `$XDG_CONFIG_HOME/opencode` → `~/.config/opencode` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `kilo` | `$XDG_CONFIG_HOME/kilo` → `~/.config/kilo` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `pi` | `$PI_CODING_AGENT_DIR` → `~/.pi/agent` | `agents/<name>.md`, `skills/<name>/SKILL.md` |
| `factory` | `~/.factory` (no override) | `droids/<name>.md`, `skills/<name>/SKILL.md` |
| `warp` | `~/.warp` (no override) | `skills/<name>/SKILL.md` |
| `zcode` | `$ZCODE_STORAGE_DIR` → `~/.zcode/cli/config.json` `storage.dir` → `~/.zcode` | `agents/<name>.md`, `commands/<name>.md`, `skills/<name>/SKILL.md` |
| `devin` | Windows: `%APPDATA%\devin`; macOS and Linux: `$XDG_CONFIG_HOME/devin` → `~/.config/devin` | `agents/<name>/AGENT.md`, `skills/<name>/SKILL.md` |

**OpenCode capture plugin (global scope).** When `squad install --global` or `squad update --global`
resolves the `opencode` target and succeeds, Squad also installs or upgrades the KyberDash OpenCode
capture plugin, `kyberdash-opencode.js`, under the OpenCode root in the table above. Squad runs
`kyberdash kyber capture plugin install --harness opencode --preserve --config-dir <that root>`. Preserve
mode keeps an edited or foreign plugin file with a warning, and it never touches `opencode.jsonc`.
The consent file `~/.kyberdash/opencode-plugin.json` is created with content off only when absent.
Pass `--no-capture-plugin` to skip the step. Project-scope runs never install the plugin. If `kyberdash`
is not installed, Squad prints a skip line that names `kyber-weave update`, and Squad's own result is
unchanged. A failed plugin step prints a warning and does not change Squad's exit code.
Restart OpenCode after the step reports an installed or upgraded plugin. Squad cannot restart it. See
[Updating](../install.md#updating) and the [KyberDash runbook](../dash/runbook.md).

Project-scope output is unchanged: each renderer still emits its `.{harness}/…` (or
`.agents/skills/…` / `.github/…`) prefix under the project root.

**Pre-migration for existing global files.** Squad never deletes an unmanaged file. If a
global root already holds a file whose name matches a canonical Squad identity but whose
bytes differ, `squad install --global` refuses that path (`UnmanagedCollision`) unless
`--adopt` finds identical bytes. Run `squad doctor --global` first: it lists every such
collision as a warning so the whole set is visible before install. Move or rename the
hand-authored file, or adopt it only when the bytes already match.

**Model pins.** Per-harness model tokens in `models.yml` are user-provider specific. An
unresolvable pin inherits silently (the renderer omits `model`) rather than failing the
install; confirm the resolved model in the harness itself after a first deploy.

The bundled assignments live in
[`profiles/models.yml`](../../products/kyber-squad/profiles/models.yml). Both `architect`
(architect and product-owner) and `deep-planning` (SQL architect and bug investigator)
are planning profiles. Antigravity uses `pro` for both planning profiles and `reviewer`,
and `flash` for the remaining profiles. Versioned model ids such as Claude's
`claude-haiku-5-5` and `claude-sonnet-5-5` pin that generation; aliases such as `opus`
follow the harness's alias resolution. Cursor's planning pin carries `effort=high`.

**Legacy rc.9/rc.10 global recovery.** See [Receipt version and layout contract](architecture.md#receipt-version-and-layout-contract) in the architecture section for layout semantics. If you have a global install from rc.9 or rc.10, `kyber-weave squad status --global` will inspect and flag the legacy layout. To complete migration, run `kyber-weave squad uninstall --global` followed by `kyber-weave squad install --global` with the current CLI.

### 3. Instance Scope (`SquadDeploymentScope.Instance`)

Instance scope isolates harness configuration and agent deployment for concurrent execution across
projects without modifying project trees or altering global harness configuration:

- Each instance directory is located at `~/.kyber-weave/squad/instances/<target>/<preset>/`.
- Deployment state (lock, receipt, and link manifests) lives in
  `~/.kyber-weave/squad/instance-state/<target>/<preset>/`.
- Mutex synchronization is keyed to the physical instance root key (`kyber-weave-squad-<root-key>`).
- Links back to the user's main harness home directory allow shared history, authentication, and
  user settings to function seamlessly while agents and models remain completely isolated.
- Instance roots are managed via `squad run`, listed in `squad status --global`, and cleaned via
  `squad instance remove`.

### Registered projects

The registry at `~/.kyber-weave/squad/registry.json` lists each project install by its physical path, the
time it was last updated in UTC, and its targets. It is an index, not the authority: a project's receipt
under `projects/<root-key>/` is what Squad trusts.

`kyber-weave squad status --global` lists each registered project and flags a stale one. A project is stale
when:

- its path no longer exists, because the repository was moved or deleted;
- the physical identity of its path no longer matches its key; or
- its receipt is missing.

A project that has a receipt but no registry entry is added back when Squad next lists the registry. Squad
lists it, and so rebuilds it, in `kyber-weave squad status --global`, `kyber-weave squad update --global`
(including when `--projects` names a path), and `kyber-weave squad doctor`. No other command reconciles
the registry.

If a registry write fails after an install or update has committed, Squad prints a warning that names the
fix, rerunning `kyber-weave squad status --global`. The transaction is not failed.

No command unregisters a stale entry. An entry whose directory no longer exists stays flagged until that
path returns, and `squad doctor` reports it as stale.

### Updating global and registered projects

`kyber-weave squad update --global` updates the global deployment. After a successful global update, Squad
can also update registered projects, so they stay on the same release.

In an **interactive terminal**, a picker opens after the global update succeeds. It lists **All**, **None**,
then each registered project path. A stale project is shown as `(stale: <reason>)`.

- **All** selects every project that is not stale.
- **None**, or an empty selection, selects nothing.
- **None** combined with another choice fails validation, and the picker asks again.
- A selected stale project is skipped with a warning.

**Without a terminal, or with `--projects`**, the picker does not open. `--projects` accepts `all`, `none`,
or one or more project paths, and it can be repeated. Without the flag, a non-interactive run updates no
project.

```bash
kyber-weave squad update --global --projects all
kyber-weave squad update --global --projects ~/src/service-a --projects ~/src/service-b
```

- A named path must be a registered project or hold importable in-repo state. Otherwise the command exits
  with **exit code 2** before any global work, and the hint names the nearest registered path.
- A named path that is registered but stale is warned about and skipped.
- `--projects` is valid only with `squad update --global`. Anywhere else it exits with **exit code 2**.

Each selected project is updated through the same path as `kyber-weave squad update` run in that project:

- It uses that project's own targets and exclusions, from its configuration and receipt.
- The global run passes `--version` and `--dry-run` through. It does not pass `--target`, `--exclude`, or
  `--replace-managed`.
- Selecting a project counts as consent, so the per-project confirmation is not shown.
- Projects are updated one after another. A failure in one project does not stop the others. A summary is
  printed, and the command exits with **exit code 1** if any project failed.
- Under `--dry-run`, a selected project that still needs its in-repo state imported is reported as
  skipped, with the note that it will be imported on the next run without `--dry-run`. Any other
  selected project is previewed. Nothing is written either way.

The picker also opens under `--global --dry-run` when the terminal is interactive and `--projects` is
absent, and the projects you pick are then handled as above.

To overwrite a project's locally edited managed files, run `kyber-weave squad update --replace-managed` in
that project. A global run never forces an overwrite in a project.

If the global update fails, no picker opens and the command exits with **exit code 1**.

### Replacing managed files in global scope

`kyber-weave squad update --global --target all --replace-managed` overwrites files Squad owns in the global
deployment, even when you have edited them. It never overwrites a file Squad does not own. If a new path
collides with a file you wrote by hand, the whole run fails with `UnmanagedCollision` and writes nothing. The
hint names the two ways forward: `kyber-weave squad install --global --adopt`, when the bytes already match,
or moving the file aside.

### Migration from earlier layouts

Migration runs automatically before every squad verb (`install`, `update`, `status`, `uninstall`, and
`doctor`). It never deletes a file, and running it again after it has succeeded changes nothing.

- **Global.** Squad looks for a global deployment in the legacy application-data tree, at
  `KyberWeave/squad/roots/<root-key>/`, and ignores empty folders. If exactly one non-empty legacy global
  deployment exists and `~/.kyber-weave/squad/global/` has no receipt, Squad copies its receipt and lock
  there. The legacy files are left in place. A legacy deployment recorded against a single root needs that
  root to be found: from its recorded absolute target root, from the registry, or by matching the home
  directory or the directory you run from. If the root cannot be found, the deployment is reported and not
  migrated.
- **More than one** non-empty legacy global deployment: none is migrated. Each is reported with guidance.
  Squad does not choose one for you.
- **Projects.** When a project-scope command, or a picker-driven update, reaches a project that has no home
  state but still has `.kyber-weave/squad.receipt.json` in its repository, Squad imports the receipt and lock
  into `~/.kyber-weave/squad/projects/<root-key>/` and registers the project. The repository's files are not
  changed. From then on the home state is authoritative.
- **Dry run.** With `--dry-run`, nothing is written, and the migration ledger is not written either. The output
  states that the legacy state at each path will be migrated on the next run without `--dry-run`. Install,
  update, and uninstall stop after that report.
- **Locking and interruption.** Each copy or import runs under the same lease an install takes for the root.
  If another transaction holds it, nothing is written, a warning says so, and the next run retries. A global
  copy writes the lock first and the receipt last. A crash that leaves a lock with no receipt is completed on
  the next run, as long as the home lock is byte-identical to the legacy lock. A different home lock blocks the
  copy and is reported for you to inspect.
- **Ledger.** Squad records each completed copy and import in `~/.kyber-weave/squad/migration-ledger.json`, so
  an uninstall of migrated state is not undone when the legacy files are migrated again. A malformed ledger is
  read as empty, with a warning. Fresh installs are not affected by the ledger.

**Upgrade every machine that runs Squad.** Older CLI versions keep reading the legacy locations, and the legacy
files are kept so that rolling back to an older CLI still finds state. Once a project has been imported, its
home state wins, so any change made to its legacy files after import is not seen by the newer CLI.

### Doctor findings

`kyber-weave squad doctor` runs the migration before it reports, so it can write. It is not a read-only check.
It reports these:

- **Stale registry entries**: a registered project whose path is missing, whose key no longer matches, or whose
  receipt is missing. Doctor warns that global updates skip it until its path is restored or its receipt is
  reinstalled. Uninstall cannot clear the entry: it requires an existing project directory, so an entry whose
  directory is gone stays flagged until that path returns. `squad update --global --projects` skips a stale
  named path with a warning.
- **Unmigrated legacy global roots**: a legacy global deployment that was not migrated, with the reason and the
  guidance for it. This includes the roots that were ambiguous (more than one legacy receipt, none chosen), those
  whose binding root could not be resolved, and those already migrated, whose home copy was removed on purpose.
- **Retained in-repo state**: a registered project, still live, that holds `.kyber-weave/squad.lock.yml` or
  `.kyber-weave/squad.receipt.json` from an earlier release. Squad retained these files, and they should be
  removed from version control. A stale project is not warned about this way.

---

## Common Workflows

### First-Time Installation

To install the squad into a project:

```bash
kyber-weave squad install
```

To preview the planned operations without writing to disk:

```bash
kyber-weave squad install --dry-run
```

To deploy specific targets or exclude certain harnesses:

```bash
kyber-weave squad install --target claude,cursor --exclude warp
```

To install into a directory other than the current one, name the deployment root with `--path` (a non-default positional `[path]` together with `--path` is rejected):

```bash
kyber-weave squad install --path /path/to/project --target claude,cursor
```

### Adopting Existing Files

If a project already contains agent or skill files that match canonical Squad content byte-for-byte, `squad install` normally treats pre-existing unmanaged files as a conflict. Use `--adopt` to claim exact-match files into Squad ownership:

```bash
kyber-weave squad install --adopt
```

Any pre-existing file whose content diverges from the canonical source will not be adopted and will safely abort the transaction.

### Updating Deployments

When a new version of Kyber-Weave is available, upgrade the project's deployed squad:

```bash
kyber-weave squad update
```

Without `--target`, update reuses the deployment receipt's target roster. To add a target,
pass the complete desired set, including every existing target that should remain. For example,
to add `codex` to an existing `claude` deployment:

```bash
kyber-weave squad update --target claude,codex
```

The explicit list replaces the desired target roster for that update; it is not merged with the
receipt. Update does not auto-detect filesystem markers. The same selection rules apply to
`--dry-run`.

By default, `squad update` preserves locally modified managed files and reports a drift warning. To intentionally overwrite local modifications with the upstream canonical version, pass `--replace-managed`:

```bash
kyber-weave squad update --replace-managed
```

When a release no longer renders a file that an earlier install or update deployed, `squad update`
deletes it if its bytes still match the receipt. A copy an operator edited stays in place and stays
owned, even with `--replace-managed`; delete it by hand if it is no longer wanted. This is how
`create-pull-request-github` leaves a host that deployed it before it was retired into
`create-pull-request`.

### Checking Deployment Status and Health

Verify the integrity of installed files, inspect version alignment, and detect unmanaged drift:

```bash
kyber-weave squad status
```

Run diagnostic checks on renderer coverage (which of the twelve declared targets can install today), the Kyber-Weave MCP server, and — in a workspace with `.devin/` — any Squad agent or skill Devin would load twice:

```bash
kyber-weave squad doctor
```

Both scopes also report stale registry entries, unmigrated legacy global roots, and in-repo state retained
from an earlier release. See [Doctor findings](#doctor-findings).

### Uninstalling

Remove all managed files and deployment state:

```bash
kyber-weave squad uninstall
```

For scripted runs attached to a terminal, `--yes` skips the confirmation prompt:

```bash
kyber-weave squad uninstall --yes
```

Locally modified files are preserved during uninstallation unless explicitly cleaned up by the operator. If no receipt exists, uninstall is a clean no-op.

`kyber-weave squad uninstall --global` removes only global state and files. It opens no picker and changes no
registered project. A project uninstall also removes that project's registry entry.

---

## Model Presets and Concurrent Harness Instances

A **model preset** is a named, user-owned YAML configuration that overrides default agent model
assignments across harnesses. While canonical agents define semantic model profiles (`fast`,
`architect`, `general`, `reviewer`, `deep-planning`) mapped to default tokens in the bundled
`models.yml`, presets allow switching inference providers or models on demand without editing
managed agent files or waiting for upstream releases.

### 1. Model Preset Files

Presets live in the user home directory under `~/.kyber-weave/squad/presets/<name>.yml`.
The preset name is the file stem and must match `^[a-z0-9][a-z0-9-]{0,63}$`.

Preset files use the `kyber-squad.model-preset/v1` schema and mirror the profile shape of
`models.yml`:

```yaml
schema: kyber-squad.model-preset/v1
profiles:
  fast:
    claude: claude-haiku-5-5
    opencode: zai-coding-plan/glm-5.3-flash
  architect:
    claude: inherit
```

- **Inheritance and fallbacks**: A preset performs a key-by-key merge per category. Categories or
  harnesses omitted by the preset fall back to bundled `models.yml` definitions.
- **Explicit inherit**: Setting a model token to `inherit` omits the `model` frontmatter key on
  rendered agents, matching bundled behavior.
- **Default token**: An optional `default` key per profile category specifies a fallback token for
  any harness not explicitly mapped in that category.
- **Validation**: `squad preset use` and `squad preset show` strictly validate preset keys
  against the resolved canonical profiles before any rewrite or print. Referencing an unknown
  profile category or harness fails validation immediately with **exit code 1**, reporting the
  exact file path and line number before writing any files. Install and update enforce the
  same check at render time.

### 2. Managing Presets (`squad preset`)

Model presets are managed using four subcommands:

```bash
# Apply a preset to target harnesses in project scope
kyber-weave squad preset use <name> --target claude,copilot

# Apply a preset globally across all deployments of the target
kyber-weave squad preset use <name> --target claude --global

# Preview model changes without modifying files or state
kyber-weave squad preset use <name> --target claude --dry-run

# Revert target harnesses to bundled model profiles
kyber-weave squad preset clear --target claude [--global]

# List all available presets and their active deployments
kyber-weave squad preset list

# Inspect resolved models for a preset and target harness
kyber-weave squad preset show <name> --target claude
```

- **Transactional updates**: `squad preset use` executes through the existing atomic `update`
  transaction engine. It updates `squad.lock.yml` to schema v2 (`kyber-squad.lock/v2`),
  recording the applied preset name and lowercase SHA-256 digest under `model-presets`.
- **Canonical profile resolution**: `use`, `clear`, and `show` resolve canonical model profiles
  in this order: the `products/kyber-squad` checkout when one is present, then the snapshot
  stored for the deployment lock's `SquadVersion`, then the newest snapshot, then nothing.
  `show` has no deployment context, so it resolves checkout first and then the newest snapshot.
  Resolution from a checkout keeps repo-built behaviour; outside a checkout the snapshot is
  what reflects the release the deployment was rendered from.
- **Skills are never rewritten**: model rewrites apply only to deployed agent files. Any
  receipt `.md` whose final path segment is `SKILL.md` or whose path contains a `skills/`
  segment passes through byte-identical — canonical skills and primary-agent entry-point
  skill projections alike — and only agent files whose profile category resolves are touched.
  Files with an unresolvable identity keep their bytes and produce no diff row.
- **Unresolvable profiles fail fast**: when no checkout and no snapshot resolve, `use` and
  `clear` exit with **exit code 1** in both dry-run and apply, and `show` exits with
  **exit code 1** as well — before any file rewrite, lock change, or printed plan. The error
  names the remedy: run `kyber-weave squad update` to refresh the profile snapshot; running
  from a kyber-weave checkout also works.
- **Snapshot persistence**: committed `squad install` and `squad update` runs (including
  `squad run` instance provisioning) write one snapshot per Squad release version to
  `~/.kyber-weave/squad/profiles/<version>.yml` (schema `kyber-squad.canonical-profiles/v1`,
  carrying the `models.yml` shape plus the agent-name-to-category map). Dry-run writes
  nothing. A snapshot write that fails after the deployment commits produces a warning
  naming the remedy (rerun `squad update`); the deployment itself is never failed for it.
  One file is kept per distinct version and snapshots are never pruned.
- **Migration for pre-snapshot deployments**: deployments installed before snapshots existed
  have no snapshot file. After upgrading the CLI, the first `preset use/clear/show` fails
  with the exit-1 remedy above; one `squad update` (global and/or in the project) re-renders
  from the release and writes the snapshot, and preset verbs work from then on.
- **Preserved versioning**: Switching presets never changes the release version of deployed agents.
- **Dry run preview**: Passing `--dry-run` prints each agent whose resolved model changes in the format
  `<agent>: <old-model> -> <new-model>` and exits without modifying state or files.
- **Status reporting**: `squad status` reports the active preset per target. If a preset file has
  been modified since it was applied, status flags `(changed since applied)`. If a referenced
  preset file has been deleted, status flags `(missing)` and exits with **exit code 1**, because
  future updates will fail until repaired.

### 3. Project Preset Selections

When running in project scope without `--global`, `squad preset use` records the project's chosen
preset under `~/.kyber-weave/squad/selections/<root-key>.yml` (schema `kyber-squad.project-selection/v1`),
where `<root-key>` is the SHA-256 of the project's canonical physical path.

- **Clean repositories**: Selections are saved exclusively in the user's home state tree. No preset
  or configuration file is written into the repository, leaving `git status` clean.
- **Automatic persistence**: Project selections survive releases and updates. Running `squad update`
  or installing additional targets automatically honors the recorded project selection.

### 4. Running Isolated Harness Instances (`squad run`)

Different projects frequently require different model presets on the same harness simultaneously.
Deploying agents directly into project repositories clutters source control, while modifying the
global home creates cross-project interference.

Kyber-Squad solves this with `squad run`:

```bash
# Launch a harness using the active project preset selection
kyber-weave squad run claude

# Pass arguments directly to the underlying harness executable
kyber-weave squad run claude -- --verbose --dangerously-skip-permissions
```

When `kyber-weave squad run <target> [-- <args>...]` is executed:
1. Squad resolves the current project's selection from `~/.kyber-weave/squad/selections/<root-key>.yml`.
   If no selection is configured for `<target>`, it exits with **exit code 2** and suggests `squad preset use`.
2. It looks up the harness in `SquadInstanceCatalog`. If the target status is `Unsupported` or
   `SpikePending`, it exits with **exit code 2** stating the reason.
3. It provisions or refreshes an isolated instance directory at
   `~/.kyber-weave/squad/instances/<target>/<preset>/` under `SquadDeploymentScope.Instance`.
4. It synchronizes links from the main harness home directory into the instance, ensuring user
   authentication, settings, and command history remain available.
5. It spawns the harness CLI with the appropriate configuration or home redirection environment
   variable (e.g. `CLAUDE_CONFIG_DIR`) pointing at the instance directory.
6. Arguments following `--` are forwarded verbatim to the harness CLI. On Windows, npm `.cmd` shims
   are located via `PATHEXT` and executed under `cmd.exe` with proper argument escaping.
7. Squad waits for harness process exit and propagates its exact exit code.

### 5. Cleaning Instances (`squad instance remove`)

Harness instances can be inspected via `squad status --global` (under the `Instances` heading)
and pruned with `instance remove`:

```bash
kyber-weave squad instance remove --target claude --preset fast [--yes]
```

Removal reads the instance's `links.json` manifest and safely removes created symbolic links,
hard links, and directory junctions without following them into the user's main home directory.

### 6. Harness Instance Support Matrix

The `SquadInstanceCatalog` defines the redirection mechanism and support status for each target:

| Target | Executable | Kind | Environment Variable | Status | Behavior and Isolation |
|---|---|---|---|---|---|
| `claude` | `claude` | `HomeReplace` | `CLAUDE_CONFIG_DIR` | `Ready` | Replaces home; settings/history linked; Keychain sign-in shared. |
| `codex` | `codex` | `HomeReplace` | `CODEX_HOME` | `Ready` | Replaces home; `auth.json` linked in file mode; keyring mode per instance. |
| `copilot` | `copilot` | `HomeReplace` | `COPILOT_HOME` | `Ready` | Replaces entire home; keychain token shared. |
| `pi` | `pi` | `HomeReplace` | `PI_CODING_AGENT_DIR` | `Ready` | Replaces agent dir; settings, sessions, and auth linked. |
| `opencode` | `opencode` | `ConfigLayer` | `OPENCODE_CONFIG_DIR` | `SpikePending` | Layers config over global home; override priority pending verification. |
| `kilo` | `kilo` | `ConfigLayer` | `KILO_CONFIG_DIR` | `SpikePending` | Layers config over global home; override priority pending verification. |
| `cursor` | `cursor` | `ConfigLayer` | `CURSOR_CONFIG_DIR` | `SpikePending` | Config layer; CLI config redirection pending verification. |
| `zcode` | `zcode` | `ConfigLayer` | `ZCODE_STORAGE_DIR` | `SpikePending` | Config layer; `ZCODE_STORAGE_DIR` vs `ZCODE_DATA_BASE_DIR` pending verification. |
| `antigravity` | `antigravity` | `None` | *None* | `Unsupported` | Antigravity does not support configuration or home directory redirection via environment variable. |
| `factory` | `factory` | `None` | *None* | `Unsupported` | Factory does not support configuration or home directory redirection via environment variable. |
| `commandcode` | `commandcode` | `None` | *None* | `Unsupported` | CommandCode only supports global `HOME` redirection, which affects all system tools. |
| `devin` | `devin` | `None` | *None* | `Unsupported` | Devin stores configuration in system app data and does not support instance directory redirection. |
| `warp` | `warp` | `None` | *None* | `Unsupported` | Warp deploys skills only and does not support agent directory redirection. |

- **`HomeReplace`**: The environment variable points the harness at a complete home directory.
  Rendered agents exist directly in the instance, while all other top-level directories and files
  from the main home are linked.
- **`ConfigLayer`**: The harness supports a secondary configuration folder loaded after global config.
  Only rendered agents reside in the instance folder.
- **`Ready`**: Verified and supported for concurrent execution via `squad run`.
- **`SpikePending`**: Candidate configuration variables identified; awaiting live platform verification.
  Attempting `squad run` exits 2 with guidance.
- **`Unsupported`**: Harness cannot isolate configuration per instance without global side effects.
  Attempting `squad run` exits 2 with explanation.

---

## PR review workflows

Use the [code-review-loop skill](../../products/kyber-squad/skills/code-review-loop/SKILL.md)
when authorizing a review feedback cycle on your chosen platform: fixes, validation,
pushed commits, linked replies, thread resolution, and another review. Specify the PR
and reviewer, and say whether you want
one re-review or continued rounds until clean. The skill preserves that authorization scope.
Its linked `code-review-loop.md` procedure is included in packages and harness deployments.

Use `pr-review-fix-comments` when you prefer step-by-step approval for each fix.

---

## Delivery circuit-breaker

For delivery intake, a concrete failure is diagnosed before routing by artifact,
even when the request also names an explicit plan/spec choice or an existing plan.
The conductor preserves that choice and existing artifact for the post-diagnosis
handoff. An explicit choice without an undiagnosed failure skips the intake assessment.
For a nontrivial diagnosed defect in a Ready task, the architect assesses whether the
approved contract still fits. Required revisions return to the artifact author and
reopen approval before implementation; unchanged contracts return to the original worker.

The conductor keeps the same developer session for a task's review fixes and
verification when the harness can resume it. The reviewer remains independent and
can retain its own review history between passes. Stateless harnesses receive a
self-contained recovery brief with previous findings, decisions, and failed approaches.
The conductor records session handles and replacement reasons on the execution artifact.

Deployed `conductor`, `csharp-dev`, `test-dev`, and `github-devops` agents stop
unbounded test-fix loops: 3 incremental fixes per implementation or rework dispatch, 2 conductor
rework dispatches per failure cluster per run, A→B→A oscillation detection, and
escalation through `STATUS: ESCALATION` / `ESCALATION: circuit-breaker`. Caps,
cluster identity, JEV checkpoints, and the closed trigger set are in
[architecture §9](architecture.md#9-conductor-execution-circuit-breaker) and the
[requirements harvest](requirements.md#conductor-execution-circuit-breaker).
Kyber-Squad has no separate runbook; this onboarding page is the operator pointer.

---

## Packaging (`squad pack`)

`kyber-weave squad pack` is a maintainer-only command for building release archives. It requires execution from the root of the Kyber-Weave repository containing `KyberWeave.sln` and `products/kyber-squad/squad.yml`:

```bash
# Build APM distribution zip
kyber-weave squad pack --format apm --out ./artifacts

# Build Agent Plugins v1 zip (skills + MCP only)
kyber-weave squad pack --format plugins --out ./artifacts

# Build both distribution artifacts
kyber-weave squad pack --format all --out ./artifacts
```

Running `squad pack` outside the repository root fails immediately with a diagnostic directing the operator to rerun the command from the Kyber-Weave repository root (or run `squad install` if deploying agents and skills to a project).

Both archive formats recurse through each skill directory. They contain all 28 canonical
`SKILL.md` files plus the 82 retained supplemental resources, and retained local skill references
must resolve in the extracted package. The APM archive additionally contains the 23 canonical
agents with their 10 owned reference files; the Agent Plugins archive never contains agents or
agent-owned resources. A fresh deployment renders every file an owner's Markdown links reach
beside its principal — 140 files on Copilot today — with authored relative links resolving inside
the target output; every skill resource reaches this render except
`skills/setup-dev-environment/agents/openai.yaml`, which stays packaged-only Codex skill-UI
metadata. The tracked root `.github/` self-deployment predates resource delivery and is refreshed
only by a release. Every retained resource has a reviewed disposition in the
[skill-resource dispositions audit](skill-resource-dispositions.md): non-policy content stays in
its skill directory as its durable home, portable policy lives in the
`products/kyber-squad/standards/` templates, and nothing was deleted.

Rendered `.github` trees are deployment output and are not added to the canonical product tree by
`squad pack`.

`products/kyber-squad/` is the canonical and package authority. The repository no longer carries a
project-level self-deployment: the root `.github/agents/`, `.github/skills/`,
`.kyber-weave/squad.lock.yml`, and `.kyber-weave/squad.receipt.json` were removed.

---

## Exit Codes

| Exit Code | Meaning |
|---|---|
| `0` | Success, healthy/clean status, or successful dry run. |
| `1` | Configuration error, prerequisite failure, file drift, dirty state, or transactional failure. Also a failed global update, or a `squad update --global --projects` run in which any selected project failed. Also preset file syntax error (`file:line`), unknown profile category / harness key validation failure, unresolvable canonical profiles for `preset use/clear/show` (remedy: `squad update`), missing preset referenced by lock, harness executable missing on `PATH`, or filesystem link creation failure during `squad run`. |
| `2` | Invalid CLI arguments, unrecognized target/format token, declined write confirmation, or target resolution required in non-interactive mode. Also `--projects` outside `squad update --global`, or a named project path that is neither registered nor holds importable in-repo state. Also unknown preset name, `squad run` with no preset selection for target, or attempting `squad run` against a harness with status `Unsupported` or `SpikePending`. |

---

## Related

- [Kyber-Squad architecture](architecture.md) — transaction engine, AgentIR, lowering, state model, and conductor execution circuit-breaker
- [ADR 0037](../adr/0037-squad-state-in-user-home.md) — why lock and receipt state moved to the user home, with the registry, the global anchor, and migration
- [ADR 0044](../adr/0044-squad-lock-v2-and-instance-deployment-scope.md) — Squad Lock v2 and Instance deployment scope
- [Requirements and degradation contract](requirements.md) — KS-001 through KS-008 specifications and the circuit-breaker harvest
- [Configuration](../configuration.md) — configuring squad settings in `.kyber-weave/kyber-weave.yml`
- [Distribution and release flow](../distribution.md) — release packaging and artifact verification
