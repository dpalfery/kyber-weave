---
id: kyber-squad-index
title: Kyber-Squad — Multi-Harness Agent & Skill Deployment Control Plane
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-10-04
---

# Kyber-Squad — Multi-Harness Agent & Skill Deployment Control Plane

> **Govern a twelve-target IDE harness catalog and deploy canonical AI agent squads and skills to all twelve targets implemented today, with transactional safety.**

Engineering teams increasingly operate across heterogeneous AI development tools—some engineers build in Cursor or Windsurf, others in Claude Code, GitHub Copilot, Cline, Pi, or Antigravity. As teams author specialized agent personas (e.g. architects, database engineers, test specialists) and reusable skills, keeping these artifacts in sync across differing IDE configurations becomes an unmanageable maintenance burden.

**Kyber-Squad** is the unified deployment control plane that compiles canonical agent definitions
(`AgentIR`) and skill specifications into target-native configurations, backed by atomic
transactional rollback. The catalog declares twelve harness targets. All twelve renderers are implemented
and registered today: `copilot`, `cursor`, `claude`, `codex`, `antigravity`, `opencode`, `kilo`, `pi`, `factory`, `warp`, `zcode`, and `devin`.

---

## Why Kyber-Squad?

Deploying multi-agent workflows across modern engineering environments breaks down in three key ways:

### 1. The Multi-Harness Fragmentation Tax
Every coding harness uses its own configuration format, folder layout, and prompt syntax (`.cursorrules`, `.claude/agents`, `.github/copilot-instructions.md`, TOML, JSON). Manually duplicating 21 specialized agent roles and 24 skills across multiple tools guarantees silent configuration drift, outdated prompts, and inconsistent behaviors across developers.

### 2. Differing Capability Boundaries & Tool Permissions
Harnesses have wildly different capabilities: some support restricted subagent spawning or granular MCP permissions; others allow only flat prompt injection. Without a formalized capability lattice, agents fail unexpectedly or gain unintended permissions when deployed to less restrictive harnesses.

### 3. High-Risk In-Place Updates Without Rollback
Modifying local developer environments or repository-level agent configurations in place without state tracking can corrupt workspace settings, overwrite custom developer tweaks, or leave broken partial installs when network/parsing errors occur.

---

## Core Capabilities

| Capability | How It Solves the Problem | Command |
|---|---|---|
| **Canonical AgentIR Compilation** | Compiles 21 canonical agents and 24 skills — each projecting its validated resource closure beside the rendered principal — for all twelve registered renderers while retaining a governed twelve-target catalog. | `kyber-weave squad install` |
| **Transactional Engine & Atomic Rollback** | Creates pre-execution rollback manifests and tracks deployed files in `.kyber-weave/squad.receipt.json` and `squad.lock.yml`—restores clean state on any failure. | `kyber-weave squad install` · `uninstall` |
| **Capability Lattice & Degradation** | Intelligently maps subagent hierarchies, permissions, and tool access to each harness's exact feature set, emitting structured degradation warnings when a feature is unsupported. | `kyber-weave squad doctor` |
| **Distributed Concurrency Leases** | Uses cross-process mutex leasing to ensure concurrent CI jobs or IDE instances cannot corrupt deployment state. | Integrated in all `squad` verbs |
| **Portable Offline Packaging** | Bundles all canonical agents, skills, resources, and schemas into self-contained APM and Agent Plugins archives for air-gapped or CI distribution. | `kyber-weave squad pack` |

---

## Canonical, packaged, and rendered skill surfaces

The canonical product contains 24 `SKILL.md` files. `code-review-loop` is authored in Kyber-Squad;
every imported skill except the six explicitly evolved skills (`bug-crusher`, `code-review`,
`product-owner`, `second-brain`, `create-pull-request`, and `pr-review-fix-comments`) matches
the designated Hotshot golden copy byte for byte. It also retains 68 supplemental references, scripts, provider
instructions, and metadata files, for 92 files under
`products/kyber-squad/skills/`. Recursive APM and Agent Plugins packages carry all 92 files and
preserve each retained local reference.

The [code-review-loop skill](../../products/kyber-squad/skills/code-review-loop/SKILL.md)
handles an authorized review cycle on the chosen platform: fix feedback, validate and
push changes, link fix commits in replies, resolve addressed threads, and request
another review. The existing
`pr-review-fix-comments` skill supports step-by-step approvals.

A fresh GitHub Copilot render projects each owner's linked resources beside its principal:
21 `.github/agents/<name>.agent.md` files, 24 `.github/skills/<name>/SKILL.md` files, and the
linked resources, 122 files total, with authored relative links resolving in the output. Every
skill resource reaches every render except `skills/setup-dev-environment/agents/openai.yaml`,
which is packaged-only Codex skill-UI metadata. Every retained resource now has a reviewed
disposition in the [skill-resource dispositions audit](skill-resource-dispositions.md):
non-policy content stays in its skill directory as its durable home, portable policy lives in the
`products/kyber-squad/standards/` templates, and nothing was deleted. Generated `.github` output
remains a deployment artifact rather than canonical product source.

`products/kyber-squad/` is the canonical and package authority. The repository root
`.github/agents/` and `.github/skills/` trees are an intentional stale Copilot self-deployment;
their tracked `.kyber-weave/squad.lock.yml` and `.kyber-weave/squad.receipt.json` state is stale
with them. Those four root paths are outside this synchronization, remain untouched, and will be
refreshed by a human after a fresh Kyber-Weave release candidate exists.

---

## Jump In

Explore the full Kyber-Squad documentation suite:

* **[Adoption & Usage Guide](onboarding.md)** — Installing, updating, scoping (`--global`), targeting specific harnesses, and running health checks.
* **[Architecture](architecture.md)** — AgentIR intermediate representation, role-skill lowering pipeline, capability lattice, state store, transaction engine, and conductor execution circuit-breaker.
* **[Requirements & Degradation Matrix](requirements.md)** — Detailed KS-001 through KS-008 specifications, harness feature matrices, degradation taxonomy, and the circuit-breaker harvest.
* **[Skill-Resource Dispositions](skill-resource-dispositions.md)** — The content-preservation audit and policy-line ledger for every retained skill resource.
