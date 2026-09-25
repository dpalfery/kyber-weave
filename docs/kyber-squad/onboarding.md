---
id: squad/onboarding
title: Kyber-Squad adoption and usage guide
doc-type: onboarding
component: KyberSquad
source-root: src/KyberWeave.Core/Squad
owner: dpalfery
last-reviewed: 2026-09-25
status: current
decided-by:
  - adr/0019-pi-native-subagents-and-primary-lowering
  - adr/0022-antigravity-native-agents
code-refs:
  - SquadDeploymentPlan
---

# Kyber-Squad adoption and usage guide

`kyber-weave squad` is the unified lifecycle and deployment control plane for agent ecosystems.
It manages the installation, update, inspection, and uninstallation of **21 canonical agents** and
**24 canonical skills**, with transactional recovery and state governance. Eleven harness targets
are declared; all eleven are currently implemented and registered.

---

## Command Reference

All squad operations are grouped under the `squad` branch:

```bash
# Install squad into project (or global) scope
kyber-weave squad install [path] [--target <targets>] [--exclude <targets>] [--global] [--dry-run] [--adopt] [--path <PATH>] [--yes]

# Update an existing squad deployment
kyber-weave squad update [path] [--global] [--dry-run] [--replace-managed] [--path <PATH>] [--yes]

# Uninstall squad deployment
kyber-weave squad uninstall [path] [--global] [--dry-run] [--path <PATH>] [--yes]

# Inspect installation health, version, and file drift
kyber-weave squad status [path] [--global] [--path <PATH>]

# Validate toolchain prerequisites and runtime health
kyber-weave squad doctor [path] [--global] [--path <PATH>]

# Build release packages (repository maintainer only)
kyber-weave squad pack --format <apm|plugins|all> --out <directory>
```

---

## Harness Targets and Auto-Detection

Kyber-Squad declares eleven coding-harness targets:

| Target Token | Input Aliases | Strong Project Marker | Projection | Renderer Status |
|---|---|---|---|---|
| `codex` | — | `.codex/` | Native agents | Implemented and registered |
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

