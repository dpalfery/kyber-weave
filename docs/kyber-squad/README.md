---
id: kyber-squad-index
title: Kyber-Squad — Multi-Harness Agent & Skill Deployment Control Plane
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-15
---

# Kyber-Squad — Multi-Harness Agent & Skill Deployment Control Plane

> **Deploy, synchronize, and govern canonical AI agent squads and skills across 10 IDE coding harnesses with transactional safety.**

Engineering teams increasingly operate across heterogeneous AI development tools—some engineers build in Cursor or Windsurf, others in Claude Code, GitHub Copilot, Cline, or Antigravity. As teams author specialized agent personas (e.g. architects, database engineers, test specialists) and reusable skills, keeping these artifacts in sync across differing IDE configurations becomes an unmanageable maintenance burden.

**Kyber-Squad** is the unified deployment control plane that compiles canonical agent definitions (`AgentIR`) and skill specifications into target-native configurations for 10 coding harnesses, backed by atomic transactional rollback.

---

## Why Kyber-Squad?

Deploying multi-agent workflows across modern engineering environments breaks down in three key ways:

### 1. The Multi-Harness Fragmentation Tax
Every coding harness uses its own configuration format, folder layout, and prompt syntax (`.cursorrules`, `.claude/agents`, `.github/copilot-instructions.md`, TOML, JSON). Manually duplicating 20 specialized agent roles and 25 skills across multiple tools guarantees silent configuration drift, outdated prompts, and inconsistent behaviors across developers.

### 2. Differing Capability Boundaries & Tool Permissions
Harnesses have wildly different capabilities: some support restricted subagent spawning or granular MCP permissions; others allow only flat prompt injection. Without a formalized capability lattice, agents fail unexpectedly or gain unintended permissions when deployed to less restrictive harnesses.

### 3. High-Risk In-Place Updates Without Rollback
Modifying local developer environments or repository-level agent configurations in place without state tracking can corrupt workspace settings, overwrite custom developer tweaks, or leave broken partial installs when network/parsing errors occur.

---

## Core Capabilities

| Capability | How It Solves the Problem | Command |
|---|---|---|
| **Canonical AgentIR Compilation** | Single source of truth: compiles 20 canonical agents and 25 skills to 10 native harness targets without manual reformatting. | `kyber-weave squad install` |
| **Transactional Engine & Atomic Rollback** | Creates pre-execution rollback manifests and tracks deployed files in `.kyber-weave/squad.receipt.json` and `squad.lock.yml`—restores clean state on any failure. | `kyber-weave squad install` · `uninstall` |
| **Capability Lattice & Degradation** | Intelligently maps subagent hierarchies, permissions, and tool access to each harness's exact feature set, emitting structured degradation warnings when a feature is unsupported. | `kyber-weave squad doctor` |
| **Distributed Concurrency Leases** | Uses cross-process mutex leasing to ensure concurrent CI jobs or IDE instances cannot corrupt deployment state. | Integrated in all `squad` verbs |
| **Portable Offline Packaging** | Bundles all canonical agent manifests, skills, and schemas into a self-contained archive for air-gapped or CI distribution. | `kyber-weave squad pack` |

---

## Jump In

Explore the full Kyber-Squad documentation suite:

* **[Adoption & Usage Guide](onboarding.md)** — Installing, updating, scoping (`--global`), targeting specific harnesses, and running health checks.
* **[Architecture](architecture.md)** — AgentIR intermediate representation, role-skill lowering pipeline, capability lattice, state store, and transaction engine.
* **[Requirements & Degradation Matrix](requirements.md)** — Detailed KS-001 through KS-008 specifications, harness feature matrices, and degradation taxonomy.
