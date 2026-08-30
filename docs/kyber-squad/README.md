---
id: kyber-squad-index
title: Kyber-Squad — Multi-Harness Agent & Skill Deployment Control Plane
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-30
---

# Kyber-Squad — Multi-Harness Agent & Skill Deployment Control Plane

> **Govern a nine-target IDE harness catalog and deploy canonical AI agent squads and skills to the five targets implemented today, with transactional safety.**

Engineering teams increasingly operate across heterogeneous AI development tools—some engineers build in Cursor or Windsurf, others in Claude Code, GitHub Copilot, Cline, or Antigravity. As teams author specialized agent personas (e.g. architects, database engineers, test specialists) and reusable skills, keeping these artifacts in sync across differing IDE configurations becomes an unmanageable maintenance burden.

**Kyber-Squad** is the unified deployment control plane that compiles canonical agent definitions
(`AgentIR`) and skill specifications into target-native configurations, backed by atomic
transactional rollback. The catalog declares nine harness targets. Five renderers are implemented
and registered today: `copilot`, `cursor`, `claude`, `codex`, and `antigravity`. The other four
declared targets—`opencode`, `kilo`, `warp`, and `factory`—fail renderer-coverage preflight.

---

## Why Kyber-Squad?

Deploying multi-agent workflows across modern engineering environments breaks down in three key ways:

### 1. The Multi-Harness Fragmentation Tax
Every coding harness uses its own configuration format, folder layout, and prompt syntax (`.cursorrules`, `.claude/agents`, `.github/copilot-instructions.md`, TOML, JSON). Manually duplicating 24 specialized agent roles and 24 skills across multiple tools guarantees silent configuration drift, outdated prompts, and inconsistent behaviors across developers.

### 2. Differing Capability Boundaries & Tool Permissions
Harnesses have wildly different capabilities: some support restricted subagent spawning or granular MCP permissions; others allow only flat prompt injection. Without a formalized capability lattice, agents fail unexpectedly or gain unintended permissions when deployed to less restrictive harnesses.

### 3. High-Risk In-Place Updates Without Rollback
Modifying local developer environments or repository-level agent configurations in place without state tracking can corrupt workspace settings, overwrite custom developer tweaks, or leave broken partial installs when network/parsing errors occur.

---

## Core Capabilities

| Capability | How It Solves the Problem | Command |
|---|---|---|
| **Canonical AgentIR Compilation** | Compiles 24 canonical agents and 24 skills for five registered renderers while retaining a governed nine-target catalog. | `kyber-weave squad install` |
| **Transactional Engine & Atomic Rollback** | Creates pre-execution rollback manifests and tracks deployed files in `.kyber-weave/squad.receipt.json` and `squad.lock.yml`—restores clean state on any failure. | `kyber-weave squad install` · `uninstall` |
| **Capability Lattice & Degradation** | Intelligently maps subagent hierarchies, permissions, and tool access to each harness's exact feature set, emitting structured degradation warnings when a feature is unsupported. | `kyber-weave squad doctor` |
| **Distributed Concurrency Leases** | Uses cross-process mutex leasing to ensure concurrent CI jobs or IDE instances cannot corrupt deployment state. | Integrated in all `squad` verbs |
| **Portable Offline Packaging** | Bundles all canonical agent manifests, skills, and schemas into a self-contained archive for air-gapped or CI distribution. | `kyber-weave squad pack` |

---

## Canonical, packaged, and rendered skill surfaces

The canonical product contains 24 `SKILL.md` files whose raw bytes match the designated Hotshot
golden copy. It also retains 64 supplemental references, scripts, provider instructions, and
metadata files, for 88 files under `products/kyber-squad/skills/`. Recursive APM and Agent Plugins
packages carry all 88 files and preserve each retained local reference.

The GitHub Copilot renderer has a narrower golden-parity boundary: exactly 24
`.github/agents/<name>.agent.md` files and 24 `.github/skills/<name>/SKILL.md` files, 48 files
total. It emits no supplemental resources. The Hotshot golden tree therefore
has a known dangling-reference defect for 61 resource links; Kyber-Squad preserves the missing
knowledge in canonical source and packages until the
[skill-resource migration todo](../todo/migrate-skill-resources-into-standards.md) is accepted and
verified. Generated `.github` output remains a deployment artifact rather than canonical product
source.

`products/kyber-squad/` is the canonical and package authority. The repository root
`.github/agents/` and `.github/skills/` trees are an intentional stale Copilot self-deployment;
their tracked `.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json` state is stale
with them. Those four root paths are outside this synchronization, remain untouched, and will be
refreshed by a human after a fresh Kyber-Weave release candidate exists.

---

## Jump In

Explore the full Kyber-Squad documentation suite:

* **[Adoption & Usage Guide](onboarding.md)** — Installing, updating, scoping (`--global`), targeting specific harnesses, and running health checks.
* **[Architecture](architecture.md)** — AgentIR intermediate representation, role-skill lowering pipeline, capability lattice, state store, and transaction engine.
* **[Requirements & Degradation Matrix](requirements.md)** — Detailed KS-001 through KS-008 specifications, harness feature matrices, and degradation taxonomy.