**Renderer coverage today**: this is the declared roster, not the set that currently installs.
Rendering canonical source into a harness's native files is Kyber-Weave's own code (see
[architecture.md](architecture.md#8-rendering)) — as of this writing `claude` (native subagents with primary-agent entry-point skill), `copilot` (native), `cursor` (native),
`codex` (native), `antigravity` (native: `.agents/agents/<name>/agent.md` + `.agents/skills/<name>/SKILL.md`, [ADR 0022](../adr/0022-antigravity-native-agents.md)), `opencode` (native), `kilo` (native), `pi` (native subagents with primary-agent lowering), `factory` (native), `warp` (fallback role-skill lowering to `.warp/skills/`), and `zcode` (native subagents and skills, with the primary agent lowered to a slash command) have renderers. All eleven declared targets are covered. `kyber-weave squad doctor` reports current coverage.

### Detection Rules

- **Strong markers only**: Detection activates a target only when its designated directory or specific configuration file is present.
- **Negative fixtures**: Generic files such as `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, generic `.github/` directories, and `.agents/skills/` are negative fixtures that **never** activate a target.
- **Antigravity**: Requires explicit `--target antigravity` or configuration entry; `.agents/` will not auto-activate it.
- **Interactive fallback**: In an interactive terminal, if no target markers are discovered, `squad install` presents a multi-selection list of all 11 targets.
- **Non-interactive terminal**: If run without an interactive TTY and without detected or configured targets, `squad install` exits immediately with **exit code 2** and outputs the exact command required (e.g. `kyber-weave squad install --target <target>`).
- **Target-root echo and confirmation**: Every mutating run (`install`, `update`, `uninstall` without `--dry-run`) prints the resolved absolute target root and its scope (project/global) before any write. An interactive console is then asked to confirm; declining prints `Declined. No changes were made.` and exits with **exit code 2**. `--yes` skips the prompt for automation attached to a terminal; non-interactive consoles (scripts, CI, captured output) echo the root and proceed without prompting.
- **Deployment root selection**: The root comes from the positional `[path]`, which defaults to the current directory (`.`); `--path <PATH>` wins over that default. Supplying both a non-default positional and `--path` is rejected with **exit code 2** and a hint naming both forms.
- **Update and uninstall**: Always consume the recorded target roster from the existing deployment receipt and never perform re-detection.

### Claude notes

Claude renders the primary `conductor` agent as both a subagent (for enforced invocation) and an
entry-point skill (for main-thread access). The following operational details ensure correct
deployment and use:

**Three ways to run the conductor:**

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
  `.claude/agents/conductor.md` (agents: project over user).

This scope-precedence rule is Claude Code's built-in behaviour; Squad documents it but neither
designs nor corrects it.

**Cloud sessions (CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1).**
On 2026-09-25, cloud sessions were observed with `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH=1`,
which disables subagent nesting entirely:
- A subagent conductor has no Agent tool and cannot delegate. Use `/conductor` instead.
- Specialists that delegate further (`architect`, `product-owner`, `code-reviewer`) cannot
  nest in cloud sessions.
- `/conductor` runs in the main thread at depth 0, so it works and reaches you.

This is not documented in public Claude Code docs; it is an observed environment constraint.
By default a subagent can spawn subagents up to 3 layers below the main conversation; the
override is the environment variable `CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH`.

**Global paths and live reload.**
- Global subagent: `~/.claude/agents/conductor.md` with references beside it.
- Global skill: `~/.claude/skills/conductor/SKILL.md` with references beside it.
- Claude Code watches `~/.claude/skills/` and picks up added skills without restart (provided
  the directory exists before the session started; a top-level `~/.claude/` directory created
  after session start needs a restart).
- `/conductor` appears only after `squad update` with a release that carries it; until then,
  older releases still have the subagent-only form.

---

### Pi notes

Pi is a third-party coding agent that renders Squad agents natively through the
`@tintinweb/pi-subagents` extension
([ADR 0019](../adr/0019-pi-native-subagents-and-primary-lowering.md)). The following operational
details ensure correct deployment and coexistence with other targets:

**Prerequisite**: Pi agent output needs `@tintinweb/pi-subagents` 0.19.0 or later, which requires
Pi 0.84.0 or later. Install the extension via Pi's `settings.json` `packages` entry beforehand;
`squad doctor` does not detect it, so verify it is configured before installing the `pi` target.

**Project trust**: Pi's agent loader has no trust check, so project `.pi/agents/` loads
regardless of project trust. Project `.pi/skills/` loads only for projects trusted in
`~/.pi/agent/trust.json`, so the conductor skill under `.pi/skills/conductor/` requires project
trust. Global skills and agents are not gated by trust.

**Override and collision behaviour**: Project `.pi/agents/<name>.md` files override
identically-named global agents in `~/.pi/agent/agents/` silently — no diagnostic is printed, and
this is the intended project authority mechanism. By contrast, project `.pi/skills/conductor/`
taking precedence over a global `~/.pi/agent/skills/conductor` is a real name collision: Pi prints
a diagnostic naming the skipped global file, and `/skill:conductor` then loads the Squad
conductor. `conductor-v3` has a different name and is unaffected; outside the project, the global
skill is unchanged.

**Subagent depth and delegation**: The conductor runs as a top-level skill at depth 0, not as a
subagent, so its specialist delegates (`architect`, `code-reviewer`, and others) start at depth 1
and can still delegate one level further to depth 2 under Pi's default `maxSubagentDepth: 2`
(main 0, subagent 1, nested child 2). A depth cap set below 2 silently disables the nested
delegation that `architect` and `code-reviewer` require. Recommend `maxSubagentDepth: 2` or
higher in `.pi/subagents.json` for full capability.

**Fallback and strict dispatch**: By default, an unknown agent type falls back to Pi's
`general-purpose` agent (all tools). To enforce strict dispatch of Squad agents only, set
`fallbackSubagent: none` in the project's `.pi/subagents.json`; Squad does not write this file, so
the operator manages it.

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

**Override:** when a project droid or skill shares a name with a personal one, the project
`.factory/` definition wins (docs.factory.ai, 2026-09-16). There is no documented environment
override for `~/.factory`.

**Inspect:** in a Factory session, `/droids` lists project and personal droids; `/skills`
lists discovered skills. Confirm names there after install.

---

## Deployment Scopes

Kyber-Squad operates across two deployment scopes:

### 1. Project Scope (Default)

When run without `--global`, `kyber-weave squad` deploys agents and skills directly into the specified workspace root (defaulting to the current directory `.`):

- Deployment state is persisted in project files:
  - `.kyber-weave/squad.lock.yml` — records bundle versions, toolchain signatures, and target configurations.
  - `.kyber-weave/squad.receipt.json` — tracks ownership, relative file paths, installation timestamps, and SHA-256 hashes.
- Lock and receipt files should be committed to version control to maintain team-wide agent synchronization.

### 2. Global Scope (`--global`)

Passing `--global` keeps Squad's lock and receipt in the per-user application-data tree
(`KyberWeave/squad/roots/<root-key>/`) and writes the rendered agents and skills into each
harness's own global directory — not into the project path and not into a single shared
"home" folder. `--global` is symmetric across `install`, `update`, `uninstall`, `status`,
and `doctor`.

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

By default, `squad update` preserves locally modified managed files and reports a drift warning. To intentionally overwrite local modifications with the upstream canonical version, pass `--replace-managed`:

```bash
kyber-weave squad update --replace-managed
```

### Checking Deployment Status and Health

Verify the integrity of installed files, inspect version alignment, and detect unmanaged drift:

```bash
kyber-weave squad status
```

Run diagnostic checks on renderer coverage (which of the eleven declared targets can install today) and the Kyber-Weave MCP server:

```bash
kyber-weave squad doctor
```

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

Both archive formats recurse through each skill directory. They contain all 24 canonical
`SKILL.md` files plus the 64 retained supplemental resources, and retained local skill references
must resolve in the extracted package. The APM archive additionally contains the 21 canonical
agents with their 10 owned reference files; the Agent Plugins archive never contains agents or
agent-owned resources. A fresh deployment renders every owner's resources beside its principal —
113 files on Copilot today — with authored relative links resolving inside the target output. The
tracked root `.github/` self-deployment predates resource delivery and is refreshed only by a
release; surplus packaged content remains until the
[resource-migration todo](../todo/migrate-skill-resources-into-standards.md) is accepted.

Rendered `.github` trees are deployment output and are not added to the canonical product tree by
`squad pack` or the golden synchronization.

`products/kyber-squad/` is the canonical and package authority. The repository root
`.github/agents/`, `.github/skills/`, `.kyber-weave/squad.lock.yml`, and
`.kyber-weave/squad.receipt.json` form an intentional stale self-deployment. This synchronization
leaves them untouched; a human will refresh them after a fresh Kyber-Weave release candidate.

---

## Exit Codes

| Exit Code | Meaning |
|---|---|
| `0` | Success, healthy/clean status, or successful dry run. |
| `1` | Configuration error, prerequisite failure, file drift, dirty state, or transactional failure. |
| `2` | Invalid CLI arguments, unrecognized target/format token, declined write confirmation, or target resolution required in non-interactive mode. |

---

## Related

- [Kyber-Squad architecture](architecture.md) — transaction engine, AgentIR, lowering, and state model
- [Requirements and degradation contract](requirements.md) — KS-001 through KS-008 specifications
- [Configuration](../configuration.md) — configuring squad settings in `.kyber-weave/kyber-weave.yml`
- [Distribution and release flow](../distribution.md) — release packaging and artifact verification
