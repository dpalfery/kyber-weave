---
id: squad/onboarding
title: Kyber-Squad adoption and usage guide
doc-type: onboarding
component: KyberSquad
source-root: src/KyberWeave.Core/Squad
owner: dpalfery
last-reviewed: 2026-08-23
status: current
code-refs:
  - SquadDeploymentPlan
---

# Kyber-Squad adoption and usage guide

`kyber-weave squad` is the unified lifecycle and deployment control plane for agent ecosystems.
It manages the installation, update, inspection, and uninstallation of **22 canonical agents** and
**26 canonical skills** across 10 coding harnesses, with transactional recovery and state governance.

---

## Command Reference

All squad operations are grouped under the `squad` branch:

```bash
# Install squad into project (or global) scope
kyber-weave squad install [path] [--target <targets>] [--exclude <targets>] [--global] [--dry-run] [--adopt]

# Update an existing squad deployment
kyber-weave squad update [path] [--global] [--dry-run] [--replace-managed]

# Uninstall squad deployment
kyber-weave squad uninstall [path] [--global] [--dry-run]

# Inspect installation health, version, and file drift
kyber-weave squad status [path] [--global]

# Validate toolchain prerequisites and runtime health
kyber-weave squad doctor [path] [--global]

# Build release packages (repository maintainer only)
kyber-weave squad pack --format <apm|plugins|all> --out <directory>
```

---

## Harness Targets and Auto-Detection

Kyber-Squad supports 10 coding harnesses:

| Target Token | Input Aliases | Strong Project Marker | Fallback Mode |
|---|---|---|---|
| `codex` | — | `.codex/` | Native agents |
| `cursor` | — | `.cursor/` | Native agents |
| `claude` | — | `.claude/` | Native agents |
| `copilot` | `github-copilot` | `.github/copilot-instructions.md`, `.github/instructions/`, `.github/agents/`, `.github/prompts/`, `.github/hooks/` | Native agents |
| `opencode` | — | `.opencode/` | Native agents |
| `kilo` | — | `.kilo/` | Native agents |
| `gemini` | — | `.gemini/` | Role-skill lowering |
| `antigravity` | — | *Explicit or configured target only* | Role-skill lowering |
| `warp` | — | `.warp/` | Role-skill lowering |
| `factory` | `factory-droids` | `.factory/` | Native agents |

**Renderer coverage today**: this is the approved roster, not the set that currently installs.
Rendering canonical source into a harness's native files is Kyber-Weave's own code (see
[architecture.md](architecture.md#8-rendering)) — as of this writing `claude` (native), `copilot` (native), `cursor` (native),
`codex` (native), and `antigravity` (fallback role-skill lowering to `.agents/skills/`) have renderers. Requesting any other target fails before the release is even downloaded,
naming the missing target(s) and pointing at `docs/todo/<target>.md`, which has what an
implementer needs to add it. `kyber-weave squad doctor` reports current coverage.

### Detection Rules

- **Strong markers only**: Detection activates a target only when its designated directory or specific configuration file is present.
- **Negative fixtures**: Generic files such as `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, generic `.github/` directories, and `.agents/skills/` are negative fixtures that **never** activate a target.
- **Antigravity**: Requires explicit `--target antigravity` or configuration entry; `.agents/` will not auto-activate it.
- **Interactive fallback**: In an interactive terminal, if no target markers are discovered, `squad install` presents a multi-selection list of all 10 targets.
- **Non-interactive terminal**: If run without an interactive TTY and without detected or configured targets, `squad install` exits immediately with **exit code 2** and outputs the exact command required (e.g. `kyber-weave squad install --target <target>`).
- **Update and uninstall**: Always consume the recorded target roster from the existing deployment receipt and never perform re-detection.

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

Passing `--global` targets the user's home/global environment rather than a project workspace:

- `--global` is **strictly symmetric** across all commands: `install`, `update`, `uninstall`, `status`, and `doctor`.
- Global state is isolated under the OS application data directory: `KyberWeave/squad/roots/<root-key>/`, where `<root-key>` is the SHA-256 hash of the canonical physical root path.

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

Run diagnostic checks on renderer coverage (which of the ten approved targets can install today) and the Kyber-Weave MCP server:

```bash
kyber-weave squad doctor
```

### Uninstalling

Remove all managed files and deployment state:

```bash
kyber-weave squad uninstall
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

---

## Exit Codes

| Exit Code | Meaning |
|---|---|
| `0` | Success, healthy/clean status, or successful dry run. |
| `1` | Configuration error, prerequisite failure, file drift, dirty state, or transactional failure. |
| `2` | Invalid CLI arguments, unrecognized target/format token, or target resolution required in non-interactive mode. |

---

## Related

- [Kyber-Squad architecture](architecture.md) — transaction engine, AgentIR, lowering, and state model
- [Requirements and degradation contract](requirements.md) — KS-001 through KS-008 specifications
- [Configuration](../configuration.md) — configuring squad settings in `.kyber-weave/kyber-weave.yml`
- [Distribution and release flow](../distribution.md) — release packaging and artifact verification
